package dev.eveintel.daemon

import dev.eveintel.wire.ClientMessage
import dev.eveintel.wire.ServerMessage
import dev.eveintel.wire.WireJson
import io.ktor.http.ContentType
import io.ktor.http.HttpStatusCode
import io.ktor.server.application.call
import io.ktor.server.application.install
import io.ktor.server.cio.CIO
import io.ktor.server.engine.embeddedServer
import io.ktor.server.response.respond
import io.ktor.server.response.respondBytes
import io.ktor.server.routing.get
import io.ktor.server.routing.routing
import io.ktor.server.websocket.WebSockets
import io.ktor.server.websocket.webSocket
import io.ktor.websocket.Frame
import io.ktor.websocket.readText
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.channels.ClosedReceiveChannelException
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.NetworkInterface
import kotlin.time.Duration.Companion.seconds

/**
 * LAN WebSocket server. No authentication and no TLS by design: it binds to the local network and
 * serves read-only intel that is already public to everyone in the channel. Do not port-forward it.
 */
class IntelServer(
    private val config: Config,
    private val configPath: java.nio.file.Path,
    private val pipeline: IntelPipeline,
    private val channels: ChannelRegistry,
    private val scopeRegionIds: Set<Int>,
    private val characters: dev.eveintel.daemon.esi.CharacterResolver,
    private val universeStatus: dev.eveintel.daemon.esi.UniverseStatusService,
    private val images: dev.eveintel.daemon.esi.ImageProxy,
) {
    private val broadcast = MutableSharedFlow<ServerMessage>(extraBufferCapacity = 256)

    private fun channelState() = ServerMessage.Channels(
        available = channels.available.value,
        selected = channels.selected.value.sorted(),
    )

    fun start(scope: CoroutineScope) {
        scope.launch {
            pipeline.intel.collect { broadcast.emit(ServerMessage.Intel(it)) }
        }
        scope.launch {
            pipeline.locations.collect { locations ->
                locations.values.forEach { broadcast.emit(ServerMessage.Location(it)) }
            }
        }
        scope.launch {
            channels.available.collect { broadcast.emit(channelState()) }
        }
        scope.launch {
            channels.selected.collect { broadcast.emit(channelState()) }
        }
        scope.launch {
            characters.updates.collect { broadcast.emit(ServerMessage.Characters(it)) }
        }
        scope.launch {
            universeStatus.sovereignty.collect { if (it.systems.isNotEmpty()) broadcast.emit(it) }
        }
        scope.launch {
            universeStatus.stats.collect { if (it.systems.isNotEmpty()) broadcast.emit(it) }
        }

        embeddedServer(CIO, port = config.port, host = config.bindAddress) {
            install(WebSockets) {
                pingPeriodMillis = 15.seconds.inWholeMilliseconds
                timeoutMillis = 45.seconds.inWholeMilliseconds
            }
            routing {
                // Image proxy: the tablet asks the daemon, the daemon asks the CDN once.
                get("/img/{category}/{id}/{variant}") {
                    val category = call.parameters["category"].orEmpty()
                    val id = call.parameters["id"]?.toLongOrNull()
                    val variant = call.parameters["variant"].orEmpty()
                    val size = call.request.queryParameters["size"]?.toIntOrNull() ?: 64
                    val image = if (id == null) null else images.get(category, id, variant, size)
                    if (image == null) {
                        call.respond(HttpStatusCode.NotFound)
                    } else {
                        call.respondBytes(
                            bytes = image.bytes,
                            contentType = ContentType.parse(image.contentType),
                        )
                    }
                }

                webSocket("/intel") {
                    val snapshot = ServerMessage.Snapshot(
                        messages = pipeline.snapshot(),
                        locations = pipeline.locations.value.values.toList(),
                        scopeRegionIds = scopeRegionIds.toList(),
                        serverTimeMillis = System.currentTimeMillis(),
                        channels = channelState(),
                    )
                    send(Frame.Text(WireJson.encodeToString(ServerMessage.serializer(), snapshot)))

                    val sender = launch {
                        broadcast.collect { message ->
                            send(Frame.Text(WireJson.encodeToString(ServerMessage.serializer(), message)))
                        }
                    }
                    try {
                        while (isActive) {
                            val frame = incoming.receive() as? Frame.Text ?: continue
                            runCatching {
                                WireJson.decodeFromString(ClientMessage.serializer(), frame.readText())
                            }.onSuccess { client ->
                                when (client) {
                                    is ClientMessage.Hello ->
                                        println("tablet connected: ${client.deviceName}")
                                    is ClientMessage.SetChannels -> {
                                        channels.select(client.channels)
                                        Config.saveChannels(configPath, channels.selected.value)
                                        println("intel channels: ${channels.selected.value.sorted().joinToString()}")
                                    }
                                    is ClientMessage.Follow -> Unit
                                }
                            }
                        }
                    } catch (_: ClosedReceiveChannelException) {
                        // Tablet went away; normal.
                    } finally {
                        sender.cancel()
                    }
                }
            }
        }.start(wait = false)

        println("Listening on :${config.port}")
        localAddresses().forEach { println("  tablet URL:  ws://$it:${config.port}/intel") }
    }

    private fun localAddresses(): List<String> = runCatching {
        NetworkInterface.getNetworkInterfaces().toList()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.toList() }
            .filter { it.hostAddress?.contains('.') == true && !it.isLoopbackAddress }
            .mapNotNull { it.hostAddress }
    }.getOrDefault(emptyList())
}
