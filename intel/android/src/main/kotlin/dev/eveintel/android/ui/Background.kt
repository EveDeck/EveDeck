package dev.eveintel.android.ui

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.painterResource
import dev.eveintel.android.R

/**
 * The EveDeck splash background, behind the whole app.
 *
 * Scrimmed the same way the desktop splash does it: the artwork is busy and intel text has to stay
 * legible at a glance across the room, so a vertical dark gradient sits between the two. Without it
 * the feed becomes unreadable over the bright areas of the image.
 */
@Composable
fun EveDeckBackground(content: @Composable () -> Unit) {
    Box(Modifier.fillMaxSize().background(IntelColors.Background)) {
        Image(
            painter = painterResource(R.drawable.bg_evedeck),
            contentDescription = null,
            contentScale = ContentScale.Crop,
            modifier = Modifier.fillMaxSize(),
        )
        Box(
            Modifier
                .fillMaxSize()
                .background(
                    // Light enough that the artwork reads, heavy enough that monospace system
                    // names stay legible over the bright areas. Tuned against a real screenshot:
                    // at 0.85 the image was invisible.
                    Brush.verticalGradient(
                        listOf(
                            IntelColors.Background.copy(alpha = 0.35f),
                            IntelColors.Background.copy(alpha = 0.55f),
                            IntelColors.Background.copy(alpha = 0.70f),
                        ),
                    ),
                ),
        )
        content()
    }
}
