package dev.eveintel.daemon.ui

import dev.eveintel.daemon.Config
import dev.eveintel.model.DisplaySettings
import dev.eveintel.wire.ChannelInfo
import java.awt.BorderLayout
import java.awt.Component
import java.awt.Dimension
import java.awt.FlowLayout
import java.awt.GridBagConstraints
import java.awt.GridBagLayout
import java.awt.Insets
import java.awt.Toolkit
import java.awt.datatransfer.StringSelection
import java.nio.file.Path
import javax.imageio.ImageIO
import javax.swing.BorderFactory
import javax.swing.BoxLayout
import javax.swing.DefaultListModel
import javax.swing.JButton
import javax.swing.JCheckBox
import javax.swing.JColorChooser
import javax.swing.JFileChooser
import javax.swing.JFrame
import javax.swing.JLabel
import javax.swing.JList
import javax.swing.JPanel
import javax.swing.JScrollPane
import javax.swing.JSlider
import javax.swing.JTextField
import javax.swing.ListSelectionModel
import javax.swing.SwingUtilities
import javax.swing.event.DocumentEvent
import javax.swing.event.DocumentListener

/**
 * The settings window behind the tray's "Settings" item.
 *
 * It exists so the packaged daemon is usable by someone who never opens a text editor: every key in
 * `eveintel.properties` a user has reason to change is here, and the file stays the source of truth
 * -- saving rewrites those keys in place and leaves comments and hand-edits alone.
 *
 * One setting applies live and the rest do not, and the window says so rather than pretending a
 * saved value took effect. Channel selection is live because the tailer reads it from a flow; the
 * port, the log directory and the scope regions are each read once while building long-lived
 * objects.
 */
class SettingsWindow(
    private val configPath: Path,
    private val initial: Config,
    private val regionNames: List<String>,
    private val availableChannels: () -> List<ChannelInfo>,
    private val onChannelsChanged: (Set<String>) -> Unit,
    private val currentDisplay: () -> DisplaySettings,
    private val onDisplayChanged: (DisplaySettings) -> Unit,
    private val status: () -> List<String>,
    private val tabletUrl: () -> String?,
    private val onRestart: (() -> Unit)?,
) {
    private var frame: JFrame? = null

    private val statusLabel = JLabel()
    private val logsField = JTextField(initial.chatLogsDirectory.toString(), 34)
    private val portField = JTextField(initial.port.toString(), 6)
    private val startFromEnd = JCheckBox(
        "Ignore history written before the daemon started",
        initial.startFromEnd,
    )
    private val channelsPanel = JPanel().apply {
        layout = BoxLayout(this, BoxLayout.Y_AXIS)
        background = Theme.PANEL
        border = BorderFactory.createEmptyBorder(6, 8, 6, 8)
    }
    private val channelChecks = linkedMapOf<String, JCheckBox>()
    private val regionFilter = JTextField(18)
    private val regionModel = DefaultListModel<String>()
    private val regionList = JList(regionModel).apply {
        selectionMode = ListSelectionModel.MULTIPLE_INTERVAL_SELECTION
        visibleRowCount = 8
        background = Theme.PANEL
    }
    private val note = Theme.hint(" ")
    private val restartButton = JButton("Restart daemon").apply {
        isVisible = false
        addActionListener { onRestart?.invoke() }
    }

    // Applies live to every connected tablet on Save -- see [onDisplayChanged]. Values are reset
    // from [currentDisplay] in [refresh] each time the window opens, since another tablet could
    // have changed them since this window was last shown.
    private val fontScaleSlider = scaleSlider(1f)
    private val iconScaleSlider = scaleSlider(1f)
    private var textColor = parseHexColor(DisplaySettings.DEFAULT_TEXT_COLOR)
    private val textColorSwatch = JButton().apply {
        preferredSize = Dimension(28, 22)
        isFocusPainted = false
        background = textColor
        toolTipText = "Feed text colour"
        addActionListener {
            val chosen = JColorChooser.showDialog(frame, "Feed text colour", textColor)
            if (chosen != null) {
                textColor = chosen
                background = chosen
            }
        }
    }
    // Independent, not mutually exclusive -- both can be checked at once.
    private val glowCheck = JCheckBox("Glow").apply { background = Theme.BACKGROUND }
    private val dropShadowCheck = JCheckBox("Drop shadow").apply { background = Theme.BACKGROUND }

    /** Held separately from the list: filtering the visible rows must not clear the selection. */
    private val selectedRegions = linkedSetOf<String>().apply { addAll(initial.scopeRegions) }

    fun show() {
        val existing = frame
        if (existing != null) {
            refresh()
            existing.isVisible = true
            existing.state = JFrame.NORMAL
            existing.toFront()
            return
        }
        frame = build().also {
            refresh()
            it.isVisible = true
        }
    }

    private fun build(): JFrame = JFrame("EveDeck Intel").apply {
        defaultCloseOperation = JFrame.HIDE_ON_CLOSE
        runCatching {
            SettingsWindow::class.java.classLoader.getResourceAsStream("icon-256.png")?.use {
                iconImage = ImageIO.read(it)
            }
        }

        val body = JPanel(GridBagLayout()).apply {
            background = Theme.BACKGROUND
            border = BorderFactory.createEmptyBorder(16, 18, 14, 18)
        }
        var row = 0

        fun add(
            component: Component,
            gridx: Int,
            gridy: Int,
            width: Int = 1,
            fill: Int = GridBagConstraints.HORIZONTAL,
            weighty: Double = 0.0,
        ) {
            body.add(
                component,
                GridBagConstraints().also {
                    it.gridx = gridx
                    it.gridy = gridy
                    it.gridwidth = width
                    it.fill = fill
                    it.weightx = if (gridx == 1) 1.0 else 0.0
                    it.weighty = weighty
                    it.anchor = GridBagConstraints.NORTHWEST
                    it.insets = Insets(5, 0, 5, 0)
                },
            )
        }

        fun heading(text: String) = Theme.heading(text).apply {
            border = BorderFactory.createEmptyBorder(3, 0, 0, 14)
        }

        add(statusPanel(), 0, row++, width = 2)

        add(heading("Chat logs"), 0, row)
        add(
            transparent(BorderLayout(8, 0)).apply {
                add(logsField, BorderLayout.CENTER)
                add(JButton("Browse").apply { addActionListener { browseForLogs() } }, BorderLayout.EAST)
            },
            1,
            row++,
        )

        add(heading("Intel channels"), 0, row)
        add(
            transparent(BorderLayout(0, 5)).apply {
                add(
                    Theme.surface(
                        JScrollPane(channelsPanel).apply {
                            preferredSize = Dimension(380, 132)
                            border = BorderFactory.createEmptyBorder()
                        },
                    ),
                    BorderLayout.CENTER,
                )
                add(Theme.hint("Local is always read: it is how character location is tracked."), BorderLayout.SOUTH)
            },
            1,
            row++,
            fill = GridBagConstraints.BOTH,
            weighty = 1.0,
        )

        add(heading("Scope regions"), 0, row)
        add(
            transparent(BorderLayout(0, 5)).apply {
                add(
                    transparent(BorderLayout()).apply {
                        border = BorderFactory.createEmptyBorder(0, 0, 5, 0)
                        add(regionFilter, BorderLayout.CENTER)
                    },
                    BorderLayout.NORTH,
                )
                add(
                    Theme.surface(
                        JScrollPane(regionList).apply {
                            preferredSize = Dimension(380, 132)
                            border = BorderFactory.createEmptyBorder()
                        },
                    ),
                    BorderLayout.CENTER,
                )
                add(Theme.hint("The regions the channel covers. This is what lets an abbreviation resolve."), BorderLayout.SOUTH)
            },
            1,
            row++,
            fill = GridBagConstraints.BOTH,
            weighty = 1.0,
        )

        add(heading("Port"), 0, row)
        add(transparent(FlowLayout(FlowLayout.LEFT, 0, 0)).apply { add(portField) }, 1, row++)

        add(transparent(BorderLayout()), 0, row)
        add(
            transparent(FlowLayout(FlowLayout.LEFT, 0, 0)).apply {
                add(startFromEnd.apply { background = Theme.BACKGROUND })
            },
            1,
            row++,
        )

        add(heading("Feed appearance"), 0, row)
        add(
            transparent(GridBagLayout()).apply {
                fun labeled(label: String, component: Component, gy: Int) {
                    add(
                        Theme.hint(label),
                        GridBagConstraints().apply {
                            gridx = 0; gridy = gy; anchor = GridBagConstraints.WEST
                            insets = Insets(2, 0, 2, 10)
                        },
                    )
                    add(
                        component,
                        GridBagConstraints().apply {
                            gridx = 1; gridy = gy; anchor = GridBagConstraints.WEST
                            insets = Insets(2, 0, 2, 0)
                        },
                    )
                }
                labeled("Font size", fontScaleSlider, 0)
                labeled("Icon size", iconScaleSlider, 1)
                labeled(
                    "Text colour",
                    transparent(FlowLayout(FlowLayout.LEFT, 6, 0)).apply {
                        add(textColorSwatch)
                        add(Theme.hint("(system-name colour and status colours are unaffected)"))
                    },
                    2,
                )
                labeled(
                    "Text effect",
                    transparent(FlowLayout(FlowLayout.LEFT, 8, 0)).apply {
                        add(glowCheck)
                        add(dropShadowCheck)
                    },
                    3,
                )
            },
            1,
            row++,
        )
        add(
            Theme.hint("Applies immediately to every connected tablet — no restart needed."),
            1,
            row++,
        )

        add(
            transparent(BorderLayout()).apply {
                border = BorderFactory.createEmptyBorder(12, 0, 0, 0)
                add(note, BorderLayout.WEST)
                add(
                    transparent(FlowLayout(FlowLayout.RIGHT, 8, 0)).apply {
                        add(restartButton)
                        add(JButton("Close").apply { addActionListener { frame?.isVisible = false } })
                        add(Theme.primary(JButton("Save").apply { addActionListener { save() } }))
                    },
                    BorderLayout.EAST,
                )
            },
            0,
            row,
            width = 2,
        )

        regionFilter.document.addDocumentListener(
            object : DocumentListener {
                override fun insertUpdate(event: DocumentEvent) = refreshRegions()
                override fun removeUpdate(event: DocumentEvent) = refreshRegions()
                override fun changedUpdate(event: DocumentEvent) = refreshRegions()
            },
        )
        regionList.addListSelectionListener { event ->
            if (event.valueIsAdjusting) return@addListSelectionListener
            // Only the visible slice can change, so reconcile against it rather than replacing the
            // whole selection: a region filtered out of view stays selected.
            val visible = (0 until regionModel.size()).map { regionModel.get(it) }
            val chosen = regionList.selectedValuesList.toSet()
            visible.forEach { if (it in chosen) selectedRegions.add(it) else selectedRegions.remove(it) }
        }

        contentPane.background = Theme.BACKGROUND
        contentPane.add(body)
        pack()
        minimumSize = Dimension(640, 0)
        setLocationRelativeTo(null)
    }

    private fun transparent(layout: java.awt.LayoutManager) = JPanel(layout).apply {
        background = Theme.BACKGROUND
        isOpaque = true
    }

    private fun statusPanel() = transparent(BorderLayout(12, 0)).apply {
        border = Theme.divider()
        add(statusLabel, BorderLayout.CENTER)
        add(
            JButton("Copy URL").apply {
                addActionListener {
                    val url = tabletUrl() ?: return@addActionListener
                    Toolkit.getDefaultToolkit().systemClipboard.setContents(StringSelection(url), null)
                }
            },
            BorderLayout.EAST,
        )
    }

    private fun refresh() {
        val lines = status()
        // The address is the one thing a user has come here to read; everything else is context.
        statusLabel.text = buildString {
            append("<html><body style='font-family:sans-serif'>")
            lines.forEachIndexed { index, line ->
                val colour = if (index == 0) Theme.ACCENT.rgbHex() else Theme.MUTED.rgbHex()
                append("<div style='color:$colour'>").append(line).append("</div>")
            }
            append("</body></html>")
        }
        refreshChannels()
        refreshRegions()

        val display = currentDisplay()
        fontScaleSlider.value = (display.fontScale * 100).toInt().coerceIn(75, 175)
        iconScaleSlider.value = (display.iconScale * 100).toInt().coerceIn(75, 175)
        textColor = parseHexColor(display.textColor)
        textColorSwatch.background = textColor
        glowCheck.isSelected = display.glowEnabled
        dropShadowCheck.isSelected = display.dropShadowEnabled
    }

    private fun refreshChannels() {
        val discovered = availableChannels().filterNot { it.reserved }.map { it.name }
        // A selected channel whose log files have aged out still has to be shown, or saving the
        // window would silently drop it.
        val names = (discovered + channelChecks.filterValues { it.isSelected }.keys + initial.intelChannels)
            .distinct()
            .sorted()
        if (names == channelChecks.keys.toList()) return

        val selected = channelChecks.filterValues { it.isSelected }.keys + initial.intelChannels
        channelChecks.clear()
        channelsPanel.removeAll()
        if (names.isEmpty()) {
            channelsPanel.add(
                Theme.hint("No chat logs found yet. Join a channel in game, then reopen this."),
            )
        }
        names.forEach { name ->
            val check = JCheckBox(name, name in selected).apply { background = Theme.PANEL }
            channelChecks[name] = check
            channelsPanel.add(check)
        }
        channelsPanel.revalidate()
        channelsPanel.repaint()
    }

    private fun refreshRegions() {
        val filter = regionFilter.text.trim().lowercase()
        val visible = regionNames.filter { filter.isEmpty() || it.lowercase().contains(filter) }
        regionModel.clear()
        visible.forEach { regionModel.addElement(it) }
        regionList.selectedIndices = visible.withIndex()
            .filter { it.value in selectedRegions }
            .map { it.index }
            .toIntArray()
    }

    private fun browseForLogs() {
        val chooser = JFileChooser(logsField.text.takeIf { it.isNotBlank() }).apply {
            fileSelectionMode = JFileChooser.DIRECTORIES_ONLY
            dialogTitle = "EVE chat logs folder"
        }
        if (chooser.showOpenDialog(frame) == JFileChooser.APPROVE_OPTION) {
            logsField.text = chooser.selectedFile.absolutePath
        }
    }

    private fun save() {
        val port = portField.text.trim().toIntOrNull()
        if (port == null || port !in 1..65535) {
            note.foreground = Theme.WARN
            note.text = "Port must be a number between 1 and 65535."
            return
        }
        val channels = channelChecks.filterValues { it.isSelected }.keys.sorted()

        Config.save(
            configPath,
            mapOf(
                "chatlogs.dir" to logsField.text.trim(),
                "intel.channels" to channels.joinToString(","),
                "scope.regions" to selectedRegions.sorted().joinToString(","),
                "server.port" to port.toString(),
                "logs.startFromEnd" to startFromEnd.isSelected.toString(),
            ),
        )
        onChannelsChanged(channels.toSet())

        onDisplayChanged(
            DisplaySettings(
                fontScale = fontScaleSlider.value / 100f,
                iconScale = iconScaleSlider.value / 100f,
                textColor = textColor.rgbHex(),
                glowEnabled = glowCheck.isSelected,
                dropShadowEnabled = dropShadowCheck.isSelected,
            ).coerced(),
        )

        val needsRestart = logsField.text.trim() != initial.chatLogsDirectory.toString() ||
            port != initial.port ||
            selectedRegions != initial.scopeRegions ||
            startFromEnd.isSelected != initial.startFromEnd
        note.foreground = if (needsRestart) Theme.WARN else Theme.MUTED
        note.text = if (needsRestart) {
            "Saved. The port, log folder and regions apply on restart."
        } else {
            "Saved. Channel selection is live."
        }
        restartButton.isVisible = needsRestart && onRestart != null
        frame?.let { SwingUtilities.invokeLater { it.revalidate() } }
    }

    private fun java.awt.Color.rgbHex() = String.format("#%02x%02x%02x", red, green, blue)

    private fun parseHexColor(hex: String): java.awt.Color =
        runCatching { java.awt.Color.decode(hex) }.getOrDefault(java.awt.Color(0xd7, 0xe1, 0xec))

    /** 75%-175%, matching [DisplaySettings]'s clamp range. */
    private fun scaleSlider(initial: Float) = JSlider(75, 175, (initial * 100).toInt().coerceIn(75, 175)).apply {
        background = Theme.BACKGROUND
        foreground = Theme.MUTED
        preferredSize = Dimension(180, preferredSize.height)
        majorTickSpacing = 25
        paintTicks = true
        toolTipText = "$value%"
        addChangeListener { toolTipText = "$value%" }
    }
}
