package dev.eveintel.daemon.ui

import java.awt.Desktop
import java.awt.Image
import java.awt.MenuItem
import java.awt.PopupMenu
import java.awt.SystemTray
import java.awt.Toolkit
import java.awt.TrayIcon
import java.awt.datatransfer.StringSelection
import java.nio.file.Files
import java.nio.file.Path
import javax.imageio.ImageIO
import javax.swing.SwingUtilities

/**
 * The daemon's only visible surface when it runs as a packaged exe.
 *
 * A packaged app-image launcher has no console, so `println` goes nowhere a user can see it. The
 * tray icon is therefore the whole status display: the tooltip carries the tablet URL, and the
 * menu carries the two things a user needs (settings, exit) plus the two that make a support
 * conversation possible (copy the URL, open the log).
 *
 * Everything here degrades to nothing: if the platform has no tray, [install] returns null and the
 * daemon keeps running exactly as the console build does.
 */
class Tray private constructor(
    private val icon: TrayIcon,
    private val tray: SystemTray,
) {
    fun status(text: String) {
        // Windows truncates a tray tooltip at 64 characters and silently drops the rest.
        icon.toolTip = if (text.length <= TOOLTIP_LIMIT) text else text.take(TOOLTIP_LIMIT - 1) + "…"
    }

    fun notify(caption: String, text: String, type: TrayIcon.MessageType = TrayIcon.MessageType.INFO) {
        runCatching { icon.displayMessage(caption, text, type) }
    }

    fun remove() = runCatching { tray.remove(icon) }.let { }

    companion object {
        private const val TOOLTIP_LIMIT = 64

        /** Loads the tray image closest to the size the platform actually asked for. */
        private fun image(preferred: Int): Image? {
            val sizes = intArrayOf(16, 20, 24, 32, 40, 48, 64)
            val best = sizes.minByOrNull { kotlin.math.abs(it - preferred) } ?: 16
            val stream = Tray::class.java.classLoader.getResourceAsStream("tray-$best.png") ?: return null
            return stream.use { ImageIO.read(it) }
        }

        fun install(
            onSettings: () -> Unit,
            onExit: () -> Unit,
            onRestart: (() -> Unit)?,
            logFile: Path?,
            tabletUrl: () -> String?,
        ): Tray? {
            if (!SystemTray.isSupported()) return null

            val tray = SystemTray.getSystemTray()
            val image = image(tray.trayIconSize.width) ?: return null

            val menu = PopupMenu()
            menu.add(MenuItem("Settings…").apply { addActionListener { SwingUtilities.invokeLater(onSettings) } })
            menu.add(MenuItem("Copy tablet URL").apply {
                addActionListener {
                    val url = tabletUrl() ?: return@addActionListener
                    Toolkit.getDefaultToolkit().systemClipboard.setContents(StringSelection(url), null)
                }
            })
            if (logFile != null) {
                menu.add(MenuItem("Open log").apply {
                    addActionListener {
                        runCatching {
                            if (Files.isRegularFile(logFile) && Desktop.isDesktopSupported()) {
                                Desktop.getDesktop().open(logFile.toFile())
                            }
                        }
                    }
                })
            }
            menu.addSeparator()
            if (onRestart != null) {
                menu.add(MenuItem("Restart").apply { addActionListener { onRestart() } })
            }
            menu.add(MenuItem("Exit").apply { addActionListener { onExit() } })

            val icon = TrayIcon(image, "EveDeck Intel", menu).apply {
                isImageAutoSize = true
                // Double-clicking a tray icon opens the app; here that is the settings window.
                addActionListener { SwingUtilities.invokeLater(onSettings) }
            }

            return runCatching {
                tray.add(icon)
                Tray(icon, tray)
            }.getOrNull()
        }
    }
}
