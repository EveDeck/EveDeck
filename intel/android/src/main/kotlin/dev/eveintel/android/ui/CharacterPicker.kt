package dev.eveintel.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import dev.eveintel.android.IntelUiState
import dev.eveintel.android.IntelViewModel

/**
 * The character multi-select, shared by the settings screen and the status-bar shortcut.
 *
 * One composable rather than two copies: which characters the alert radius measures from is the
 * setting most likely to be wrong, and two lists that could drift apart is how it stays wrong.
 */
@Composable
fun CharacterPickerRows(state: IntelUiState, viewModel: IntelViewModel) {
    val selected = state.settings?.alertCharacters.orEmpty()

    if (state.locations.isEmpty()) {
        Text(
            "No characters seen yet. They appear once the daemon reads a Local channel.",
            color = IntelColors.Muted,
            fontSize = 13.sp,
        )
        return
    }

    if (selected.isEmpty()) {
        // Without an origin nothing can be measured against the radius, so every hostile report
        // alerts. Saying so beats a radius that looks set and quietly does nothing.
        Text(
            "None selected — the jump radius cannot be applied, so every hostile report alerts.",
            color = IntelColors.Warning,
            fontSize = 13.sp,
        )
    }

    // Most recently moved first. A character parked weeks ago is still technically somewhere, but
    // the ones worth alerting on are the ones being flown now, and an account set can run to a
    // dozen or more names.
    state.locations.values.sortedByDescending { it.sinceMillis }.forEach { location ->
        val isSelected = location.characterName in selected
        Row(
            Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(4.dp))
                .background(if (isSelected) IntelColors.Accent.copy(alpha = 0.15f) else IntelColors.Surface)
                .clickable { viewModel.toggleAlertCharacter(location.characterName) }
                .padding(12.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            Text(
                (if (isSelected) "✓  " else "  ") + location.characterName,
                color = if (isSelected) IntelColors.Accent else IntelColors.OnSurface,
                fontWeight = if (isSelected) FontWeight.Bold else FontWeight.Normal,
            )
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    location.systemName,
                    color = IntelColors.Muted,
                    fontFamily = FontFamily.Monospace,
                )
                // How long ago they were last seen moving. Without it a stale position looks
                // exactly like a live one, which is the whole difficulty in a long character list.
                Text(
                    sinceLabel(location.sinceMillis),
                    color = IntelColors.Muted,
                    fontSize = 11.sp,
                )
            }
        }
    }
}

/** Coarse age of a location. Precision past "hours" tells the reader nothing useful here. */
private fun sinceLabel(millis: Long): String {
    val minutes = (System.currentTimeMillis() - millis) / 60_000
    return when {
        minutes < 0 -> ""
        minutes < 1 -> "now"
        minutes < 60 -> "${minutes}m"
        minutes < 60 * 24 -> "${minutes / 60}h"
        else -> "${minutes / (60 * 24)}d"
    }
}

/**
 * The same picker as a dialog, reachable from the status bar.
 *
 * Buried three taps deep in Settings, this went untouched and the alert radius silently did
 * nothing as a result. It belongs next to the thing it explains: the system name in the header.
 */
@Composable
fun CharacterPickerDialog(state: IntelUiState, viewModel: IntelViewModel, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = IntelColors.Chrome,
        title = { Text("Alert characters", color = IntelColors.OnSurface) },
        text = {
            Column(
                Modifier
                    .heightIn(max = 320.dp)
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Text(
                    "Jump distance is measured from whichever of these is closest.",
                    color = IntelColors.Muted,
                    fontSize = 12.sp,
                )
                CharacterPickerRows(state, viewModel)
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) { Text("Done", color = IntelColors.Accent) }
        },
    )
}
