package dev.eveintel.android

import android.content.Context
import dev.eveintel.universe.Universe
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Loads the bundled universe once per process.
 *
 * ~1 MB of JSON parsed on a background thread at startup. The tablet holds the same graph the
 * daemon does, which is why intel messages on the wire carry only system ids.
 */
object UniverseLoader {

    @Volatile
    private var cached: Universe? = null

    suspend fun load(context: Context): Universe {
        cached?.let { return it }
        return withContext(Dispatchers.IO) {
            cached ?: synchronized(this) {
                cached ?: run {
                    val json = context.assets.open("universe.json")
                        .bufferedReader()
                        .use { it.readText() }
                    Universe.parse(json).also { cached = it }
                }
            }
        }
    }

    fun peek(): Universe? = cached
}
