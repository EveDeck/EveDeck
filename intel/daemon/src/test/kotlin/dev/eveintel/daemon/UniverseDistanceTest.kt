package dev.eveintel.daemon

import dev.eveintel.universe.Universe
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Multi-origin jump distance.
 *
 * This is what the tablet's alert radius is measured with: a pilot running several clients wants
 * "how close is this to *any* of mine", not a distance from one nominated character. Getting it
 * wrong is not a visible crash — it silently alerts on the wrong things — so it is pinned here.
 *
 * Systems are Providence and Catch, deliberately nowhere near anybody's real home.
 */
class UniverseDistanceTest {

    private val universe: Universe by lazy { loadUniverse() }

    private fun id(name: String) = assertNotNull(universe.systemByName(name), "unknown system $name").id

    @Test
    fun `a single origin matches the single-argument overload`() {
        val origin = id("9UY4-H")
        assertEquals(universe.distancesFrom(origin), universe.distancesFrom(listOf(origin)))
    }

    @Test
    fun `distance is taken from the nearest origin`() {
        val near = id("9UY4-H")
        val far = id("KBP7-G")
        val target = id("KBP7-G")

        // Four jumps from one, zero from the other; the pair must report the closer.
        assertEquals(4, universe.distancesFrom(near)[target])
        assertEquals(0, universe.distancesFrom(listOf(near, far))[target])
    }

    @Test
    fun `every origin is its own zero`() {
        val origins = listOf(id("9UY4-H"), id("KBP7-G"))
        val distances = universe.distancesFrom(origins)
        origins.forEach { assertEquals(0, distances[it], "origin should be zero jumps from itself") }
    }

    @Test
    fun `adding an origin never increases a distance`() {
        val first = id("9UY4-H")
        val second = id("F4R2-Q")
        val one = universe.distancesFrom(listOf(first))
        val both = universe.distancesFrom(listOf(first, second))
        one.forEach { (system, jumps) ->
            val merged = assertNotNull(both[system], "reachable system disappeared when adding an origin")
            assertTrue(merged <= jumps, "distance to $system grew from $jumps to $merged")
        }
    }

    @Test
    fun `no origins means no distances rather than everything at zero`() {
        // The caller reads an empty map as "range unknown". Returning distances-from-nowhere, or
        // treating it as reachable, would turn an unknown range into a false in-range alert.
        assertTrue(universe.distancesFrom(emptyList()).isEmpty())
    }

    @Test
    fun `unknown origins are ignored rather than poisoning the result`() {
        val real = id("9UY4-H")
        assertEquals(
            universe.distancesFrom(listOf(real)),
            universe.distancesFrom(listOf(real, -1)),
        )
        assertTrue(universe.distancesFrom(listOf(-1)).isEmpty())
    }
}
