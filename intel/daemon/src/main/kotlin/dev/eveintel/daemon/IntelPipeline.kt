package dev.eveintel.daemon

import dev.eveintel.model.CharacterLocation
import dev.eveintel.model.IntelMessage
import dev.eveintel.model.Token
import dev.eveintel.parse.ChatLogFormat
import dev.eveintel.parse.IntelParser
import dev.eveintel.parse.isIntel
import dev.eveintel.parse.messageId
import dev.eveintel.universe.Universe
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * Turns raw tailed lines into the intel stream the tablet consumes.
 *
 * Two jobs beyond parsing:
 *  - **Deduplication.** With five characters logged in, every channel line lands in five log files.
 *    Keyed on the message id so the tablet sees each line once.
 *  - **Location.** Local channel announcements tell us where each character is, which is what makes
 *    jump distance possible without ESI.
 */
class IntelPipeline(
    private val universe: Universe,
    private val parser: IntelParser,
    /** Live: the tablet can change the selection without restarting the daemon. */
    private val intelChannels: StateFlow<Set<String>>,
    private val historySize: Int = 200,
) {
    private val seen = LinkedHashSet<String>()
    private val mutex = Mutex()

    private val history = ArrayDeque<IntelMessage>()

    /** Most recent explicit system report per channel, for [correlate]. */
    private val recentSystem = mutableMapOf<String, RecentSystem>()
    private data class RecentSystem(val systemId: Int, val systemName: String, val timestampMillis: Long)

    private val _intel = MutableSharedFlow<IntelMessage>(extraBufferCapacity = 256)
    val intel: Flow<IntelMessage> = _intel.asSharedFlow()

    private val _locations = MutableStateFlow<Map<String, CharacterLocation>>(emptyMap())
    val locations = _locations.asStateFlow()

    suspend fun submit(tailed: TailedMessage) {
        if (tailed.channel == LOCAL_CHANNEL) {
            handleLocal(tailed)
            return
        }
        if (tailed.channel !in intelChannels.value) return
        if (tailed.message.author == ChatLogFormat.EVE_SYSTEM_AUTHOR) return

        val id = messageId(tailed.message)
        val isNew = mutex.withLock {
            if (!seen.add(id)) {
                false
            } else {
                if (seen.size > SEEN_CAPACITY) {
                    val oldest = seen.iterator()
                    repeat(SEEN_CAPACITY / 4) { if (oldest.hasNext()) { oldest.next(); oldest.remove() } }
                }
                true
            }
        }
        if (!isNew) return

        val parsed = mutex.withLock { correlate(tailed.channel, parser.parse(tailed.channel, tailed.message)) }
        if (!parsed.isIntel()) return

        mutex.withLock {
            history.addLast(parsed)
            while (history.size > historySize) history.removeFirst()
        }
        _intel.emit(parsed)
    }

    /**
     * Fills in a missing system on a terse follow-up (`nv`, a bare ship name) by borrowing the
     * channel's most recently *stated* system, if one landed within [CORRELATION_WINDOW_MS].
     *
     * Real reports arrive split across lines -- someone names the system, then a beat later adds
     * `nv` or a ship type with nothing else in the line. Read alone those lines have no location
     * and either show as unusable (`nv`) or get silently dropped (a bare ship name never clears
     * [isIntel]). The borrowed system is marked [Token.System.inferred] so the UI never presents it
     * as something the reporter actually typed.
     */
    private fun correlate(channel: String, message: IntelMessage): IntelMessage {
        val explicit = message.tokens.filterIsInstance<Token.System>().firstOrNull()
        if (explicit != null) {
            recentSystem[channel] = RecentSystem(explicit.systemId, explicit.name, message.timestampMillis)
            return message
        }

        val hasContent = message.keywords.isNotEmpty() || message.ships.isNotEmpty() ||
            message.questions.isNotEmpty() || message.reportedCount != null
        if (!hasContent) return message

        val recent = recentSystem[channel] ?: return message
        if (message.timestampMillis - recent.timestampMillis !in 0..CORRELATION_WINDOW_MS) return message

        return message.copy(
            tokens = message.tokens + Token.System(
                text = "",
                systemId = recent.systemId,
                name = recent.systemName,
                inferred = true,
            ),
        )
    }

    private fun handleLocal(tailed: TailedMessage) {
        val systemName = ChatLogFormat.parseLocalSystemChange(tailed.message) ?: return
        val character = tailed.listener ?: return
        val system = universe.systemByName(systemName) ?: return
        _locations.value = _locations.value + (
            character to CharacterLocation(
                characterName = character,
                systemId = system.id,
                systemName = system.name,
                sinceMillis = tailed.message.timestampMillis,
            )
            )
    }

    suspend fun snapshot(): List<IntelMessage> = mutex.withLock { history.toList() }

    private companion object {
        const val LOCAL_CHANNEL = "Local"
        const val SEEN_CAPACITY = 4000

        /** How long a bare follow-up can still borrow the channel's last stated system. */
        const val CORRELATION_WINDOW_MS = 30_000L
    }
}
