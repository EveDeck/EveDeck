package dev.eveintel.android

import dev.eveintel.wire.ClientMessage
import dev.eveintel.wire.ServerMessage
import dev.eveintel.wire.WireJson
import io.ktor.client.HttpClient
import io.ktor.client.engine.okhttp.OkHttp
import io.ktor.client.plugins.websocket.WebSockets
import io.ktor.client.plugins.websocket.webSocket
import io.ktor.websocket.Frame
import io.ktor.websocket.readText
import io.ktor.websocket.send
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlin.coroutines.coroutineContext

/**
 * WebSocket client for the PC daemon.
 *
 * Reconnects with a bounded backoff: a tablet sitting on a desk will lose the connection whenever
 * wifi sleeps or the PC restarts, and it needs to come back on its own without being touched.
 */
class IntelClient(private val deviceName: String) {

    private val http = HttpClient(OkHttp) {
        install(WebSockets)
    }

    /** Last failure reason, surfaced in the UI so a misconfiguration is visible rather than silent. */
    @Volatile
    var lastError: String? = null
        private set

    /**
     * Queued client messages, drained by whichever session is currently open.
     * Not named `outgoing`: that would be shadowed by the WebSocket session's own channel.
     */
    private val pending = MutableSharedFlow<ClientMessage>(extraBufferCapacity = 16)

    fun send(message: ClientMessage) {
        pending.tryEmit(message)
    }

    suspend fun run(url: String) {
        var backoff = MIN_BACKOFF_MS
        while (coroutineContext.isActive) {
            try {
                IntelRepository.setConnection(ConnectionState.CONNECTING)
                http.webSocket(url) {
                    IntelRepository.setConnection(ConnectionState.CONNECTED)
                    backoff = MIN_BACKOFF_MS
                    lastError = null

                    send(
                        WireJson.encodeToString(
                            ClientMessage.serializer(),
                            ClientMessage.Hello(deviceName),
                        ),
                    )
                    // No Follow is sent: jump distance is computed here from the locations the
                    // daemon broadcasts for every character, so the server never needed to know
                    // which pilot we care about. It still accepts the verb from older clients.

                    val sender = launch {
                        pending.collect { message ->
                            send(WireJson.encodeToString(ClientMessage.serializer(), message))
                        }
                    }
                    try {
                        for (frame in incoming) {
                            val text = (frame as? Frame.Text)?.readText() ?: continue
                            handle(text)
                        }
                    } finally {
                        sender.cancel()
                    }
                }
            } catch (cancellation: CancellationException) {
                throw cancellation
            } catch (error: Exception) {
                // Daemon down, wifi asleep, PC rebooting — all the same response: back off and
                // retry. Logged because a permanently failing cause (blocked cleartext, wrong
                // address) is otherwise indistinguishable from a daemon that is simply not running.
                android.util.Log.w(TAG, "connection to $url failed: ${error.javaClass.simpleName}: ${error.message}")
                lastError = "${error.javaClass.simpleName}: ${error.message}"
            }
            IntelRepository.setConnection(ConnectionState.DISCONNECTED)
            if (!coroutineContext.isActive) return
            delay(backoff)
            backoff = (backoff * 2).coerceAtMost(MAX_BACKOFF_MS)
        }
    }

    private suspend fun handle(text: String) {
        val message = runCatching {
            WireJson.decodeFromString(ServerMessage.serializer(), text)
        }.getOrNull() ?: return

        when (message) {
            is ServerMessage.Snapshot -> {
                IntelRepository.setScope(message.scopeRegionIds.toSet())
                IntelRepository.replaceAll(message.messages, message.locations)
                IntelRepository.setChannels(message.channels)
                IntelRepository.setHeartbeat(System.currentTimeMillis())
            }
            is ServerMessage.Channels -> IntelRepository.setChannels(message)
            is ServerMessage.Characters -> IntelRepository.addCharacters(message.characters)
            is ServerMessage.Sovereignty -> IntelRepository.setSovereignty(message)
            is ServerMessage.Stats -> IntelRepository.setStats(message.systems)
            is ServerMessage.Intel -> IntelRepository.add(message.message)
            is ServerMessage.Location -> IntelRepository.updateLocation(message.location)
            is ServerMessage.Heartbeat -> IntelRepository.setHeartbeat(System.currentTimeMillis())
        }
    }

    fun close() = http.close()

    private companion object {
        const val TAG = "IntelClient"
        const val MIN_BACKOFF_MS = 1_000L
        const val MAX_BACKOFF_MS = 15_000L
    }
}
