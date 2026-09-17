package dev.eveintel.android

import android.content.Context
import android.content.SharedPreferences
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * User settings, persisted in SharedPreferences.
 *
 * The state is **process-wide**, not per instance. The foreground service and the view model each
 * construct a `Settings`, and when they each held their own [MutableStateFlow] the service simply
 * never saw a change made in the UI: it kept alerting on the jump radius it had read at startup.
 * Sharing one flow is what makes this class's promise — that the UI and the service see the same
 * values — actually true.
 */
class Settings(context: Context) {

    private val prefs = context.applicationContext.getSharedPreferences("eveintel", Context.MODE_PRIVATE)

    private val _state = shared(prefs)
    val state: StateFlow<Snapshot> = _state.asStateFlow()

    data class Snapshot(
        val serverHost: String,
        val serverPort: Int,
        /**
         * Characters whose position the alert radius is measured from. Alerting uses whichever is
         * closest, so a multiboxer gets one alert for the fleet rather than one per pilot.
         */
        val alertCharacters: Set<String>,
        /** Alert when hostile intel lands this many jumps away or closer. 0 disables. */
        val alertJumpRadius: Int,
        val alertOnClear: Boolean,
        val keepScreenOn: Boolean,
    ) {
        val webSocketUrl: String get() = "ws://$serverHost:$serverPort/intel"
        val isConfigured: Boolean get() = serverHost.isNotBlank()

        /** Everything that, when changed, requires tearing the WebSocket down and redialling. */
        val connection: Pair<String, Int> get() = serverHost to serverPort
    }

    fun update(transform: (Snapshot) -> Snapshot) {
        val next = transform(_state.value)
        prefs.edit().apply {
            putString(KEY_HOST, next.serverHost)
            putInt(KEY_PORT, next.serverPort)
            putStringSet(KEY_CHARACTERS, next.alertCharacters)
            putInt(KEY_RADIUS, next.alertJumpRadius)
            putBoolean(KEY_ALERT_CLEAR, next.alertOnClear)
            putBoolean(KEY_KEEP_SCREEN_ON, next.keepScreenOn)
        }.apply()
        _state.value = next
    }

    private companion object {
        const val KEY_HOST = "server.host"
        const val KEY_PORT = "server.port"
        const val KEY_CHARACTERS = "alert.characters"

        /** Superseded by [KEY_CHARACTERS]; still read once so an existing install keeps its pick. */
        const val KEY_LEGACY_CHARACTER = "character"
        const val KEY_RADIUS = "alert.radius"
        const val KEY_ALERT_CLEAR = "alert.clear"
        const val KEY_KEEP_SCREEN_ON = "keepScreenOn"

        private val lock = Any()

        @Volatile
        private var instance: MutableStateFlow<Snapshot>? = null

        fun shared(prefs: SharedPreferences): MutableStateFlow<Snapshot> =
            instance ?: synchronized(lock) {
                instance ?: MutableStateFlow(read(prefs)).also { instance = it }
            }

        fun read(prefs: SharedPreferences) = Snapshot(
            serverHost = prefs.getString(KEY_HOST, "") ?: "",
            serverPort = prefs.getInt(KEY_PORT, 31337),
            alertCharacters = readCharacters(prefs),
            alertJumpRadius = prefs.getInt(KEY_RADIUS, 5),
            alertOnClear = prefs.getBoolean(KEY_ALERT_CLEAR, false),
            keepScreenOn = prefs.getBoolean(KEY_KEEP_SCREEN_ON, true),
        )

        /**
         * The set as stored, falling back to the single character the old build tracked.
         *
         * `getStringSet` hands back the live instance it caches, so it is copied before use —
         * mutating it would corrupt the preference store's own state.
         */
        fun readCharacters(prefs: SharedPreferences): Set<String> {
            prefs.getStringSet(KEY_CHARACTERS, null)?.let { return it.toSet() }
            return prefs.getString(KEY_LEGACY_CHARACTER, null)?.let { setOf(it) } ?: emptySet()
        }
    }
}
