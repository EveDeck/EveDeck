package dev.eveintel.daemon.ui

import dev.eveintel.daemon.ChannelRegistry
import dev.eveintel.daemon.Config
import dev.eveintel.daemon.IntelServer
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.nio.file.StandardOpenOption
import java.io.PrintStream
import javax.swing.SwingUtilities
import kotlin.system.exitProcess

/**
 * Everything the daemon needs in order to run as a packaged Windows app rather than a console
 * process: where its configuration and log live, and the tray it is driven from.
 *
 * The console build is unchanged and still the default when a console exists. This only takes over
 * when there is nowhere for `println` to go.
 */
object DesktopSession {

    /**
     * Set by every jpackage launcher; the single reliable way to tell "packaged app" from "run
     * from Gradle", and the path to relaunch on restart.
     */
    val appPath: Path? = System.getProperty("jpackage.app-path")?.let { Paths.get(it) }

    val isPackaged: Boolean get() = appPath != null

    /**
     * Configuration sits beside the exe, not in the working directory.
     *
     * Launched from Explorer the two are the same, but launched from a shortcut or a startup entry
     * they are not, and a daemon that reads a different config depending on how it was started is
     * indistinguishable from one that lost its settings.
     */
    fun defaultConfigPath(): Path =
        appPath?.parent?.resolve(CONFIG_NAME) ?: Paths.get(CONFIG_NAME)

    /**
     * Seeds a first-run config from the template shipped beside the exe, so the file a user opens
     * has the explanatory comments in it rather than only the keys the settings window writes.
     */
    fun seedConfig(path: Path) {
        if (Files.exists(path)) return
        val template = path.parent?.resolve("$CONFIG_NAME.example") ?: return
        if (Files.isRegularFile(template)) runCatching { Files.copy(template, path) }
    }

    /**
     * Redirects stdout and stderr into a file next to the config.
     *
     * A packaged launcher has no console, so without this the daemon's entire diagnostic output --
     * including the reason it found no chat logs -- goes to a handle nobody can read. Truncated on
     * each start: this is a "why did it not work just now" log, not an archive.
     */
    fun redirectOutput(configPath: Path): Path? = runCatching {
        val logFile = configPath.resolveSibling(LOG_NAME)
        val stream = PrintStream(
            Files.newOutputStream(
                logFile,
                StandardOpenOption.CREATE,
                StandardOpenOption.WRITE,
                StandardOpenOption.TRUNCATE_EXISTING,
            ),
            true,
            Charsets.UTF_8,
        )
        System.setOut(stream)
        System.setErr(stream)
        logFile
    }.getOrNull()

    /**
     * Installs the tray and the settings window.
     *
     * Returns null when the platform has no tray, in which case the daemon carries on headless --
     * there is no state here the rest of the daemon depends on.
     */
    fun install(
        configPath: Path,
        config: Config,
        regionNames: List<String>,
        registry: ChannelRegistry,
        knownLocations: () -> Int,
        logFile: Path?,
        openSettings: Boolean = false,
        onExit: () -> Unit,
    ): Tray? {
        // Before any component is constructed: Swing reads the look and feel at construction
        // time, and a window built first stays grey however the theme is set afterwards.
        Theme.install()

        val tabletUrl = { IntelServer.localAddresses().firstOrNull()?.let { "ws://$it:${config.port}/intel" } }

        val settings = SettingsWindow(
            configPath = configPath,
            initial = config,
            regionNames = regionNames,
            availableChannels = { registry.available.value },
            onChannelsChanged = { registry.select(it) },
            status = {
                val urls = IntelServer.localAddresses().map { "ws://$it:${config.port}/intel" }
                buildList {
                    add(
                        if (urls.isEmpty()) {
                            "Listening on port ${config.port}. No LAN address found."
                        } else {
                            "Tablet URL: " + urls.joinToString("   ")
                        },
                    )
                    // A count, never a name: this is the number that says whether the tablet has
                    // anything to measure its alert radius against.
                    add("${registry.selected.value.size} channel(s) selected, ${knownLocations()} character location(s) known.")
                }
            },
            tabletUrl = tabletUrl,
            onRestart = restartAction(),
        )

        val tray = Tray.install(
            onSettings = { settings.show() },
            onExit = onExit,
            onRestart = restartAction(),
            logFile = logFile,
            tabletUrl = tabletUrl,
        )
        tray?.status(tabletUrl()?.let { "EveDeck Intel - $it" } ?: "EveDeck Intel")
        println(if (tray != null) "tray: installed" else "tray: unavailable, running headless")

        // Windows 11 hides a new tray icon in the overflow flyout, so a first run can look like
        // nothing happened at all. Opening settings once gives the daemon a visible front door.
        if (openSettings || tray == null) SwingUtilities.invokeLater { settings.show() }
        return tray
    }

    /**
     * Relaunching only makes sense for a packaged build; from Gradle there is no single command to
     * re-run, so the restart affordances hide themselves rather than offering a broken button.
     */
    private fun restartAction(): (() -> Unit)? {
        val exe = appPath ?: return null
        return {
            // The replacement has to start *after* this process has released the listening socket,
            // or it dies on bind and the user is left with no daemon at all. A detached shell that
            // waits a couple of seconds is the cheapest way to sequence that without a supervisor.
            runCatching {
                ProcessBuilder("cmd", "/c", "ping -n 3 127.0.0.1 >nul & start \"\" \"$exe\"")
                    .directory(exe.parent?.toFile())
                    .start()
            }
            SwingUtilities.invokeLater { exitProcess(0) }
        }
    }

    private const val CONFIG_NAME = "eveintel.properties"
    private const val LOG_NAME = "eveintel.log"
}
