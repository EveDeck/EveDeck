package dev.eveintel.android.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/**
 * Deliberately dark and low-contrast in the chrome, with saturation reserved for intel state.
 * This is read at a glance, in a dark room, next to a game client.
 */
object IntelColors {
    val Background = Color(0xFF0B0F14)

    // Translucent so the EveDeck artwork reads through the chrome without washing out the text.
    val Surface = Color(0x99131A22)
    val SurfaceRaised = Color(0xCC1B2530)
    val Chrome = Color(0xCC0B0F14)
    val OnSurface = Color(0xFFD7E1EC)
    val Muted = Color(0xFF7A8899)

    val Hostile = Color(0xFFFF5252)
    val HostileDim = Color(0xFF7F2B2B)
    val Clear = Color(0xFF4CAF50)
    val Warning = Color(0xFFFFB300)
    val Accent = Color(0xFF4FC3F7)
    val You = Color(0xFFB388FF)

    /** Jump-distance colouring: closer is louder. */
    fun forJumps(jumps: Int?): Color = when {
        jumps == null -> Muted
        jumps == 0 -> Hostile
        jumps <= 2 -> Warning
        jumps <= 5 -> Accent
        else -> Muted
    }
}

private val DarkScheme = darkColorScheme(
    primary = IntelColors.Accent,
    onPrimary = IntelColors.Background,
    background = IntelColors.Background,
    onBackground = IntelColors.OnSurface,
    surface = IntelColors.Surface,
    onSurface = IntelColors.OnSurface,
    surfaceVariant = IntelColors.SurfaceRaised,
    error = IntelColors.Hostile,
)

@Composable
fun EveIntelTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = DarkScheme, content = content)
}
