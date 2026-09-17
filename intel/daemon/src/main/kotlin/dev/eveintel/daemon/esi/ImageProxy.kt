package dev.eveintel.daemon.esi

import java.nio.file.Files
import java.nio.file.Path

/**
 * Fetches EVE images once and serves them from disk.
 *
 * The tablet never talks to the internet: it asks the daemon for
 * `/img/<category>/<id>/<variant>?size=<n>` and the daemon fetches from `images.evetech.net` the
 * first time, then answers from the cache. That keeps the app LAN-only and avoids every device
 * re-fetching the same portraits.
 */
class ImageProxy(
    private val esi: EsiClient,
    private val cacheDirectory: Path,
) {
    /** Categories and variants the CDN actually serves; anything else is rejected. */
    private val allowed = mapOf(
        "characters" to setOf("portrait"),
        "corporations" to setOf("logo"),
        "alliances" to setOf("logo"),
        "types" to setOf("icon", "render", "bp", "bpc"),
        "factions" to setOf("logo"),
    )

    private val allowedSizes = setOf(32, 64, 128, 256, 512)

    data class Image(val bytes: ByteArray, val contentType: String)

    suspend fun get(category: String, id: Long, variant: String, size: Int): Image? {
        if (allowed[category]?.contains(variant) != true) return null
        if (size !in allowedSizes) return null
        if (id <= 0) return null

        val file = cacheDirectory.resolve(category).resolve("$id-$variant-$size.png")
        if (Files.isRegularFile(file)) {
            val cached = runCatching { Files.readAllBytes(file) }.getOrNull()
            if (cached != null && cached.isNotEmpty()) return Image(cached, CONTENT_TYPE)
        }

        val url = "${EsiClient.IMAGES}/$category/$id/$variant?size=$size"
        val bytes = esi.bytes(url) ?: return null
        if (bytes.isEmpty()) return null

        runCatching {
            Files.createDirectories(file.parent)
            Files.write(file, bytes)
        }.onFailure { System.err.println("could not cache image: ${it.message}") }

        return Image(bytes, CONTENT_TYPE)
    }

    fun cachedCount(): Int = runCatching {
        Files.walk(cacheDirectory).use { stream ->
            stream.filter { Files.isRegularFile(it) }.count().toInt()
        }
    }.getOrDefault(0)

    private companion object {
        const val CONTENT_TYPE = "image/png"
    }
}
