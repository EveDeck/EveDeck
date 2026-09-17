package dev.eveintel.android

import dev.eveintel.model.CharacterLocation
import dev.eveintel.model.IntelMessage
import dev.eveintel.universe.Universe
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow

enum class ConnectionState { DISCONNECTED, CONNECTING, CONNECTED }

/**
 * Process-wide intel state.
 *
 * The foreground service writes; the UI reads. A plain object rather than a DI graph — there is one
 * connection and one feed, and the service must keep filling it while no Activity exists.
 */
object IntelRepository {

    private val _connection = MutableStateFlow(ConnectionState.DISCONNECTED)
    val connection: StateFlow<ConnectionState> = _connection.asStateFlow()

    private val _messages = MutableStateFlow<List<IntelMessage>>(emptyList())

    /** Newest first. Capped so a long session cannot grow without bound. */
    val messages: StateFlow<List<IntelMessage>> = _messages.asStateFlow()

    private val _locations = MutableStateFlow<Map<String, CharacterLocation>>(emptyMap())
    val locations: StateFlow<Map<String, CharacterLocation>> = _locations.asStateFlow()

    private val _scopeRegionIds = MutableStateFlow<Set<Int>>(emptySet())
    val scopeRegionIds: StateFlow<Set<Int>> = _scopeRegionIds.asStateFlow()

    private val _lastHeartbeat = MutableStateFlow(0L)
    val lastHeartbeat: StateFlow<Long> = _lastHeartbeat.asStateFlow()

    private val _channels = MutableStateFlow(dev.eveintel.wire.ServerMessage.Channels())
    val channels: StateFlow<dev.eveintel.wire.ServerMessage.Channels> = _channels.asStateFlow()

    /** Set by the client so the settings screen can request a different channel selection. */
    @Volatile
    var requestChannels: ((List<String>) -> Unit)? = null

    fun setChannels(channels: dev.eveintel.wire.ServerMessage.Channels) {
        _channels.value = channels
    }

    private val _characters = MutableStateFlow<Map<String, dev.eveintel.wire.CharacterInfo>>(emptyMap())

    /** ESI-resolved character detail, keyed by the name as it appears in chat. */
    val characters: StateFlow<Map<String, dev.eveintel.wire.CharacterInfo>> = _characters.asStateFlow()

    private val _sovereignty = MutableStateFlow(dev.eveintel.wire.ServerMessage.Sovereignty())
    val sovereignty: StateFlow<dev.eveintel.wire.ServerMessage.Sovereignty> = _sovereignty.asStateFlow()

    private val _stats = MutableStateFlow<Map<Int, dev.eveintel.wire.SystemStats>>(emptyMap())
    val stats: StateFlow<Map<Int, dev.eveintel.wire.SystemStats>> = _stats.asStateFlow()

    fun addCharacters(characters: List<dev.eveintel.wire.CharacterInfo>) {
        _characters.value = _characters.value + characters.associateBy { it.name }
    }

    fun setSovereignty(sovereignty: dev.eveintel.wire.ServerMessage.Sovereignty) {
        _sovereignty.value = sovereignty
    }

    fun setStats(stats: List<dev.eveintel.wire.SystemStats>) {
        _stats.value = stats.associateBy { it.systemId }
    }

    /** Emitted for messages that arrive live, so the service can decide whether to alert. */
    private val _arrivals = MutableSharedFlow<IntelMessage>(extraBufferCapacity = 64)
    val arrivals: SharedFlow<IntelMessage> = _arrivals.asSharedFlow()

    @Volatile
    var universe: Universe? = null
        private set

    fun attachUniverse(universe: Universe) {
        this.universe = universe
    }

    fun setConnection(state: ConnectionState) {
        _connection.value = state
    }

    fun setHeartbeat(millis: Long) {
        _lastHeartbeat.value = millis
    }

    fun setScope(regionIds: Set<Int>) {
        _scopeRegionIds.value = regionIds
    }

    fun replaceAll(messages: List<IntelMessage>, locations: List<CharacterLocation>) {
        _messages.value = messages.sortedByDescending { it.timestampMillis }.take(MAX_MESSAGES)
        _locations.value = locations.associateBy { it.characterName }
    }

    suspend fun add(message: IntelMessage) {
        val current = _messages.value
        if (current.any { it.id == message.id }) return
        _messages.value = (listOf(message) + current).take(MAX_MESSAGES)
        _arrivals.emit(message)
    }

    fun updateLocation(location: CharacterLocation) {
        _locations.value = _locations.value + (location.characterName to location)
    }

    /**
     * Jump distances to everywhere reachable, measured from whichever of [characters] is closest.
     * Recomputed on demand; a BFS over ~8k nodes is cheap enough not to cache.
     *
     * An empty result means the distance is genuinely unknown — no characters chosen, none of them
     * seen in Local yet, or the universe still loading. Callers must not read that as "far away".
     */
    fun distancesFrom(characters: Set<String>): Map<Int, Int> {
        val universe = universe ?: return emptyMap()
        val origins = characters.mapNotNull { _locations.value[it]?.systemId }
        if (origins.isEmpty()) return emptyMap()
        return universe.distancesFrom(origins)
    }

    private const val MAX_MESSAGES = 500
}
