package dev.eveintel.model

import kotlinx.serialization.Serializable

/**
 * How the tablet's feed renders, synced from the daemon rather than kept per-device.
 *
 * One set of PC-side controls rather than the same knobs repeated on every tablet: the settings
 * window is easier to work a slider in than a tablet propped up across the desk, and a household
 * running more than one tablet (see [[reference-intel-test-devices]]) gets them to match for free.
 * The tablet's own settings screen edits the same values and sends them back -- either side can
 * change it, and the daemon is just where it is persisted.
 *
 * The four hostile/clear/warning/jump-distance colours in `IntelColors` are deliberately NOT here:
 * they carry meaning ("closer is louder"), and letting them be recoloured away would break the
 * at-a-glance reading the whole feed is designed around. [textColor] only affects body text that
 * has no semantic colour of its own -- ship names, pilot names.
 */
@Serializable
data class DisplaySettings(
    /** Multiplies every feed font size. 1.0 is the shipped default sizing. */
    val fontScale: Float = 1f,
    /** Multiplies ship-icon and portrait image size. */
    val iconScale: Float = 1f,
    /** Hex RGB, e.g. "#D7E1EC". Body text only -- see class doc for what stays fixed. */
    val textColor: String = DEFAULT_TEXT_COLOR,
    /** Independent, not mutually exclusive -- both can render on the same text at once. */
    val glowEnabled: Boolean = false,
    val dropShadowEnabled: Boolean = false,
) {
    /** Keeps a corrupt properties file or a bad client value from rendering an unreadable feed. */
    fun coerced(): DisplaySettings = copy(
        fontScale = fontScale.coerceIn(MIN_SCALE, MAX_SCALE),
        iconScale = iconScale.coerceIn(MIN_SCALE, MAX_SCALE),
        textColor = if (HEX_COLOR.matches(textColor)) textColor else DEFAULT_TEXT_COLOR,
    )

    companion object {
        const val DEFAULT_TEXT_COLOR = "#D7E1EC"
        const val MIN_SCALE = 0.75f
        const val MAX_SCALE = 1.75f
        private val HEX_COLOR = Regex("""^#[0-9A-Fa-f]{6}$""")
    }
}
