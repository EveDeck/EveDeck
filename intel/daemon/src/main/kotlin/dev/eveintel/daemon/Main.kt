package dev.eveintel.daemon

import dev.eveintel.parse.ChatLogFormat
import dev.eveintel.parse.IntelParser
import dev.eveintel.parse.isHostile
import dev.eveintel.parse.isIntel
import dev.eveintel.universe.Universe
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.launch
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import kotlin.io.path.name

fun loadUniverse(): Universe {
    val stream = Universe::class.java.classLoader.getResourceAsStream("universe.json")
        ?: error("universe.json missing from the daemon classpath")
    return Universe.parse(stream.readBytes().toString(StandardCharsets.UTF_8))
}

fun main(args: Array<String>) {
    val configPath = args.indexOf("--config").takeIf { it >= 0 }?.let { Paths.get(args[it + 1]) }
        ?: Paths.get("eveintel.properties")
    val config = Config.load(configPath)

    if (args.contains("--validate")) {
        validate(config, args)
        return
    }

    val universe = loadUniverse()
    val scopeRegionIds = config.scopeRegionIds(universe)
    val parser = IntelParser(universe, scopeRegionIds)

    val selectedChannels = MutableStateFlow(config.intelChannels)
    val registry = ChannelRegistry(config.chatLogsDirectory, selectedChannels)
    val pipeline = IntelPipeline(universe, parser, selectedChannels)

    val cacheDirectory = configPath.toAbsolutePath().parent.resolve("cache")
    val esi = dev.eveintel.daemon.esi.EsiClient()
    val characters = dev.eveintel.daemon.esi.CharacterResolver(esi, cacheDirectory.resolve("characters.json"))
    val universeStatus = dev.eveintel.daemon.esi.UniverseStatusService(esi)
    val images = dev.eveintel.daemon.esi.ImageProxy(esi, cacheDirectory.resolve("images"))

    println("EVE Intel daemon")
    println("  chat logs:  ${config.chatLogsDirectory}")
    println("  channels:   ${config.intelChannels.joinToString().ifEmpty { "(none — pick them on the tablet)" }}")
    println("  scope:      ${config.scopeRegions.joinToString().ifEmpty { "whole universe" }}")

    runBlocking {
        registry.scan()
        println("  discovered: ${registry.available.value.joinToString { it.name }}")
        registry.start(this)

        val tailer = LogTailer(
            directory = config.chatLogsDirectory,
            // Local is always tailed: it is how character location is tracked.
            channels = { selectedChannels.value + ChannelRegistry.RESERVED },
            startFromEnd = config.startFromEnd,
            // Local is where character position comes from, and position is state, not history.
            alwaysFromStart = ChannelRegistry.RESERVED,
        )
        launch { tailer.messages.collect { pipeline.submit(it) } }

        // Count only, never names: this is the number that tells you whether the tablet will have
        // anything to measure the alert radius against.
        launch {
            pipeline.locations.collect { println("locations known: ${it.size} character(s)") }
        }

        // Every character named in intel gets queued for an ESI lookup; the resolver batches,
        // caches and ignores names it has already failed on.
        launch { pipeline.intel.collect { characters.submit(it.players) } }

        characters.start(this)
        universeStatus.start(this)
        tailer.start(this)
        IntelServer(
            config = config,
            configPath = configPath,
            pipeline = pipeline,
            channels = registry,
            scopeRegionIds = scopeRegionIds,
            characters = characters,
            universeStatus = universeStatus,
            images = images,
        ).start(this)
    }
}

/**
 * Replays historical logs through the parser and prints what it understood.
 *
 * This is how the vocabulary gets tuned: run it over your own channel history and look at what
 * lands in "unrecognised".
 */
private fun validate(config: Config, args: Array<String>) {
    val limit = args.indexOf("--limit").takeIf { it >= 0 }?.let { args[it + 1].toIntOrNull() } ?: 40
    val show = args.indexOf("--show").takeIf { it >= 0 }?.let { args[it + 1].toIntOrNull() } ?: 40
    val universe = loadUniverse()
    val scopeRegionIds = config.scopeRegionIds(universe)
    val parser = IntelParser(universe, scopeRegionIds)

    println("universe: ${universe.systems.size} systems, ${universe.ships.size} ships")
    println("scope:    ${config.scopeRegions.joinToString().ifEmpty { "whole universe" }} -> ${scopeRegionIds.size} region(s)")
    println("channels: ${config.intelChannels.joinToString()}")
    println()

    val files = Files.list(config.chatLogsDirectory).use { stream ->
        stream.toList()
            .filter { Files.isRegularFile(it) }
            .mapNotNull { path ->
                val parsed = ChatLogFormat.parseFileName(path.name) ?: return@mapNotNull null
                if (parsed.channel !in config.intelChannels) return@mapNotNull null
                path to parsed
            }
            .sortedByDescending { "${it.second.date}${it.second.time}" }
            .take(limit)
            .map { it.first }
    }
    println("replaying ${files.size} file(s)")

    var lines = 0
    var intel = 0
    var hostile = 0
    var hostileByKeywordAlone = 0
    val seen = mutableSetOf<String>()
    val unrecognised = mutableListOf<String>()
    val systemHits = mutableMapOf<String, Int>()
    val samples = mutableListOf<String>()

    for (file in files) {
        val text = Files.readAllBytes(file).toString(StandardCharsets.UTF_16LE)
        val channel = ChatLogFormat.parseFileName(file.name)?.channel ?: continue
        for (line in text.lines()) {
            val raw = ChatLogFormat.parseMessage(line) ?: continue
            if (raw.author == ChatLogFormat.EVE_SYSTEM_AUTHOR) continue
            if (!seen.add("${raw.timestampMillis}|${raw.author}|${raw.message}")) continue
            lines++
            val message = parser.parse(channel, raw)
            if (message.isIntel()) {
                intel++
                if (message.isHostile()) {
                    hostile++
                    // Lines with no ship, pilot or count: these alert only because a hostile
                    // keyword says so, and used to pass silently through to the feed.
                    val named = message.ships.isNotEmpty() ||
                        message.players.isNotEmpty() ||
                        message.reportedCount != null
                    if (!named) hostileByKeywordAlone++
                }
                message.systemIds.forEach { id ->
                    val name = universe.system(id)?.name ?: return@forEach
                    systemHits[name] = (systemHits[name] ?: 0) + 1
                }
                if (samples.size < 25) {
                    samples.add(
                        buildString {
                            append(raw.message.take(70).padEnd(72))
                            append(" -> ")
                            append(
                                message.tokens.joinToString(" ") { token ->
                                    when (token) {
                                        is dev.eveintel.model.Token.System -> "[sys ${token.name}]"
                                        is dev.eveintel.model.Token.Ship ->
                                            "[ship ${token.name}${token.count?.let { "x$it" } ?: ""}]"
                                        is dev.eveintel.model.Token.Player -> "[chr ${token.text}]"
                                        is dev.eveintel.model.Token.Kw -> "[kw ${token.keyword}]"
                                        is dev.eveintel.model.Token.Count -> "[n ${token.value}${token.modifier}]"
                                        is dev.eveintel.model.Token.Question -> "[? ${token.kind}]"
                                        is dev.eveintel.model.Token.Url -> "[url]"
                                        is dev.eveintel.model.Token.Word -> "?${token.text}"
                                    }
                                },
                            )
                        },
                    )
                }
            } else if (unrecognised.size < UNRECOGNISED_CAP) {
                unrecognised.add(raw.message.take(90))
            }
        }
    }

    println()
    println("=== PARSED $intel / $lines unique lines as intel (${percent(intel, lines)}) ===")
    println(
        "=== HOSTILE $hostile / $intel intel lines (${percent(hostile, intel)}), " +
            "$hostileByKeywordAlone on a keyword alone ===",
    )
    samples.forEach(::println)

    println()
    println("=== TOP SYSTEMS ===")
    systemHits.entries.sortedByDescending { it.value }.take(20)
        .forEach { println("  ${it.value.toString().padStart(5)}  ${it.key}") }

    println()
    println("=== UNCLASSIFIED WORD FREQUENCY ===")
    println("  ${unrecognised.size} unclassified line(s) scanned")
    println("  Lower-case words only: anything capitalised is a pilot or a system, and the point")
    println("  here is to find missing keywords and ship shorthand, not to list people.")
    wordFrequency(unrecognised).take(30).forEach { (word, count) ->
        println("  ${count.toString().padStart(5)}  $word")
    }

    println()
    println("=== NOT CLASSIFIED AS INTEL (first $show of ${unrecognised.size}) ===")
    unrecognised.take(show).forEach { println("  $it") }
}

/**
 * Lower-case word counts across the unclassified lines, commonest first.
 *
 * This is the signal worth acting on when growing `Vocabulary`. A list of raw lines shows whatever
 * happened to be logged first, which is mostly chatter; a frequency count surfaces the shorthand
 * that is actually costing coverage. Capitalised tokens are dropped rather than ranked, because
 * they are pilot and system names and listing them serves no tuning purpose.
 */
private fun wordFrequency(lines: List<String>): List<Pair<String, Int>> {
    val counts = mutableMapOf<String, Int>()
    val word = Regex("""[a-z][a-z'-]+""")
    for (line in lines) {
        for (match in word.findAll(line.lowercase())) {
            val text = match.value
            if (text.length < 2 || text in CHATTER) continue
            counts[text] = (counts[text] ?: 0) + 1
        }
    }
    return counts.entries.sortedByDescending { it.value }.map { it.key to it.value }
}

/** Ordinary English that will always top the count and says nothing about intel vocabulary. */
private val CHATTER = setOf(
    "the", "and", "for", "you", "are", "not", "but", "was", "his", "her", "its", "out", "get",
    "got", "can", "all", "any", "one", "two", "how", "who", "why", "did", "has", "had", "him",
    "she", "they", "them", "this", "that", "with", "have", "from", "just", "like", "what",
    "when", "will", "your", "yeah", "lol", "there", "here", "then", "than", "into", "over",
    "some", "were", "been", "does", "dont", "cant", "its", "it's", "im", "i'm",
)

private const val UNRECOGNISED_CAP = 20_000

private fun percent(part: Int, whole: Int): String =
    if (whole == 0) "0%" else "${(part * 100.0 / whole).toInt()}%"
