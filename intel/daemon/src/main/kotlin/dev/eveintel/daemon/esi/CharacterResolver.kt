package dev.eveintel.daemon.esi

import dev.eveintel.wire.CharacterInfo
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.nio.file.Files
import java.nio.file.Path

@Serializable
private data class IdsResponse(val characters: List<NamedId> = emptyList())

@Serializable
private data class NamedId(val id: Long, val name: String)

@Serializable
private data class Affiliation(
    val character_id: Long,
    val corporation_id: Long? = null,
    val alliance_id: Long? = null,
)

/**
 * Turns character names scraped from chat into portraits and corp/alliance tickers.
 *
 * Names arrive continuously and repeat heavily, so lookups are batched and cached permanently on
 * disk. A name that fails to resolve is remembered as a failure too — intel channels contain plenty
 * of typos and nicknames, and retrying them forever would be pointless traffic.
 */
class CharacterResolver(
    private val esi: EsiClient,
    private val cacheFile: Path,
) {
    private val json = Json { ignoreUnknownKeys = true; prettyPrint = false }

    private val resolved = mutableMapOf<String, CharacterInfo>()
    private val unresolvable = mutableSetOf<String>()
    private val queue = Channel<String>(Channel.UNLIMITED)
    private val pending = mutableSetOf<String>()

    private val _updates = MutableSharedFlow<List<CharacterInfo>>(extraBufferCapacity = 64)
    val updates: SharedFlow<List<CharacterInfo>> = _updates.asSharedFlow()

    fun known(): List<CharacterInfo> = synchronized(resolved) { resolved.values.toList() }

    /** Queues any names not already known. Safe to call for every message. */
    fun submit(names: Collection<String>) {
        synchronized(resolved) {
            names.forEach { name ->
                if (name in resolved || name in unresolvable || name in pending) return@forEach
                if (!looksResolvable(name)) return@forEach
                pending.add(name)
                queue.trySend(name)
            }
        }
    }

    fun start(scope: CoroutineScope) {
        load()
        scope.launch(Dispatchers.IO) {
            while (isActive) {
                val batch = drainBatch()
                if (batch.isEmpty()) {
                    delay(BATCH_IDLE_MS)
                    continue
                }
                runCatching { resolveBatch(batch) }
                    .onFailure { System.err.println("ESI resolve failed: ${it.message}") }
                delay(BATCH_INTERVAL_MS)
            }
        }
    }

    private suspend fun drainBatch(): List<String> {
        val batch = mutableListOf<String>()
        while (batch.size < BATCH_SIZE) {
            val next = queue.tryReceive().getOrNull() ?: break
            batch.add(next)
        }
        return batch
    }

    private suspend fun resolveBatch(names: List<String>) {
        val ids = esi.post<IdsResponse>("/universe/ids/", names)?.characters.orEmpty()
        val byName = ids.associateBy { it.name }

        val missing = names.filter { it !in byName }
        synchronized(resolved) {
            unresolvable.addAll(missing)
            missing.forEach { pending.remove(it) }
        }
        if (ids.isEmpty()) return

        val affiliations = esi.post<List<Affiliation>>("/characters/affiliation/", ids.map { it.id })
            .orEmpty()
            .associateBy { it.character_id }

        // One names call covers every corp and alliance in the batch.
        val orgIds = affiliations.values
            .flatMap { listOfNotNull(it.corporation_id, it.alliance_id) }
            .distinct()
        val orgNames = if (orgIds.isEmpty()) {
            emptyMap()
        } else {
            esi.post<List<UniverseName>>("/universe/names/", orgIds).orEmpty().associateBy { it.id }
        }
        val tickers = fetchTickers(affiliations.values)

        val infos = ids.map { named ->
            val affiliation = affiliations[named.id]
            CharacterInfo(
                name = named.name,
                characterId = named.id,
                corporationId = affiliation?.corporation_id,
                corporationName = affiliation?.corporation_id?.let { orgNames[it]?.name },
                corporationTicker = affiliation?.corporation_id?.let { tickers[it] },
                allianceId = affiliation?.alliance_id,
                allianceName = affiliation?.alliance_id?.let { orgNames[it]?.name },
                allianceTicker = affiliation?.alliance_id?.let { tickers[it] },
            )
        }

        synchronized(resolved) {
            infos.forEach {
                resolved[it.name] = it
                pending.remove(it.name)
            }
        }
        save()
        _updates.emit(infos)
    }

    /** Tickers need a per-entity call; only alliances are worth it for a compact display. */
    private suspend fun fetchTickers(affiliations: Collection<Affiliation>): Map<Long, String> {
        val allianceIds = affiliations.mapNotNull { it.alliance_id }.distinct()
        val result = mutableMapOf<Long, String>()
        for (id in allianceIds.take(MAX_TICKER_LOOKUPS)) {
            val alliance = esi.get<AllianceDetail>("/alliances/$id/") ?: continue
            alliance.ticker?.let { result[id] = it }
        }
        return result
    }

    /**
     * Cheap pre-filter. EVE names are 3-37 characters of letters, digits, space, apostrophe and
     * hyphen; anything else came from the parser misreading chatter and is not worth an API call.
     */
    private fun looksResolvable(name: String): Boolean {
        if (name.length !in 3..37) return false
        return name.all { it.isLetterOrDigit() || it in " '-" }
    }

    private fun load() {
        if (!Files.isRegularFile(cacheFile)) return
        runCatching {
            val cache = json.decodeFromString(Cache.serializer(), Files.readString(cacheFile))
            synchronized(resolved) {
                cache.characters.forEach { resolved[it.name] = it }
                unresolvable.addAll(cache.unresolvable)
            }
            println("ESI cache: ${cache.characters.size} characters, ${cache.unresolvable.size} known-bad names")
        }.onFailure { System.err.println("could not read ESI cache: ${it.message}") }
    }

    private fun save() {
        runCatching {
            val snapshot = synchronized(resolved) {
                Cache(resolved.values.toList(), unresolvable.toList())
            }
            Files.createDirectories(cacheFile.parent)
            Files.writeString(cacheFile, json.encodeToString(Cache.serializer(), snapshot))
        }.onFailure { System.err.println("could not write ESI cache: ${it.message}") }
    }

    @Serializable
    private data class Cache(
        val characters: List<CharacterInfo> = emptyList(),
        val unresolvable: List<String> = emptyList(),
    )

    private companion object {
        const val BATCH_SIZE = 100
        const val BATCH_INTERVAL_MS = 1_500L
        const val BATCH_IDLE_MS = 1_000L
        const val MAX_TICKER_LOOKUPS = 20
    }
}
