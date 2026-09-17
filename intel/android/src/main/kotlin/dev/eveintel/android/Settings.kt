package dev.eveintel.android

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * User settings, persisted in SharedPreferences.
 *
 * Exposed as a [StateFlow] so the UI and the foreground service see the same values without an
 * observer registry.
 */
class Settings(context: Context) {

    private val prefs = context.applicationContext.getSharedPreferences("eveintel", Context.MODE_PRIVATE)

    private val _state = MutableStateFlow(read())
    val state: StateFlow<Snapshot> = _state.asStateFlow()

    data class Snapshot(
        val serverHost: String,
        val serverPort: Int,
        val followedCharacter: String?,
        /** Alert when hostile intel lands this many jumps away or closer. 0 disables. */
        val alertJumpRadius: Int,
        val alertOnClear: Boolean,
        val keepScreenOn: Boolean,
    ) {
        val webSocketUrl: String get() = "ws://$serverHost:$serverPort/intel"
        val isConfigured: Boolean get() = serverHost.isNotBlank()
    }

    private fun read() = Snapshot(
        serverHost = prefs.getString(KEY_HOST, "") ?: "",
        serverPort = prefs.getInt(KEY_PORT, 31337),
        followedCharacter = prefs.getString(KEY_CHARACTER, null),
        alertJumpRadius = prefs.getInt(KEY_RADIUS, 5),
        alertOnClear = prefs.getBoolean(KEY_ALERT_CLEAR, false),
        keepScreenOn = prefs.getBoolean(KEY_KEEP_SCREEN_ON, true),
    )

    fun update(transform: (Snapshot) -> Snapshot) {
        val next = transform(_state.value)
        prefs.edit().apply {
            putString(KEY_HOST, next.serverHost)
            putInt(KEY_PORT, next.serverPort)
            putString(KEY_CHARACTER, next.followedCharacter)
            putInt(KEY_RADIUS, next.alertJumpRadius)
            putBoolean(KEY_ALERT_CLEAR, next.alertOnClear)
            putBoolean(KEY_KEEP_SCREEN_ON, next.keepScreenOn)
        }.apply()
        _state.value = next
    }

    private companion object {
        const val KEY_HOST = "server.host"
        const val KEY_PORT = "server.port"
        const val KEY_CHARACTER = "character"
        const val KEY_RADIUS = "alert.radius"
        const val KEY_ALERT_CLEAR = "alert.clear"
        const val KEY_KEEP_SCREEN_ON = "keepScreenOn"
    }
}
