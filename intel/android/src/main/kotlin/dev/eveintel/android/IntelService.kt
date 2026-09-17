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
            // Restart the connection whenever the server address or followed character changes.
            settings.state.collectLatest { snapshot ->
                connectionJob?.cancel()
                client?.close()
                if (!snapshot.isConfigured) {
                    updateServiceNotification("No server configured")
                    return@collectLatest
                }
                val newClient = IntelClient(Build.MODEL ?: "tablet")
                client = newClient
                IntelRepository.requestChannels = { channels ->
                    newClient.send(dev.eveintel.wire.ClientMessage.SetChannels(channels))
                }
                connectionJob = lifecycleScope.launch {
                    newClient.run(snapshot.webSocketUrl, snapshot.followedCharacter)
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

    /** Alerts only on hostile intel inside the configured jump radius of the followed character. */
    private fun maybeAlert(message: IntelMessage) {
        val snapshot = settings.state.value
        if (snapshot.alertJumpRadius <= 0) return
        if (!message.isHostile()) {
            if (!(snapshot.alertOnClear && Keyword.CLEAR in message.keywords)) return
        }

        val distances = IntelRepository.distancesFrom(snapshot.followedCharacter)
        val universe = IntelRepository.universe
        val nearest = message.systemIds
            .mapNotNull { id -> distances[id]?.let { id to it } }
            .minByOrNull { it.second }

        // With no location known, alert on everything in scope rather than going silent.
        val jumps = nearest?.second
        if (jumps != null && jumps > snapshot.alertJumpRadius) return

        val systemName = message.systemIds.firstNotNullOfOrNull { universe?.system(it)?.name }
            ?: return
        val distanceText = jumps?.let { if (it == 0) " — YOUR SYSTEM" else " — $it jump${if (it == 1) "" else "s"}" } ?: ""

        val notification = NotificationCompat.Builder(this, CHANNEL_ALERTS)
            .setSmallIcon(android.R.drawable.ic_dialog_alert)
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
            .setSmallIcon(android.R.drawable.ic_menu_compass)
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
