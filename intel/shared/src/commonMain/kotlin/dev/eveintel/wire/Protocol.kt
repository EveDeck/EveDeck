package dev.eveintel.wire

import dev.eveintel.model.CharacterLocation
import dev.eveintel.model.IntelMessage
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * Messages sent from the PC daemon to the tablet over the LAN WebSocket.
 *
 * The tablet holds the same [dev.eveintel.universe.Universe] the daemon does, so the wire only
 * carries system *ids* — never names, coordinates or jump graphs.
 */
@Serializable
sealed interface ServerMessage {

    /** Sent once on connect, so a tablet joining mid-fight sees recent history immediately. */
    @Serializable
    @SerialName("snapshot")
    data class Snapshot(
        val messages: List<IntelMessage>,
        val locations: List<CharacterLocation>,
        val scopeRegionIds: List<Int>,
        val serverTimeMillis: Long,
        val channels: Channels = Channels(),
    ) : ServerMessage

    /**
     * Every channel the daemon can see log files for, and which of them are treated as intel.
     * Sent on connect and whenever either list changes, so the tablet can offer a picker rather
     * than the user hand-editing a properties file.
     */
    @Serializable
    @SerialName("channels")
    data class Channels(
        val available: List<ChannelInfo> = emptyList(),
        val selected: List<String> = emptyList(),
    ) : ServerMessage

    @Serializable
    @SerialName("intel")
    data class Intel(val message: IntelMessage) : ServerMessage

    @Serializable
    @SerialName("location")
    data class Location(val location: CharacterLocation) : ServerMessage

    /** Keeps the connection warm and lets the tablet show an accurate "live" indicator. */
    /** ESI-resolved detail for characters seen in intel. Sent incrementally as lookups complete. */
    @Serializable
    @SerialName("characters")
    data class Characters(val characters: List<CharacterInfo>) : ServerMessage

    /** Sovereignty holders and which system each one holds. Refreshed hourly. */
    @Serializable
    @SerialName("sovereignty")
    data class Sovereignty(
        val holders: List<SovHolder> = emptyList(),
        /** systemId -> holder id. Systems with no sov are simply absent. */
        val systems: Map<Int, Long> = emptyMap(),
    ) : ServerMessage

    /** Kill and jump activity per system, from ESI. Refreshed hourly. */
    @Serializable
    @SerialName("stats")
    data class Stats(val systems: List<SystemStats> = emptyList()) : ServerMessage

    @Serializable
    @SerialName("heartbeat")
    data class Heartbeat(val serverTimeMillis: Long) : ServerMessage
}

/**
 * A character seen in intel, resolved through ESI.
 *
 * Names come out of chat text, so resolution is best-effort: a typo or a since-renamed character
 * simply never gets an id, and the UI falls back to the plain name.
 */
@Serializable
data class CharacterInfo(
    val name: String,
    val characterId: Long,
    val corporationId: Long? = null,
    val corporationName: String? = null,
    val corporationTicker: String? = null,
    val allianceId: Long? = null,
    val allianceName: String? = null,
    val allianceTicker: String? = null,
)

@Serializable
data class SovHolder(
    val id: Long,
    val name: String,
    val ticker: String? = null,
    /** "alliance" or "faction". */
    val kind: String = "alliance",
)

@Serializable
data class SystemStats(
    val systemId: Int,
    val shipKills: Int = 0,
    val podKills: Int = 0,
    val npcKills: Int = 0,
    val jumps: Int = 0,
) {
    /** Player-versus-player activity, which is what matters for reading a map at a glance. */
    val pvpKills: Int get() = shipKills + podKills
}

/**
 * A chat channel the daemon has log files for.
 *
 * [lastActivityMillis] lets the picker sort by what is actually in use — a character who joined a
 * channel once a year ago should not sit above the one they are reporting into right now.
 */
@Serializable
data class ChannelInfo(
    val name: String,
    val fileCount: Int,
    val lastActivityMillis: Long,
    /** True for channels the daemon never treats as intel, such as `Local`. */
    val reserved: Boolean = false,
)

@Serializable
sealed interface ClientMessage {

    @Serializable
    @SerialName("hello")
    data class Hello(val deviceName: String, val protocolVersion: Int = PROTOCOL_VERSION) : ClientMessage

    /** The tablet chooses which character it is following for jump-distance purposes. */
    @Serializable
    @SerialName("follow")
    data class Follow(val characterName: String?) : ClientMessage

    /** Replaces the set of channels treated as intel. The daemon persists this. */
    @Serializable
    @SerialName("setChannels")
    data class SetChannels(val channels: List<String>) : ClientMessage
}

const val PROTOCOL_VERSION = 1

val WireJson: Json = Json {
    ignoreUnknownKeys = true
    encodeDefaults = true
    classDiscriminator = "type"
}
