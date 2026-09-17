package dev.eveintel.daemon.esi

import io.ktor.client.HttpClient
import io.ktor.client.call.body
import io.ktor.client.engine.cio.CIO
import io.ktor.client.plugins.contentnegotiation.ContentNegotiation
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.request.post
import io.ktor.client.request.setBody
import io.ktor.client.statement.HttpResponse
import io.ktor.client.statement.readRawBytes
import io.ktor.http.ContentType
import io.ktor.http.contentType
import io.ktor.http.isSuccess
import io.ktor.serialization.kotlinx.json.json
import kotlinx.coroutines.delay
import kotlinx.serialization.json.Json

/**
 * Minimal ESI client.
 *
 * ESI asks third-party tools to identify themselves so CCP can contact the author if a tool
 * misbehaves; the User-Agent below is that identification. Requests are read-only and unauthenticated
 * — everything this app uses is public data, so there is no SSO, no token and no scope to grant.
 */
class EsiClient(userAgentContact: String = "eveintel (LAN intel reader)") {

    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
    }

    // Internal rather than private: the reified helpers below are inline, and an inline function
    // cannot touch private members.
    @PublishedApi
    internal val http = HttpClient(CIO) {
        install(ContentNegotiation) { json(json) }
        expectSuccess = false
    }

    @PublishedApi
    internal val userAgent = userAgentContact

    suspend inline fun <reified T> get(path: String): T? = request(path) { url ->
        http.get(url) { header("User-Agent", userAgent) }
    }

    suspend inline fun <reified T> post(path: String, body: Any): T? = request(path) { url ->
        http.post(url) {
            header("User-Agent", userAgent)
            contentType(ContentType.Application.Json)
            setBody(body)
        }
    }

    /** Retries on 5xx and 420 (ESI's error-limited response) with a short backoff. */
    suspend inline fun <reified T> request(path: String, call: (String) -> HttpResponse): T? {
        val url = if (path.startsWith("http")) path else "$BASE$path"
        var attempt = 0
        while (attempt < MAX_ATTEMPTS) {
            val response = runCatching { call(url) }.getOrNull()
            if (response != null && response.status.isSuccess()) {
                return runCatching { response.body<T>() }.getOrNull()
            }
            val status = response?.status?.value
            if (status != null && status < 500 && status != 420) return null
            attempt++
            delay(RETRY_DELAY_MS * attempt)
        }
        return null
    }

    /** Raw bytes, used by the image proxy. Returns null on any failure. */
    suspend fun bytes(url: String): ByteArray? {
        val response = runCatching {
            http.get(url) { header("User-Agent", userAgent) }
        }.getOrNull() ?: return null
        if (!response.status.isSuccess()) return null
        return runCatching { response.readRawBytes() }.getOrNull()
    }

    fun close() = http.close()

    companion object {
        const val BASE = "https://esi.evetech.net/latest"
        const val IMAGES = "https://images.evetech.net"
        const val MAX_ATTEMPTS = 3
        const val RETRY_DELAY_MS = 800L
    }
}
