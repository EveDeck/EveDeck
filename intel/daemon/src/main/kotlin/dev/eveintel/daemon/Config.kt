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
            val line = "intel.channels=${channels.sorted().joinToString(",")}"
            val existing = if (Files.isRegularFile(path)) Files.readAllLines(path) else emptyList()
            val updated = if (existing.any { it.trimStart().startsWith("intel.channels=") }) {
                existing.map { if (it.trimStart().startsWith("intel.channels=")) line else it }
            } else {
                existing + line
            }
            runCatching { Files.write(path, updated) }
                .onFailure { System.err.println("could not save channel selection: ${it.message}") }
        }

        fun load(path: Path?): Config {
            val properties = Properties()
            if (path != null && Files.isRegularFile(path)) {
                Files.newBufferedReader(path).use { properties.load(it) }
            }

            fun string(key: String, default: String) = properties.getProperty(key, default).trim()
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
