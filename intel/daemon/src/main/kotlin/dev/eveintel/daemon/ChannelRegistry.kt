package dev.eveintel.daemon

import dev.eveintel.parse.ChatLogFormat
import dev.eveintel.wire.ChannelInfo
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.nio.file.Files
import java.nio.file.Path
import kotlin.io.path.name

/**
 * Discovers which chat channels exist, so the tablet can offer a picker instead of the user editing
 * a properties file.
 *
 * Rescanned periodically rather than once at startup: joining a new intel channel in game creates
 * its first log file immediately, and it should appear in the picker without restarting anything.
 */
class ChannelRegistry(
    private val directory: Path,
    private val selectedChannels: MutableStateFlow<Set<String>>,
) {
    private val _available = MutableStateFlow<List<ChannelInfo>>(emptyList())
    val available: StateFlow<List<ChannelInfo>> = _available.asStateFlow()

    val selected: StateFlow<Set<String>> = selectedChannels.asStateFlow()

    fun start(scope: CoroutineScope) = scope.launch(Dispatchers.IO) {
        while (isActive) {
            runCatching { scan() }
            delay(SCAN_INTERVAL_MS)
        }
    }

    fun select(channels: Collection<String>) {
        selectedChannels.value = channels.filter { it !in RESERVED }.toSet()
    }

    suspend fun scan() = withContext(Dispatchers.IO) {
        if (!Files.isDirectory(directory)) return@withContext

        data class Accumulator(var count: Int = 0, var lastModified: Long = 0L)

        val byChannel = mutableMapOf<String, Accumulator>()
        Files.list(directory).use { stream ->
            stream.forEach { path ->
                if (!Files.isRegularFile(path)) return@forEach
                val parsed = ChatLogFormat.parseFileName(path.name) ?: return@forEach
                val accumulator = byChannel.getOrPut(parsed.channel) { Accumulator() }
                accumulator.count++
                val modified = runCatching { Files.getLastModifiedTime(path).toMillis() }.getOrDefault(0L)
                if (modified > accumulator.lastModified) accumulator.lastModified = modified
            }
        }

        _available.value = byChannel
            .map { (name, accumulator) ->
                ChannelInfo(
                    name = name,
                    fileCount = accumulator.count,
                    lastActivityMillis = accumulator.lastModified,
                    reserved = name in RESERVED,
                )
            }
            .sortedWith(compareByDescending<ChannelInfo> { it.lastActivityMillis }.thenBy { it.name })
    }

    companion object {
        /** `Local` drives location tracking and is always tailed; it is never an intel channel. */
        val RESERVED = setOf("Local")
        private const val SCAN_INTERVAL_MS = 30_000L
    }
}
