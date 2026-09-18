package dev.eveintel.parse

import dev.eveintel.model.CountModifier
import dev.eveintel.model.IntelMessage
import dev.eveintel.model.Keyword
import dev.eveintel.model.Token
import dev.eveintel.universe.SolarSystem
import dev.eveintel.universe.Universe

/**
 * Turns an intel channel line into typed tokens.
 *
 * The key observation from real channel traffic is that **two or more spaces separate entities**:
 *
 * ```
 * 5-9L3H*  Halvard  Petra Vance  KrenV  Exequror Navy Issue* 3  Exequror* 1
 * ```
 *
 * So the parser works segment by segment, trying to match each segment as a whole before falling
 * back to scanning inside it. That single convention removes most of the ambiguity that would
 * otherwise need a character-name lookup.
 *
 * @param scopeRegionIds regions the channel covers. Abbreviations like `9UY` are resolved against
 *   these first, which is what makes short forms unambiguous. Empty means "the whole universe".
 */
class IntelParser(
    private val universe: Universe,
    private val scopeRegionIds: Set<Int> = emptySet(),
) {

    private val shipsByLowerName: Map<String, Int> =
        universe.ships.associate { it.n.lowercase() to it.i }

    private val maxShipWords: Int =
        universe.ships.maxOfOrNull { it.n.count { ch -> ch == ' ' } + 1 } ?: 1

    /** Systems eligible for abbreviation matching, longest names first so prefixes stay stable. */
    private val scopedSystems: List<SolarSystem> =
        (if (scopeRegionIds.isEmpty()) universe.systems else universe.systemsInRegions(scopeRegionIds))
            .sortedBy { it.name.length }

    private val maxKeywordWords: Int =
        Vocabulary.KEYWORDS.keys.maxOfOrNull { it.count { ch -> ch == ' ' } + 1 } ?: 1

    fun parse(
        channel: String,
        raw: ChatLogFormat.RawMessage,
    ): IntelMessage = IntelMessage(
        id = messageId(raw),
        channel = channel,
        author = raw.author,
        timestampMillis = raw.timestampMillis,
        raw = raw.message,
        tokens = tokenise(raw.message),
    )

    fun tokenise(message: String): List<Token> {
        val tokens = mutableListOf<Token>()
        for (segment in message.split(SEGMENT_SPLIT)) {
            val trimmed = segment.trim()
            if (trimmed.isEmpty()) continue
            parseSegment(trimmed, tokens)
        }
        return attachCountsToShips(tokens)
    }

    private fun parseSegment(segment: String, out: MutableList<Token>) {
        // A segment is usually exactly one entity, so try the whole thing first.
        matchEntity(segment)?.let { out.add(it); return }

        // A dragged/targeted ship link ("Nhar (Stork)*") names a pilot and their hull in one
        // segment. Handled before the generic word loop below, which would otherwise split it into
        // "Nhar" (an unmatched word) and "(Stork)*" (a ship) -- and since the same pilot is usually
        // *also* linked bare elsewhere in the line, that leftover "Nhar" became a second, phantom
        // Player token: the same person shown twice in the feed.
        matchPlayerWithShip(segment)?.let { out.addAll(it); return }

        val words = segment.split(' ').filter { it.isNotBlank() }
        var i = 0
        val pendingName = mutableListOf<String>()

        fun flushName() {
            if (pendingName.isEmpty()) return
            val joined = pendingName.joinToString(" ")
            out.add(
                if (looksLikeCharacterName(joined)) {
                    Token.Player(text = joined, linked = pendingName.any { it.endsWith(LINK_MARKER) })
                } else {
                    Token.Word(joined)
                },
            )
            pendingName.clear()
        }

        while (i < words.size) {
            var matched = false
            val maxWindow = minOf(maxOf(maxShipWords, maxKeywordWords), words.size - i)
            for (n in maxWindow downTo 1) {
                val phrase = words.subList(i, i + n).joinToString(" ")
                val token = matchEntity(phrase) ?: continue
                flushName()
                out.add(token)
                i += n
                matched = true
                break
            }
            if (!matched) {
                pendingName.add(words[i])
                i++
            }
        }
        flushName()
    }

    /** Attempts to classify [phrase] as a single entity. Returns null if nothing fits. */
    private fun matchEntity(phrase: String): Token? {
        val raw = phrase.trim()
        if (raw.isEmpty()) return null

        // Questions are matched before punctuation is stripped, so `status?` (a request) stays
        // distinct from `status` (a report).
        Vocabulary.QUESTIONS[raw.lowercase()]?.let { return Token.Question(text = raw, kind = it) }

        val stripped = raw.trim(*TRIM_CHARS)
        if (stripped.isEmpty()) return null

        val linked = stripped.endsWith(LINK_MARKER)
        val core = stripped.removeSuffix(LINK_MARKER).trim(*TRIM_CHARS)
        if (core.isEmpty()) return null
        val lower = core.lowercase()

        if (URL.matches(core)) return Token.Url(core)

        Vocabulary.KEYWORDS[lower]?.let { return Token.Kw(text = core, keyword = it) }

        matchShip(lower)?.let { (typeId, name) ->
            return Token.Ship(text = core, typeId = typeId, name = name, linked = linked)
        }

        matchSystem(core)?.let {
            return Token.System(text = core, systemId = it.id, name = it.name, linked = linked)
        }

        matchCount(core)?.let { return it }

        return null
    }

    private fun matchShip(lower: String): Pair<Int?, String>? {
        shipsByLowerName[lower]?.let { return it to canonicalShipName(lower) }
        Vocabulary.SHIP_ALIASES[lower]?.let { alias ->
            val id = shipsByLowerName[alias.lowercase()]
            return id to alias
        }
        // "caracal navy", "stabber fleet": nobody types the trailing "Issue". A rule beats listing
        // them, because the SDE carries dozens of faction hulls and a hand-written list would rot.
        for (suffix in ISSUE_SUFFIXES) {
            if (!lower.endsWith(suffix)) continue
            val full = "$lower issue"
            shipsByLowerName[full]?.let { return it to canonicalShipName(full) }
        }
        // Plural hull report ("2 sabres", "caracal navy issues"): strip a trailing "s" and retry
        // once, rather than listing every hull's plural by hand.
        if (lower.endsWith("s") && lower.length > 1) {
            val singular = lower.dropLast(1)
            shipsByLowerName[singular]?.let { return it to canonicalShipName(singular) }
            Vocabulary.SHIP_ALIASES[singular]?.let { alias ->
                val id = shipsByLowerName[alias.lowercase()]
                return id to alias
            }
        }

        matchChineseShip(lower)?.let { return it }

        return null
    }

    /** `Nhar (Stork)*` -> a pilot token plus the ship token they're currently flying. */
    private fun matchPlayerWithShip(segment: String): List<Token>? {
        val match = PLAYER_SHIP.matchEntire(segment) ?: return null
        val namePart = match.groupValues[1].trim()
        val shipPart = match.groupValues[2].trim()
        val linked = match.groupValues[3] == LINK_MARKER
        if (!looksLikeCharacterName(namePart)) return null
        val (typeId, shipName) = matchShip(shipPart.lowercase()) ?: return null
        return listOf(
            Token.Player(text = namePart, linked = linked),
            Token.Ship(text = shipPart, typeId = typeId, name = shipName, linked = linked),
        )
    }

    /** Chinese-client hull report ("洛基级", "狞獾级海军型"). See [Vocabulary.SHIP_ALIASES_ZH]. */
    private fun matchChineseShip(text: String): Pair<Int?, String>? {
        Vocabulary.SHIP_ALIASES_ZH[text]?.let { base ->
            return shipsByLowerName[base.lowercase()] to base
        }
        for ((zhSuffix, enSuffix) in Vocabulary.ZH_ISSUE_SUFFIXES) {
            if (!text.endsWith(zhSuffix)) continue
            val base = Vocabulary.SHIP_ALIASES_ZH["${text.removeSuffix(zhSuffix)}级"] ?: continue
            val full = "$base $enSuffix"
            shipsByLowerName[full.lowercase()]?.let { return it to full }
        }
        return null
    }

    private fun canonicalShipName(lower: String): String =
        universe.ships.firstOrNull { it.n.lowercase() == lower }?.n ?: lower

    /**
     * Resolves a system name or abbreviation.
     *
     * Exact names match anywhere in the universe. Abbreviations only resolve when the token contains
     * a digit or a hyphen — nullsec names always do, and that single rule keeps ordinary English
     * words from turning into systems.
     */
    fun matchSystem(text: String): SolarSystem? {
        val core = text.trim().trim(*TRIM_CHARS).removeSuffix(LINK_MARKER).trim(*TRIM_CHARS)
        if (core.isEmpty() || core.lowercase() in Vocabulary.SYSTEM_STOPWORDS) return null

        universe.systemByName(core)?.let { return it }

        val hasDigit = core.any { it.isDigit() }
        val hasHyphen = core.contains('-')
        if (!hasDigit && !hasHyphen) return null
        if (core.length < MIN_ABBREVIATION) return null

        val needle = core.uppercase().filter { it.isLetterOrDigit() }
        if (needle.isEmpty()) return null

        val candidates = scopedSystems.filter { system ->
            system.name.uppercase().filter { it.isLetterOrDigit() }.startsWith(needle)
        }
        if (candidates.size == 1) return candidates.single()
        if (candidates.isEmpty() && scopeRegionIds.isNotEmpty()) {
            // Nothing in the channel's regions — fall back to the whole universe, but only when
            // the answer is unambiguous.
            val wide = universe.systems.filter { system ->
                system.name.uppercase().filter { it.isLetterOrDigit() }.startsWith(needle)
            }
            return wide.singleOrNull()
        }
        return null
    }

    private fun matchCount(core: String): Token.Count? {
        COUNT_PLUS.matchEntire(core)?.let {
            return Token.Count(core, it.groupValues[1].toInt(), CountModifier.PLUS)
        }
        COUNT_TOTAL.matchEntire(core)?.let {
            val digits = it.groupValues.drop(1).firstOrNull { g -> g.isNotEmpty() } ?: return null
            return Token.Count(core, digits.toInt(), CountModifier.TOTAL)
        }
        COUNT_TIMES.matchEntire(core)?.let {
            val digits = it.groupValues.drop(1).firstOrNull { g -> g.isNotEmpty() } ?: return null
            return Token.Count(core, digits.toInt(), CountModifier.TIMES)
        }
        COUNT_BARE.matchEntire(core)?.let {
            return Token.Count(core, core.toInt(), CountModifier.EXACT)
        }
        return null
    }

    /**
     * `Exequror Navy Issue* 3` means three of them. Folds a count that directly follows a ship into
     * that ship, leaving standalone counts (`4=  exeq navy`) alone for the UI to interpret.
     */
    private fun attachCountsToShips(tokens: List<Token>): List<Token> {
        val out = mutableListOf<Token>()
        var i = 0
        while (i < tokens.size) {
            val current = tokens[i]
            val next = tokens.getOrNull(i + 1)
            if (current is Token.Ship && next is Token.Count && current.count == null) {
                out.add(current.copy(count = next.value))
                i += 2
            } else {
                out.add(current)
                i++
            }
        }
        return out
    }

    /** EVE character names: one to three capitalised words, letters plus `' - .` */
    private fun looksLikeCharacterName(text: String): Boolean {
        val words = text.removeSuffix(LINK_MARKER).split(' ').filter { it.isNotBlank() }
        if (words.isEmpty() || words.size > 3) return false
        return words.all { word ->
            val clean = word.trim(*TRIM_CHARS).removeSuffix(LINK_MARKER)
            if (clean.isEmpty() || !clean.all { it.isLetterOrDigit() || it in "'-." }) return@all false
            // Ordinary title-cased pilot name, or a stylised handle with a digit swapped in for a
            // letter ("dedmustd1e") -- real chatter never mixes letters and digits like that, and a
            // bare number is already claimed by Token.Count before a word ever reaches here.
            clean.first().isUpperCase() || clean.any { it.isDigit() }
        }
    }

    companion object {
        private const val LINK_MARKER = "*"

        /** Faction hull suffixes people shorten by dropping the trailing "Issue". */
        private val ISSUE_SUFFIXES = listOf(" navy", " fleet")
        private const val MIN_ABBREVIATION = 3
        private val TRIM_CHARS = charArrayOf(',', '.', '!', '?', ':', ';', '(', ')', '[', ']', '"', '\'', ' ')
        private val PLAYER_SHIP = Regex("""^(.+?)\s+\((.+)\)(\*?)$""")
        private val SEGMENT_SPLIT = Regex("""\s{2,}""")
        private val URL = Regex("""https?://\S+""")
        private val COUNT_PLUS = Regex("""\+\s?(\d{1,4})""")
        private val COUNT_TOTAL = Regex("""(?:(\d{1,4})=|=(\d{1,4}))""")
        private val COUNT_TIMES = Regex("""(?:(\d{1,4})[xX]|[xX](\d{1,4}))""")
        private val COUNT_BARE = Regex("""\d{1,4}""")
    }
}

/**
 * Stable id for a message, so the same line seen in several characters' logs is shown once.
 * FNV-1a keeps this in common code with no platform hashing dependency.
 */
fun messageId(raw: ChatLogFormat.RawMessage): String {
    val input = "${raw.timestampMillis}|${raw.author}|${raw.message}"
    var hash = FNV_OFFSET
    for (char in input) {
        hash = hash xor char.code.toLong()
        hash *= FNV_PRIME
    }
    return hash.toULong().toString(16).padStart(16, '0')
}

private const val FNV_OFFSET = -3750763034362895579L // 14695981039346656037 unsigned
private const val FNV_PRIME = 1099511628211L

/**
 * True if the line carries something worth showing on an intel display.
 *
 * A named system is what separates a report from conversation. `Nora DarkStar its my anathema`
 * mentions a ship but is chatter; `1GH-48 clr` names a system and is not. The exception is a short
 * question or keyword-only line, which is a follow-up to the system named in the line before.
 */
fun IntelMessage.isIntel(): Boolean {
    if (author == ChatLogFormat.EVE_SYSTEM_AUTHOR) return false
    if (systemIds.isNotEmpty()) return true
    val unmatchedWords = tokens.count { it is Token.Word }
    return unmatchedWords == 0 && (questions.isNotEmpty() || keywords.isNotEmpty())
}

/**
 * Intel that implies the system is currently hostile, rather than a "clear" report.
 *
 * A named ship, pilot or count is the usual evidence. But a line can carry none of those and still
 * be the most urgent thing in the channel -- `<system> camped`, `<system> spiked` -- so the
 * hostile keywords count too. Those lines previously reached the feed and then never alerted.
 */
fun IntelMessage.isHostile(): Boolean {
    if (Keyword.CLEAR in keywords) return false
    if (ships.isNotEmpty() || players.isNotEmpty() || reportedCount != null) return true
    return keywords.any { it in Vocabulary.HOSTILE_KEYWORDS }
}
