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
    /** Re-read on every poll, so channels selected from the tablet take effect without a restart. */
    private val channels: () -> Set<String>,
    private val pollInterval: Long = 500L,
    private val startFromEnd: Boolean = true,
) {
    private val offsets = mutableMapOf<Path, Long>()
    private val headers = mutableMapOf<Path, ChatLogFormat.Header>()
    private val events = MutableSharedFlow<TailedMessage>(extraBufferCapacity = 512)

    val messages: Flow<TailedMessage> = events.asSharedFlow()

    fun start(scope: CoroutineScope) = scope.launch(Dispatchers.IO) {
        while (isActive) {
            runCatching { poll() }.onFailure { System.err.println("poll failed: ${it.message}") }
            delay(pollInterval)
        }
    }

    /** Files EVE is currently appending to: the newest file per channel per character. */
    private fun activeFiles(): List<Path> {
        if (!Files.isDirectory(directory)) return emptyList()
        val wanted = channels()
        return Files.list(directory).use { stream ->
            stream.toList()
                .filter { Files.isRegularFile(it) }
                .mapNotNull { path ->
                    val parsed = ChatLogFormat.parseFileName(path.name) ?: return@mapNotNull null
                    if (wanted.isNotEmpty() && parsed.channel !in wanted) return@mapNotNull null
                    Triple(path, parsed, "${parsed.channel}|${parsed.characterId}")
                }
                .groupBy { it.third }
                .mapNotNull { (_, group) ->
                    group.maxByOrNull { "${it.second.date}${it.second.time}" }?.first
                }
        }
    }

    private suspend fun poll() = withContext(Dispatchers.IO) {
        for (path in activeFiles()) {
            val fresh = path !in offsets
            FileChannel.open(path, StandardOpenOption.READ).use { channel ->
                val size = channel.size()
                if (fresh) {
                    // Read the header once so we know the listener, then optionally skip history.
                    readHeader(channel, path)
                    offsets[path] = if (startFromEnd) alignToEven(size) else 0L
                    if (startFromEnd) return@use
                }
                val from = offsets[path] ?: 0L
                if (size <= from) return@use
                emitRange(channel, path, from, size)
            }
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
        offsets[path] = alignToEven(minOf(to, from + consumedBytes))

        val fileName = ChatLogFormat.parseFileName(path.name) ?: return
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
        const val HEADER_BYTES = 2048L
        const val MAX_READ = 4L * 1024 * 1024
    }
}
