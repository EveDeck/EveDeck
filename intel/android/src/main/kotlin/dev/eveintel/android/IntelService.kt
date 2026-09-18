package dev.eveintel.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.LifecycleService
import dev.eveintel.model.IntelMessage
import dev.eveintel.model.Keyword
import dev.eveintel.parse.isHostile
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch

/**
 * Holds the WebSocket open and raises alerts.
 *
 * A foreground service because the whole point is a tablet propped beside the monitor showing live
 * intel; Android would otherwise suspend the socket within minutes of the screen locking.
 */
class IntelService : LifecycleService() {

    private lateinit var settings: Settings
    private var client: IntelClient? = null
    private var connectionJob: Job? = null

    override fun onCreate() {
        super.onCreate()
        settings = Settings(this)
        createChannels()
        startForeground(SERVICE_NOTIFICATION_ID, serviceNotification("Starting…"))

        lifecycleScope.launch {
            IntelRepository.attachUniverse(UniverseLoader.load(this@IntelService))
        }

        lifecycleScope.launch {
            // Only the server address may tear the socket down. The alert settings are read fresh
            // per message, so this must not react to them: collecting the whole snapshot would
            // redial the WebSocket on every tick of the radius slider.
            settings.state
                .map { it.connection }
                .distinctUntilChanged()
                .collectLatest { (host, port) ->
                    connectionJob?.cancel()
                    client?.close()
                    if (host.isBlank()) {
                        updateServiceNotification("No server configured")
                        return@collectLatest
                    }
                    val newClient = IntelClient(Build.MODEL ?: "tablet")
                    client = newClient
                    IntelRepository.requestChannels = { channels ->
                        newClient.send(dev.eveintel.wire.ClientMessage.SetChannels(channels))
                    }
                    IntelRepository.requestDisplaySettings = { settings ->
                        newClient.send(dev.eveintel.wire.ClientMessage.SetDisplay(settings))
                    }
                    connectionJob = lifecycleScope.launch {
                        newClient.run("ws://$host:$port/intel")
                    }
                }
        }

        lifecycleScope.launch {
            IntelRepository.connection.collectLatest { state ->
                updateServiceNotification(
                    when (state) {
                        ConnectionState.CONNECTED -> "Connected to ${settings.state.value.serverHost}"
                        ConnectionState.CONNECTING -> "Connecting…"
                        ConnectionState.DISCONNECTED -> "Disconnected — retrying"
                    },
                )
            }
        }

        lifecycleScope.launch {
            IntelRepository.arrivals.collect { maybeAlert(it) }
        }
    }

    /**
     * Alerts on hostile intel inside the configured jump radius of the chosen characters.
     *
     * Distance is the nearest of those characters, so a multiboxer gets one alert for the fleet.
     * When it cannot be worked out at all — nobody chosen, nobody seen in Local yet, the universe
     * still loading — the alert is raised anyway and says so, because going quiet about a hostile
     * is the worse failure. It is labelled rather than silently passed off as in-range.
     */
    private fun maybeAlert(message: IntelMessage) {
        val snapshot = settings.state.value
        if (snapshot.alertJumpRadius <= 0) return
        if (!message.isHostile()) {
            if (!(snapshot.alertOnClear && Keyword.CLEAR in message.keywords)) return
        }

        val distances = IntelRepository.distancesFrom(snapshot.alertCharacters)
        val universe = IntelRepository.universe
        val jumps = message.systemIds.mapNotNull { distances[it] }.minOrNull()

        if (jumps != null && jumps > snapshot.alertJumpRadius) return

        val systemName = message.systemIds.firstNotNullOfOrNull { universe?.system(it)?.name }
            ?: return
        val distanceText = when {
            jumps == 0 -> " — YOUR SYSTEM"
            jumps != null -> " — $jumps jump${if (jumps == 1) "" else "s"}"
            else -> " — range unknown"
        }

        val notification = NotificationCompat.Builder(this, CHANNEL_ALERTS)
            .setSmallIcon(R.drawable.ic_stat_intel)
            .setContentTitle("$systemName$distanceText")
            .setContentText(message.raw)
            .setStyle(NotificationCompat.BigTextStyle().bigText(message.raw))
            .setPriority(if ((jumps ?: 99) <= 1) NotificationCompat.PRIORITY_HIGH else NotificationCompat.PRIORITY_DEFAULT)
            .setCategory(NotificationCompat.CATEGORY_ALARM)
            .setAutoCancel(true)
            .setContentIntent(activityIntent())
            .build()

        runCatching {
            NotificationManagerCompatShim.notify(this, message.id.hashCode(), notification)
        }
    }

    /**
     * The platform asking us to stop, on a foreground-service type it time-limits.
     *
     * The service runs as `specialUse`, which is not capped, so this should never fire. It is
     * implemented anyway because the penalty for ignoring it is an ANR rather than a quiet stop:
     * the system gives a few seconds to call [stopSelf] and then kills the app if nothing happened.
     * Leaving a stale "Connected" notification behind would be worse than saying what happened.
     */
    override fun onTimeout(startId: Int, fgsType: Int) {
        stopBecauseOfTimeout()
        super.onTimeout(startId, fgsType)
    }

    @Deprecated("Superseded by the two-argument overload on API 35+", ReplaceWith("onTimeout(startId, 0)"))
    override fun onTimeout(startId: Int) {
        stopBecauseOfTimeout()
        @Suppress("DEPRECATION")
        super.onTimeout(startId)
    }

    private fun stopBecauseOfTimeout() {
        updateServiceNotification("Stopped by Android — reopen EveDeck Intel to resume")
        connectionJob?.cancel()
        client?.close()
        stopSelf()
    }

    private fun createChannels() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_SERVICE, getString(R.string.channel_service), NotificationManager.IMPORTANCE_LOW),
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ALERTS, getString(R.string.channel_intel), NotificationManager.IMPORTANCE_HIGH).apply {
                enableVibration(true)
            },
        )
    }

    private fun serviceNotification(text: String): Notification =
        NotificationCompat.Builder(this, CHANNEL_SERVICE)
            .setSmallIcon(R.drawable.ic_stat_intel)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setOngoing(true)
            .setContentIntent(activityIntent())
            .build()

    private fun updateServiceNotification(text: String) {
        runCatching {
            NotificationManagerCompatShim.notify(this, SERVICE_NOTIFICATION_ID, serviceNotification(text))
        }
    }

    private fun activityIntent(): PendingIntent = PendingIntent.getActivity(
        this,
        0,
        Intent(this, MainActivity::class.java),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    companion object {
        private const val CHANNEL_SERVICE = "service"
        private const val CHANNEL_ALERTS = "alerts"
        private const val SERVICE_NOTIFICATION_ID = 1

        fun start(context: Context) {
            val intent = Intent(context, IntelService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }
    }
}

/** Keeps the POST_NOTIFICATIONS permission check in one place. */
private object NotificationManagerCompatShim {
    fun notify(context: Context, id: Int, notification: Notification) {
        val manager = androidx.core.app.NotificationManagerCompat.from(context)
        if (manager.areNotificationsEnabled()) {
            @Suppress("MissingPermission")
            manager.notify(id, notification)
        }
    }
}
