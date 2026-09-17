package dev.eveintel.daemon

import dev.eveintel.parse.ChatLogFormat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.nio.ByteBuffer
import java.nio.channels.FileChannel
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardOpenOption
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import kotlin.io.path.name

/**
 * A message together with which log file it came from.
 *
 * The same channel line appears in every logged-in character's file, so [characterId] is what lets
 * the deduplicator keep one copy while still tracking each character's Local channel separately.
 */
data class TailedMessage(
    val channel: String,
    val characterId: Long?,
    val listener: String?,
    val message: ChatLogFormat.RawMessage,
)

/**
 * Watches EVE's Chatlogs directory and emits new lines as they are written.
 *
 * Deliberately polls rather than using a filesystem watcher: EVE keeps its log files open and
 * Windows does not refresh directory metadata for open handles, so `WatchService` and
 * `Files.size()` both go stale. Opening a channel and asking it for the size is the reliable read.
 */
class LogTailer(
    private val directory: Path,
    /** Consulted on every scan, so channels selected from the tablet take effect without a restart. */
    private val channels: () -> Set<String>,
    private val pollInterval: Long = 500L,
    private val startFromEnd: Boolean = true,
    /**
     * How often the directory is re-enumerated to find rotated or newly selected files.
     *
     * Decoupled from [pollInterval] because these cost wildly different amounts. Reading new bytes
     * from a handful of known files is nearly free; enumerating the Chatlogs directory is not, as
     * it accumulates every log ever written -- 6k files on the development machine, and it only
     * grows. Scanning that twice a second cost a measurable fraction of a core doing nothing.
     */
    private val rescanInterval: Long = 5_000L,
    /**
     * Channels whose existing content is read even under [startFromEnd].
     *
     * `Local` carries a character's *current system*, which is state rather than an event: EVE
     * writes one file per system entered and announces the change on its first line. Skipping the
     * history of a file that already existed therefore means never learning where anybody is until
     * they next jump -- so a docked or stationary pilot never appears at all. Reading these from
     * the top is cheap, because the pipeline discards every Local line except that announcement.
     */
    private val alwaysFromStart: Set<String> = emptySet(),
    /**
     * Ignore log files whose name is older than this many days.
     *
     * The Chatlogs directory is an archive going back years, and the newest file *per character*
     * is picked out of all of it -- so without a bound, every character ever flown is reported at
     * whatever system they were parked in months ago. A file EVE is still appending to is one from
     * a session that is realistically this recent.
     */
    private val maxFileAgeDays: Long = 30,
) {
    private val offsets = mutableMapOf<Path, Long>()
    private val headers = mutableMapOf<Path, ChatLogFormat.Header>()
    private val events = MutableSharedFlow<TailedMessage>(extraBufferCapacity = 512)

    /**
     * Log file names that existed when we started, captured unfiltered.
     *
     * This is what separates "history we chose to skip" from "a file EVE has just created". A file
     * appearing later is a rotation or a fresh login, and its opening lines are live intel, so it
     * is read from the top even under [startFromEnd]. Capturing it unfiltered matters: otherwise
     * selecting a new channel from the tablet would make that channel's existing logs look new and
     * replay the whole day into the feed.
     */
    private var preexisting: Set<String>? = null

    private var active: List<Path> = emptyList()
    private var lastScanAt = 0L
    private var lastChannels: Set<String>? = null

    val messages: Flow<TailedMessage> = events.asSharedFlow()

    fun start(scope: CoroutineScope) = scope.launch(Dispatchers.IO) {
        while (isActive) {
            runCatching { poll() }.onFailure { System.err.println("poll failed: ${it.message}") }
            delay(pollInterval)
        }
    }

    /**
     * Files EVE is currently appending to: the newest file per channel per character.
     *
     * Cached between scans. A channel-selection change forces one immediately, so the tablet's
     * setting still takes effect at once rather than waiting out [rescanInterval].
     */
    private fun activeFiles(): List<Path> {
        val wanted = channels()
        val due = System.currentTimeMillis() - lastScanAt >= rescanInterval
        if (!due && wanted == lastChannels) return active

        lastScanAt = System.currentTimeMillis()
        lastChannels = wanted
        active = scan(wanted)

        // Files that rotated out are never read again; without this the maps grow for as long as
        // the daemon runs, which is the whole point of it.
        val live = active.toSet()
        offsets.keys.retainAll(live)
        headers.keys.retainAll(live)
        return active
    }

    private fun scan(wanted: Set<String>): List<Path> {
        if (!Files.isDirectory(directory)) return emptyList()
        // A glob plus the filename parse is the filter; the previous isRegularFile check cost a
        // stat syscall per entry across the whole directory, every single poll.
        val logs = Files.newDirectoryStream(directory, "*.txt").use { stream ->
            stream.mapNotNull { path ->
                ChatLogFormat.parseFileName(path.name)?.let { path to it }
            }
        }
        if (preexisting == null) preexisting = logs.mapTo(mutableSetOf()) { it.first.name }

        // Compared as a string: the name carries yyyyMMdd, so this costs no stat call.
        val oldest = LocalDate.now().minusDays(maxFileAgeDays).format(FILE_DATE)

        return logs
            .filter { (_, parsed) -> parsed.date >= oldest }
            .filter { (_, parsed) -> wanted.isEmpty() || parsed.channel in wanted }
            .groupBy { (_, parsed) -> "${parsed.channel}|${parsed.characterId}" }
            .mapNotNull { (_, group) ->
                group.maxByOrNull { "${it.second.date}${it.second.time}" }?.first
            }
    }

    private suspend fun poll() = withContext(Dispatchers.IO) {
        for (path in activeFiles()) {
            // Per file, not per poll: during a rotation a file can vanish or be briefly locked, and
            // one such failure must not cost every other channel its turn.
            runCatching { tail(path) }
                .onFailure { System.err.println("tail ${path.name} failed: $it") }
        }
    }

    private suspend fun tail(path: Path) {
        val fresh = path !in offsets
        FileChannel.open(path, StandardOpenOption.READ).use { channel ->
            val size = channel.size()
            if (fresh) {
                // Read the header once so we know the listener, then optionally skip history.
                readHeader(channel, path)
                val channelName = ChatLogFormat.parseFileName(path.name)?.channel
                val skipHistory = startFromEnd &&
                    path.name in preexisting.orEmpty() &&
                    channelName !in alwaysFromStart
                offsets[path] = if (skipHistory) alignToEven(size) else 0L
                if (skipHistory) return@use
            }
            val from = offsets[path] ?: 0L
            if (size <= from) return@use
            emitRange(channel, path, from, size)
        }
    }

    private fun readHeader(channel: FileChannel, path: Path) {
        val length = minOf(channel.size(), HEADER_BYTES)
        if (length <= 0) return
        val text = readText(channel, 0, length)
        headers[path] = ChatLogFormat.parseHeader(text.lines())
    }

    private suspend fun emitRange(channel: FileChannel, path: Path, from: Long, to: Long) {
        val text = readText(channel, from, to - from)
        // A partial trailing line is possible; keep the offset before it and re-read next poll.
        val complete = text.substringBeforeLast('\n', missingDelimiterValue = "")
        if (complete.isEmpty()) return

        val consumedBytes = complete.length.toLong() * 2 + 2 // UTF-16LE, plus the newline
        val next = alignToEven(minOf(to, from + consumedBytes))

        val fileName = ChatLogFormat.parseFileName(path.name)
        if (fileName == null) {
            offsets[path] = next // Unreachable for a file we chose to tail, but never re-read it.
            return
        }
        val header = headers[path]
        for (line in complete.lines()) {
            val message = ChatLogFormat.parseMessage(line) ?: continue
            events.emit(
                TailedMessage(
                    channel = fileName.channel,
                    characterId = fileName.characterId,
                    listener = header?.listener,
                    message = message,
                ),
            )
        }
        // Advanced only once the lines are handed off. If emission is cancelled part way the range
        // is re-read next poll, and the pipeline's dedup drops what already got through -- whereas
        // advancing first would lose those lines for good.
        offsets[path] = next
    }

    private fun readText(channel: FileChannel, position: Long, length: Long): String {
        val safeLength = length.coerceAtMost(MAX_READ).toInt()
        if (safeLength <= 0) return ""
        val buffer = ByteBuffer.allocate(safeLength)
        channel.read(buffer, position)
        buffer.flip()
        return StandardCharsets.UTF_16LE.decode(buffer).toString()
    }

    /** UTF-16 is two bytes per unit; landing mid-character would corrupt the next read. */
    private fun alignToEven(value: Long): Long = value - (value % 2)

    private companion object {
        val FILE_DATE: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyyMMdd")
        const val HEADER_BYTES = 2048L
        const val MAX_READ = 4L * 1024 * 1024
    }
}
