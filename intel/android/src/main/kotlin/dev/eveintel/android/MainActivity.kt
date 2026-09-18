package dev.eveintel.android

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Public
import androidx.compose.material.icons.filled.Settings
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import kotlinx.coroutines.delay
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.compose.foundation.clickable
import androidx.compose.foundation.shape.RoundedCornerShape
import dev.eveintel.android.ui.CharacterPickerDialog
import dev.eveintel.android.ui.EveDeckBackground
import dev.eveintel.android.ui.EveIntelTheme
import dev.eveintel.android.ui.FeedScreen
import dev.eveintel.android.ui.IntelColors
import dev.eveintel.android.ui.MapScreen
import dev.eveintel.android.ui.SettingsScreen
import kotlinx.coroutines.delay

private enum class Tab(val label: String, val icon: ImageVector) {
    FEED("Intel", Icons.AutoMirrored.Filled.List),
    MAP("Map", Icons.Filled.Public),
    SETTINGS("Settings", Icons.Filled.Settings),
}

class MainActivity : ComponentActivity() {

    private val notificationPermission = registerForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { /* Alerts simply stay silent if declined. */ }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        requestNotificationPermission()
        IntelService.start(this)

        setContent {
            val viewModel: IntelViewModel = viewModel()
            val state by viewModel.state.collectAsStateWithLifecycle()

            // Drives relative timestamps and map fading without recomposing on every frame.
            var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
            LaunchedEffect(Unit) {
                while (true) {
                    now = System.currentTimeMillis()
                    delay(1_000)
                }
            }

            LaunchedEffect(state.settings?.keepScreenOn) {
                if (state.settings?.keepScreenOn == true) {
                    window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                } else {
                    window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                }
            }

            var tab by remember { mutableStateOf(Tab.FEED) }
            var pickingCharacters by remember { mutableStateOf(false) }

            EveIntelTheme {
                EveDeckBackground {
                Scaffold(
                    containerColor = androidx.compose.ui.graphics.Color.Transparent,
                    topBar = { StatusBar(state) { pickingCharacters = true } },
                    bottomBar = {
                        NavigationBar(containerColor = IntelColors.Chrome) {
                            Tab.entries.forEach { entry ->
                                NavigationBarItem(
                                    selected = tab == entry,
                                    onClick = { tab = entry },
                                    icon = { Icon(entry.icon, contentDescription = entry.label) },
                                    label = { Text(entry.label) },
                                )
                            }
                        }
                    },
                ) { padding ->
                    Box(Modifier.padding(padding)) {
                        when (tab) {
                            Tab.FEED -> FeedScreen(state, now)
                            Tab.MAP -> MapScreen(state, now)
                            Tab.SETTINGS -> SettingsScreen(state, viewModel)
                        }
                        if (pickingCharacters) {
                            CharacterPickerDialog(state, viewModel) { pickingCharacters = false }
                        }
                    }
                }
                }
            }
        }
    }

    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) return
        val granted = ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) ==
            PackageManager.PERMISSION_GRANTED
        if (!granted) notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
    }
}

// EVE Online's in-game clock is always UTC ("EVE time"), and downtime starts 11:00 UTC and
// nominally runs to ~11:15-11:30 (longer on patch days). The feed goes quiet during that window
// because nothing new is being logged, not because anything is broken -- so this needs to read as
// "the server is down for maintenance", not as a dead connection.
private val EVE_CLOCK_FORMATTER: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm").withZone(ZoneOffset.UTC)

@Composable
private fun EveClock() {
    var now by remember { mutableStateOf(Instant.now()) }
    LaunchedEffect(Unit) {
        while (true) {
            now = Instant.now()
            val utc = now.atZone(ZoneOffset.UTC)
            val millisToNextMinute = 60_000L - (utc.second * 1_000L + utc.nano / 1_000_000L)
            delay(millisToNextMinute)
        }
    }

    val utc = now.atZone(ZoneOffset.UTC)
    val inDowntime = utc.hour == 11 && utc.minute <= 30
    Text(
        text = EVE_CLOCK_FORMATTER.format(now) + if (inDowntime) " · DOWNTIME" else "",
        color = if (inDowntime) IntelColors.Warning else IntelColors.Muted,
        fontFamily = FontFamily.Monospace,
        fontWeight = if (inDowntime) FontWeight.Bold else FontWeight.Normal,
        fontSize = 12.sp,
    )
}

@Composable
private fun StatusBar(state: IntelUiState, onPickCharacters: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .background(IntelColors.Chrome)
            .windowInsetsPadding(WindowInsets.systemBars)
            .padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .size(9.dp)
                .clip(CircleShape)
                .background(
                    when (state.connection) {
                        ConnectionState.CONNECTED -> IntelColors.Clear
                        ConnectionState.CONNECTING -> IntelColors.Warning
                        ConnectionState.DISCONNECTED -> IntelColors.Hostile
                    },
                ),
        )
        Spacer(Modifier.width(10.dp))
        Text("INTEL", color = IntelColors.OnSurface, fontWeight = FontWeight.Bold, fontSize = 15.sp)

        Spacer(Modifier.weight(1f))

        EveClock()
        Spacer(Modifier.width(14.dp))

        // The whole right-hand block is the shortcut into the character picker: this is where a
        // user looks to ask "measured from where?", so it is where the answer is changed.
        val location = state.primaryLocation
        val extras = (state.settings?.alertCharacters?.size ?: 0) - 1
        Column(
            Modifier
                .clip(RoundedCornerShape(6.dp))
                .clickable(onClick = onPickCharacters)
                .padding(horizontal = 8.dp, vertical = 4.dp),
            horizontalAlignment = Alignment.End,
        ) {
            if (location != null) {
                Text(
                    location.systemName,
                    color = IntelColors.You,
                    fontFamily = FontFamily.Monospace,
                    fontWeight = FontWeight.Bold,
                    fontSize = 15.sp,
                )
                Text(
                    location.characterName + if (extras > 0) "  +$extras" else "",
                    color = IntelColors.Muted,
                    fontSize = 11.sp,
                )
            } else if (state.locations.isEmpty()) {
                Text("no characters yet", color = IntelColors.Muted, fontSize = 12.sp)
            } else {
                // Warning colour, because in this state the alert radius is doing nothing at all.
                Text(
                    "tap to pick characters",
                    color = IntelColors.Warning,
                    fontWeight = FontWeight.Bold,
                    fontSize = 13.sp,
                )
            }
        }
    }
}
