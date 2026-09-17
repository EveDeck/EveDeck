package dev.eveintel.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import dev.eveintel.android.IntelUiState
import dev.eveintel.android.IntelViewModel

@Composable
fun SettingsScreen(state: IntelUiState, viewModel: IntelViewModel, modifier: Modifier = Modifier) {
    val settings = state.settings ?: return

    var host by remember(settings.serverHost) { mutableStateOf(settings.serverHost) }
    var port by remember(settings.serverPort) { mutableStateOf(settings.serverPort.toString()) }

    Column(
        modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(20.dp),
    ) {
        Section("Daemon") {
            Text(
                "The PC running EVE prints its address when the daemon starts.",
                color = IntelColors.Muted,
                fontSize = 12.sp,
            )
            Row(verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = host,
                    onValueChange = { host = it },
                    label = { Text("Host") },
                    singleLine = true,
                    modifier = Modifier.weight(1f),
                )
                Spacer(Modifier.width(12.dp))
                OutlinedTextField(
                    value = port,
                    onValueChange = { port = it.filter(Char::isDigit).take(5) },
                    label = { Text("Port") },
                    singleLine = true,
                    modifier = Modifier.width(110.dp),
                )
            }
            Button(
                onClick = { viewModel.setServer(host, port.toIntOrNull() ?: 31337) },
                enabled = host.isNotBlank(),
            ) {
                Text("Connect")
            }
        }

        Section("Intel channels") {
            Text(
                "Every channel the daemon can see logs for. Tick the ones carrying intel. " +
                    "Local is always read, for your position.",
                color = IntelColors.Muted,
                fontSize = 12.sp,
            )
            if (state.channels.available.isEmpty()) {
                Text(
                    if (state.connection == dev.eveintel.android.ConnectionState.CONNECTED) {
                        "Daemon reported no channels."
                    } else {
                        "Connect to the daemon to see channels."
                    },
                    color = IntelColors.Muted,
                    fontSize = 13.sp,
                )
            }
            state.channels.available.forEach { channel ->
                val selected = channel.name in state.channels.selected
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(4.dp))
                        .background(IntelColors.Surface)
                        .clickable(enabled = !channel.reserved) {
                            viewModel.toggleChannel(channel.name, !selected)
                        }
                        .padding(horizontal = 12.dp, vertical = 8.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(
                            channel.name,
                            color = when {
                                channel.reserved -> IntelColors.Muted
                                selected -> IntelColors.Accent
                                else -> IntelColors.OnSurface
                            },
                            fontFamily = FontFamily.Monospace,
                            fontWeight = if (selected) FontWeight.Bold else FontWeight.Normal,
                        )
                        Text(
                            if (channel.reserved) {
                                "always read — location tracking"
                            } else {
                                "${channel.fileCount} log file" + if (channel.fileCount == 1) "" else "s"
                            },
                            color = IntelColors.Muted,
                            fontSize = 11.sp,
                        )
                    }
                    if (!channel.reserved) {
                        Switch(
                            checked = selected,
                            onCheckedChange = { viewModel.toggleChannel(channel.name, it) },
                        )
                    }
                }
            }
        }

        Section("Alert characters") {
            Text(
                "Jump distance is measured from whichever of these characters is closest, using " +
                    "the system their Local channel last reported.",
                color = IntelColors.Muted,
                fontSize = 12.sp,
            )
            CharacterPickerRows(state, viewModel)
        }

        Section("Alerts") {
            Text(
                "Notify on hostile intel within ${settings.alertJumpRadius} jump" +
                    if (settings.alertJumpRadius == 1) "" else "s",
                color = IntelColors.OnSurface,
            )
            Slider(
                value = settings.alertJumpRadius.toFloat(),
                onValueChange = { viewModel.setAlertRadius(it.toInt()) },
                valueRange = 0f..15f,
                steps = 14,
            )
            Text(
                if (settings.alertJumpRadius == 0) "Alerts are off." else "0 turns alerts off.",
                color = IntelColors.Muted,
                fontSize = 12.sp,
            )
            ToggleRow("Also alert on 'clear' reports", settings.alertOnClear, viewModel::setAlertOnClear)
            ToggleRow("Keep the screen on", settings.keepScreenOn, viewModel::setKeepScreenOn)
        }

        Section("About") {
            Text(
                "Reads your alliance intel channels from the EVE client's chat logs on the PC and " +
                    "mirrors them here. LAN only — no account, no TLS, nothing leaves your network.",
                color = IntelColors.Muted,
                fontSize = 12.sp,
            )
        }
    }
}

@Composable
private fun Section(title: String, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text(
            title.uppercase(),
            color = IntelColors.Accent,
            fontSize = 12.sp,
            fontWeight = FontWeight.Bold,
        )
        content()
    }
}

@Composable
private fun ToggleRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(
        Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, color = IntelColors.OnSurface, fontSize = 14.sp)
        Switch(checked = checked, onCheckedChange = onChange)
    }
}
