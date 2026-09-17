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
import androidx.compose.material.icons.filled.List
import androidx.compose.material.icons.filled.Public
import androidx.compose.material.icons.filled.Settings
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
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
import dev.eveintel.android.ui.EveDeckBackground
import dev.eveintel.android.ui.EveIntelTheme
import dev.eveintel.android.ui.FeedScreen
import dev.eveintel.android.ui.IntelColors
import dev.eveintel.android.ui.MapScreen
import dev.eveintel.android.ui.SettingsScreen
import kotlinx.coroutines.delay

private enum class Tab(val label: String, val icon: ImageVector) {
    FEED("Intel", Icons.Filled.List),
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

            EveIntelTheme {
                EveDeckBackground {
                Scaffold(
                    containerColor = androidx.compose.ui.graphics.Color.Transparent,
                    topBar = { StatusBar(state) },
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

@Composable
private fun StatusBar(state: IntelUiState) {
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

        val location = state.primaryLocation
        if (location != null) {
            Column(horizontalAlignment = Alignment.End) {
                Text(
                    location.systemName,
                    color = IntelColors.You,
                    fontFamily = FontFamily.Monospace,
                    fontWeight = FontWeight.Bold,
                    fontSize = 15.sp,
                )
                Text(location.characterName, color = IntelColors.Muted, fontSize = 11.sp)
            }
        } else {
            Text(
                if (state.locations.isEmpty()) "no characters" else "pick a character",
                color = IntelColors.Muted,
                fontSize = 12.sp,
            )
        }
    }
}
