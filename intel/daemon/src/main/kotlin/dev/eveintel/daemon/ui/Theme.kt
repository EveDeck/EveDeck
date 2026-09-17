package dev.eveintel.daemon.ui

import com.formdev.flatlaf.FlatDarkLaf
import com.formdev.flatlaf.FlatLaf
import java.awt.Color
import java.awt.Font
import javax.swing.BorderFactory
import javax.swing.JButton
import javax.swing.JComponent
import javax.swing.JLabel
import javax.swing.UIManager
import javax.swing.border.Border

/**
 * EveDeck's palette, applied to Swing.
 *
 * The daemon is a sibling of the desktop app, not a separate product, and a stock grey Windows
 * dialog next to EveDeck's own chrome reads as something a different person wrote. The colours here
 * are the same tokens the app and the site use, so all three agree.
 *
 * FlatLaf does the component painting; this only supplies values. Nothing outside this file knows
 * what the colours are.
 */
object Theme {

    val BACKGROUND = Color(0x0b, 0x0f, 0x14)
    val PANEL = Color(0x0d, 0x14, 0x20)
    val PANEL_RAISED = Color(0x12, 0x1b, 0x2a)
    val TEXT = Color(0xe7, 0xf0, 0xff)
    val MUTED = Color(0x9f, 0xb2, 0xc9)
    val ACCENT = Color(0x22, 0xc3, 0xff)
    val ACCENT_DEEP = Color(0x0b, 0x4f, 0xa8)
    val LINE = Color(0x1c, 0x2a, 0x3d)
    val WARN = Color(0xff, 0xb4, 0x4e)

    private var installed = false

    /** Safe to call more than once and safe to fail: an unthemed window still works. */
    fun install() {
        if (installed) return
        installed = true
        runCatching {
            // See themes/FlatDarkLaf.properties: anything FlatLaf bakes into an icon has to be
            // registered before the look and feel is installed, not put into UIManager after it.
            FlatLaf.registerCustomDefaultsSource("themes")
            FlatDarkLaf.setup()

            UIManager.put("Panel.background", BACKGROUND)
            UIManager.put("OptionPane.background", BACKGROUND)
            UIManager.put("RootPane.background", BACKGROUND)
            UIManager.put("Label.foreground", TEXT)
            UIManager.put("OptionPane.messageForeground", TEXT)

            UIManager.put("TextField.background", PANEL)
            UIManager.put("TextField.foreground", TEXT)
            UIManager.put("TextField.caretForeground", ACCENT)
            UIManager.put("TextField.borderColor", LINE)
            UIManager.put("TextField.focusedBorderColor", ACCENT)
            UIManager.put("Component.focusColor", ACCENT)
            UIManager.put("Component.focusedBorderColor", ACCENT)
            UIManager.put("Component.borderColor", LINE)
            UIManager.put("Component.arc", 8)
            UIManager.put("TextComponent.arc", 8)
            UIManager.put("Button.arc", 10)

            UIManager.put("List.background", PANEL)
            UIManager.put("List.foreground", TEXT)
            UIManager.put("List.selectionBackground", ACCENT_DEEP)
            UIManager.put("List.selectionForeground", TEXT)
            UIManager.put("ScrollPane.background", PANEL)
            UIManager.put("Viewport.background", PANEL)
            UIManager.put("ScrollBar.thumb", LINE)
            UIManager.put("ScrollBar.track", PANEL)

            UIManager.put("CheckBox.background", PANEL)
            UIManager.put("CheckBox.foreground", TEXT)

            UIManager.put("Button.background", PANEL_RAISED)
            UIManager.put("Button.foreground", TEXT)
            UIManager.put("Button.hoverBackground", LINE)
            UIManager.put("Button.default.background", ACCENT_DEEP)
            UIManager.put("Button.default.foreground", TEXT)
            UIManager.put("Button.default.focusedBackground", ACCENT_DEEP)

            UIManager.put("TitlePane.background", BACKGROUND)
            UIManager.put("TitlePane.foreground", TEXT)
            UIManager.put("TitlePane.inactiveBackground", BACKGROUND)
            UIManager.put("TitlePane.inactiveForeground", MUTED)
            // Paints the window frame itself dark, so the title bar does not flash white.
            UIManager.put("TitlePane.unifiedBackground", true)
        }
    }

    /** A section heading: the only uppercase text in the window, and the only tracked text. */
    fun heading(text: String) = JLabel(text.uppercase()).apply {
        foreground = MUTED
        font = font.deriveFont(Font.BOLD, font.size2D - 1f)
    }

    fun hint(text: String) = JLabel(text).apply {
        foreground = MUTED
        font = font.deriveFont(font.size2D - 1f)
    }

    fun primary(button: JButton) = button.apply {
        putClientProperty("JButton.buttonType", "default")
        background = ACCENT_DEEP
        foreground = TEXT
    }

    /** A framed content area: the list boxes read as one surface rather than as raw scroll panes. */
    fun surface(component: JComponent): JComponent = component.apply {
        border = BorderFactory.createLineBorder(LINE)
        background = PANEL
    }

    fun divider(): Border = BorderFactory.createCompoundBorder(
        BorderFactory.createMatteBorder(0, 0, 1, 0, LINE),
        BorderFactory.createEmptyBorder(0, 0, 10, 0),
    )
}
