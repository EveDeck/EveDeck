package dev.eveintel.daemon.esi

import dev.eveintel.wire.ServerMessage
import dev.eveintel.wire.SovHolder
import dev.eveintel.wire.SystemStats
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable

@Serializable
private data class SovEntry(
    val system_id: Int,
    val alliance_id: Long? = null,
    val faction_id: Long? = null,
    val corporation_id: Long? = null,
)

@Serializable
private data class KillsEntry(
    val system_id: Int,
    val ship_kills: Int = 0,
    val pod_kills: Int = 0,
    val npc_kills: Int = 0,
)

@Serializable
private data class JumpsEntry(val system_id: Int, val ship_jumps: Int = 0)

/**
 * Sovereignty and activity data for the map.
 *
 * Both ESI endpoints are cached by CCP for an hour, so polling faster would return identical bytes;
 * the refresh interval matches that deliberately.
 */
class UniverseStatusService(private val esi: EsiClient) {

    private val _sovereignty = MutableStateFlow(ServerMessage.Sovereignty())
    val sovereignty: StateFlow<ServerMessage.Sovereignty> = _sovereignty.asStateFlow()

    private val _stats = MutableStateFlow(ServerMessage.Stats())
    val stats: StateFlow<ServerMessage.Stats> = _stats.asStateFlow()

    fun start(scope: CoroutineScope) = scope.launch(Dispatchers.IO) {
        while (isActive) {
            runCatching { refreshSovereignty() }
                .onFailure { System.err.println("sov refresh failed: ${it.message}") }
            runCatching { refreshStats() }
                .onFailure { System.err.println("stats refresh failed: ${it.message}") }
            delay(REFRESH_MS)
        }
    }

    private suspend fun refreshSovereignty() {
        val entries = esi.get<List<SovEntry>>("/sovereignty/map/") ?: return

        // Alliance sov is what reads meaningfully on a nullsec map; faction sov marks empire space.
        val systems = mutableMapOf<Int, Long>()
        entries.forEach { entry ->
            val holder = entry.alliance_id ?: entry.faction_id
            if (holder != null) systems[entry.system_id] = holder
        }

        val holderIds = systems.values.distinct()
        val names = holderIds.chunked(NAME_CHUNK).flatMap { chunk ->
            esi.post<List<UniverseName>>("/universe/names/", chunk).orEmpty()
        }.associateBy { it.id }

        val holders = holderIds.map { id ->
            val name = names[id]
            SovHolder(
                id = id,
                name = name?.name ?: "Unknown",
                ticker = if (name?.category == "alliance") allianceTicker(id) else null,
                kind = name?.category ?: "alliance",
            )
        }

        _sovereignty.value = ServerMessage.Sovereignty(holders = holders, systems = systems)
        println("sovereignty: ${systems.size} systems, ${holders.size} holders")
    }

    /** Tickers are one call each, so only the largest holders get one. */
    private suspend fun allianceTicker(id: Long): String? =
        tickerCache.getOrPut(id) { esi.get<AllianceDetail>("/alliances/$id/")?.ticker ?: "" }
            .ifEmpty { null }

    private val tickerCache = mutableMapOf<Long, String>()

    private suspend fun refreshStats() {
        val kills = esi.get<List<KillsEntry>>("/universe/system_kills/").orEmpty()
        val jumps = esi.get<List<JumpsEntry>>("/universe/system_jumps/").orEmpty()
            .associate { it.system_id to it.ship_jumps }

        val combined = mutableMapOf<Int, SystemStats>()
        kills.forEach { entry ->
            combined[entry.system_id] = SystemStats(
                systemId = entry.system_id,
                shipKills = entry.ship_kills,
                podKills = entry.pod_kills,
                npcKills = entry.npc_kills,
                jumps = jumps[entry.system_id] ?: 0,
            )
        }
        jumps.forEach { (systemId, shipJumps) ->
            combined.getOrPut(systemId) { SystemStats(systemId = systemId, jumps = shipJumps) }
        }

        _stats.value = ServerMessage.Stats(combined.values.toList())
        println("activity: ${combined.size} systems with kills or jumps")
    }

    private companion object {
        const val REFRESH_MS = 60 * 60 * 1000L
        const val NAME_CHUNK = 900
    }
}
