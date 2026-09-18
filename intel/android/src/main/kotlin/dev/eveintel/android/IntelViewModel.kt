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
    val display: dev.eveintel.model.DisplaySettings = dev.eveintel.model.DisplaySettings(),
    val characters: Map<String, dev.eveintel.wire.CharacterInfo> = emptyMap(),
    val sovereignty: dev.eveintel.wire.ServerMessage.Sovereignty = dev.eveintel.wire.ServerMessage.Sovereignty(),
    val stats: Map<Int, dev.eveintel.wire.SystemStats> = emptyMap(),
    /** Jump distance to every reachable system from the nearest of the alert characters. */
    val distances: Map<Int, Int> = emptyMap(),
) {
    /** Where the alert characters currently are, for centring and for the settings list. */
    val alertLocations: List<CharacterLocation>
        get() = settings?.alertCharacters.orEmpty().mapNotNull { locations[it] }

    /** Systems an alert character is sitting in right now, for the "you are here" markers. */
    val alertSystemIds: Set<Int>
        get() = alertLocations.mapTo(mutableSetOf()) { it.systemId }

    /**
     * One location to centre the map on and show in the header. A multiboxer's pilots are almost
     * always in or near the same place, so the alphabetically first is a stable pick rather than
     * one that flips about as locations update.
     */
    val primaryLocation: CharacterLocation?
        get() = alertLocations.minByOrNull { it.characterName }

    /**
     * True when a radius is set but nothing can be measured against it, so every hostile line
     * alerts. Surfaced in the UI: the radius reads as active while silently doing nothing.
     */
    val rangeUnmeasurable: Boolean
        get() = jumpRange > 0 && distances.isEmpty()

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
        val origins = settingsSnapshot.alertCharacters.mapNotNull { locations[it]?.systemId }
        IntelUiState(
            connection = connection,
            messages = messages,
            locations = locations,
            settings = settingsSnapshot,
            universe = universe,
            scopeRegionIds = scope,
            distances = if (universe != null && origins.isNotEmpty()) {
                universe.distancesFrom(origins)
            } else {
                emptyMap()
            },
        )
    }

    // combine() only has a typed overload up to five flows, and this needs six -- folding display
    // in first keeps every downstream combine call typed rather than casting out of an array.
    private val withDisplay = combine(core, IntelRepository.display) { base, display ->
        base.copy(display = display)
    }

    val state: StateFlow<IntelUiState> = combine(
        withDisplay,
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

    /** Adds or removes a character from the set the alert radius is measured against. */
    fun toggleAlertCharacter(character: String) = settings.update {
        val next = if (character in it.alertCharacters) {
            it.alertCharacters - character
        } else {
            it.alertCharacters + character
        }
        it.copy(alertCharacters = next)
    }

    fun setAlertRadius(radius: Int) = settings.update { it.copy(alertJumpRadius = radius) }

    fun setAlertOnClear(enabled: Boolean) = settings.update { it.copy(alertOnClear = enabled) }

    fun setKeepScreenOn(enabled: Boolean) = settings.update { it.copy(keepScreenOn = enabled) }

    /** Adds or removes one channel from the intel set and tells the daemon. */
    fun toggleChannel(name: String, enabled: Boolean) {
        val current = state.value.channels.selected.toMutableSet()
        if (enabled) current.add(name) else current.remove(name)
        IntelRepository.requestChannels?.invoke(current.sorted())
    }

    /**
     * Pushes a display-settings change to the daemon, which persists it and rebroadcasts to every
     * connected tablet -- including this one, so the UI updates from the round trip rather than an
     * optimistic local write, the same as [toggleChannel].
     */
    fun setDisplay(update: (dev.eveintel.model.DisplaySettings) -> dev.eveintel.model.DisplaySettings) {
        IntelRepository.requestDisplaySettings?.invoke(update(state.value.display).coerced())
    }
}
