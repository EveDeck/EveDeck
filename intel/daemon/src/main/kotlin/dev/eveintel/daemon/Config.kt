package dev.eveintel.daemon

import dev.eveintel.universe.Universe
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.util.Properties

/**
 * Daemon configuration, read from `eveintel.properties` next to the jar (or `--config <path>`).
 * Every value has a working default for a standard Windows EVE install.
 */
data class Config(
    val chatLogsDirectory: Path,
    val intelChannels: Set<String>,
    val scopeRegions: Set<String>,
    val port: Int,
    val bindAddress: String,
    val startFromEnd: Boolean,
) {
    fun scopeRegionIds(universe: Universe): Set<Int> =
        scopeRegions.mapNotNull { universe.regionIdsByLowerName[it.lowercase()] }.toSet()

    companion object {

        /**
         * Rewrites just the `intel.channels` line, preserving comments and every other setting.
         * Called when the tablet changes the selection, so the choice survives a daemon restart.
         */
        fun saveChannels(path: Path, channels: Collection<String>) {
            save(path, mapOf("intel.channels" to channels.sorted().joinToString(",")))
        }

        /**
         * Rewrites the given keys in place, preserving comments, ordering and every key not named.
         *
         * The properties file is hand-edited as often as it is written by the settings window, and
         * `Properties.store` would flatten its comments into a single timestamp line -- so this
         * edits the lines rather than round-tripping through [Properties].
         */
        fun save(path: Path, values: Map<String, String>) {
            val existing = if (Files.isRegularFile(path)) Files.readAllLines(path) else emptyList()
            // Properties.load() (below) un-escapes backslashes on read, so a raw Windows path
            // written here without doubling them comes back mangled next launch -- silently, since
            // a garbled chatlogs.dir just finds no files rather than throwing.
            val remaining = values.mapValues { (_, value) -> value.replace("\\", "\\\\") }.toMutableMap()
            val updated = existing.map { line ->
                val key = remaining.keys.firstOrNull { line.trimStart().startsWith("$it=") }
                    ?: return@map line
                "$key=${remaining.remove(key)}"
            } + remaining.map { (key, value) -> "$key=$value" }

            runCatching { Files.write(path, updated) }
                .onFailure { System.err.println("could not save configuration: ${it.message}") }
        }

        fun load(path: Path?): Config {
            val properties = Properties()
            if (path != null && Files.isRegularFile(path)) {
                Files.newBufferedReader(path).use { properties.load(it) }
            }

            // A present-but-empty key means "work it out for me", not "use an empty value" -- the
            // shipped template leaves `chatlogs.dir=` blank precisely so autodetection runs.
            fun string(key: String, default: String) =
                properties.getProperty(key, default).trim().ifEmpty { default }
            fun set(key: String, default: String) = string(key, default)
                .split(',')
                .map { it.trim() }
                .filter { it.isNotEmpty() }
                .toSet()

            return Config(
                chatLogsDirectory = Paths.get(string("chatlogs.dir", defaultChatLogsDirectory())),
                intelChannels = set("intel.channels", ""),
                scopeRegions = set("scope.regions", ""),
                port = string("server.port", "31337").toIntOrNull() ?: 31337,
                bindAddress = string("server.bind", "0.0.0.0"),
                startFromEnd = string("logs.startFromEnd", "true").toBooleanStrictOrNull() ?: true,
            )
        }

        /** EVE writes to Documents; OneDrive redirection is common, so check both. */
        fun defaultChatLogsDirectory(): String {
            val home = System.getProperty("user.home") ?: "."
            val candidates = listOf(
                Paths.get(home, "Documents", "EVE", "logs", "Chatlogs"),
                Paths.get(home, "OneDrive", "Documents", "EVE", "logs", "Chatlogs"),
            )
            return (candidates.firstOrNull { Files.isDirectory(it) } ?: candidates.first()).toString()
        }
    }
}
