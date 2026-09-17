package dev.eveintel.android

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import dev.eveintel.model.CharacterLocation
import dev.eveintel.model.IntelMessage
import dev.eveintel.universe.Universe
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

data class IntelUiState(
    val connection: ConnectionState = ConnectionState.DISCONNECTED,
    val messages: List<IntelMessage> = emptyList(),
    val locations: Map<String, CharacterLocation> = emptyMap(),
    val settings: Settings.Snapshot? = null,
    val universe: Universe? = null,
    val scopeRegionIds: Set<Int> = emptySet(),
    val channels: dev.eveintel.wire.ServerMessage.Channels = dev.eveintel.wire.ServerMessage.Channels(),
    val characters: Map<String, dev.eveintel.wire.CharacterInfo> = emptyMap(),
    val sovereignty: dev.eveintel.wire.ServerMessage.Sovereignty = dev.eveintel.wire.ServerMessage.Sovereignty(),
    val stats: Map<Int, dev.eveintel.wire.SystemStats> = emptyMap(),
    /** Jump distance from the followed character to every reachable system. */
    val distances: Map<Int, Int> = emptyMap(),
) {
    val followedLocation: CharacterLocation?
        get() = settings?.followedCharacter?.let { locations[it] }

    fun jumpsTo(systemId: Int): Int? = distances[systemId]

    /** The closest system mentioned by a message, and how far away it is. */
    fun nearestJumps(message: IntelMessage): Int? =
        message.systemIds.mapNotNull { distances[it] }.minOrNull()

    /** The alert radius doubles as the "near me" threshold for styling. */
    val jumpRange: Int get() = settings?.alertJumpRadius ?: 0

    fun isInRange(jumps: Int?): Boolean = jumps != null && jumpRange > 0 && jumps <= jumpRange

    /** Base URL of the daemon's image proxy, or null until a server is configured. */
    val imageBaseUrl: String?
        get() = settings?.takeIf { it.isConfigured }?.let { "http://${it.serverHost}:${it.serverPort}" }

    fun portraitUrl(name: String, size: Int = 64): String? {
        val id = characters[name]?.characterId ?: return null
        return imageBaseUrl?.let { "$it/img/characters/$id/portrait?size=$size" }
    }

    fun allianceLogoUrl(name: String, size: Int = 32): String? {
        val id = characters[name]?.allianceId ?: return null
        return imageBaseUrl?.let { "$it/img/alliances/$id/logo?size=$size" }
    }

    fun shipIconUrl(typeId: Int?, size: Int = 64): String? {
        if (typeId == null) return null
        return imageBaseUrl?.let { "$it/img/types/$typeId/icon?size=$size" }
    }

    fun sovHolderOf(systemId: Int): dev.eveintel.wire.SovHolder? {
        val holderId = sovereignty.systems[systemId] ?: return null
        return sovereignty.holders.firstOrNull { it.id == holderId }
    }
}

class IntelViewModel(application: Application) : AndroidViewModel(application) {

    val settings = Settings(application)

    private val core: kotlinx.coroutines.flow.Flow<IntelUiState> = combine(
        IntelRepository.connection,
        IntelRepository.messages,
        IntelRepository.locations,
        settings.state,
        IntelRepository.scopeRegionIds,
    ) { connection, messages, locations, settingsSnapshot, scope ->
        val universe = IntelRepository.universe
        val origin = settingsSnapshot.followedCharacter?.let { locations[it] }
        IntelUiState(
            connection = connection,
            messages = messages,
            locations = locations,
            settings = settingsSnapshot,
            universe = universe,
            scopeRegionIds = scope,
            distances = if (universe != null && origin != null) {
                universe.distancesFrom(origin.systemId)
            } else {
                emptyMap()
            },
        )
    }

    val state: StateFlow<IntelUiState> = combine(
        core,
        IntelRepository.channels,
        IntelRepository.characters,
        IntelRepository.sovereignty,
        IntelRepository.stats,
    ) { base, channels, characters, sovereignty, stats ->
        base.copy(
            channels = channels,
            characters = characters,
            sovereignty = sovereignty,
            stats = stats,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), IntelUiState())

    init {
        viewModelScope.launch {
            IntelRepository.attachUniverse(UniverseLoader.load(application))
        }
    }

    fun setServer(host: String, port: Int) = settings.update {
        it.copy(serverHost = host.trim(), serverPort = port)
    }

    fun follow(character: String?) = settings.update { it.copy(followedCharacter = character) }

    fun setAlertRadius(radius: Int) = settings.update { it.copy(alertJumpRadius = radius) }

    fun setAlertOnClear(enabled: Boolean) = settings.update { it.copy(alertOnClear = enabled) }

    fun setKeepScreenOn(enabled: Boolean) = settings.update { it.copy(keepScreenOn = enabled) }

    /** Adds or removes one channel from the intel set and tells the daemon. */
    fun toggleChannel(name: String, enabled: Boolean) {
        val current = state.value.channels.selected.toMutableSet()
        if (enabled) current.add(name) else current.remove(name)
        IntelRepository.requestChannels?.invoke(current.sorted())
    }
}
