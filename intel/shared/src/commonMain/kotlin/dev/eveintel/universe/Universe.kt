package dev.eveintel.universe

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

@Serializable
data class SystemDto(
    val i: Int,
    val n: String,
    val r: Int,
    val c: Int,
    val s: Double,
    val x: Double,
    val y: Double,
    val z: Double,
)

@Serializable
data class RegionDto(val i: Int, val n: String)

@Serializable
data class ConstellationDto(val i: Int, val n: String, val r: Int)

@Serializable
data class ShipDto(val i: Int, val n: String)

@Serializable
data class UniverseDto(
    val systems: List<SystemDto>,
    val regions: List<RegionDto>,
    val constellations: List<ConstellationDto>,
    val jumps: List<List<Int>>,
    val ships: List<ShipDto>,
)

data class SolarSystem(
    val id: Int,
    val name: String,
    val regionId: Int,
    val regionName: String,
    val constellationId: Int,
    val security: Double,
    val x: Double,
    val y: Double,
    val z: Double,
) {
    val isNullsec: Boolean get() = security < 0.05
}

/**
 * The static universe: systems, regions and the stargate graph.
 *
 * Loaded once from the bundled `universe.json`. Small enough (~8k systems, ~14k gates) to keep
 * entirely in memory on a tablet, which is why there is no database here.
 */
class Universe(dto: UniverseDto) {

    private val regionNames: Map<Int, String> = dto.regions.associate { it.i to it.n }

    val systems: List<SolarSystem> = dto.systems.map {
        SolarSystem(
            id = it.i,
            name = it.n,
            regionId = it.r,
            regionName = regionNames[it.r] ?: "Unknown",
            constellationId = it.c,
            security = it.s,
            x = it.x,
            y = it.y,
            z = it.z,
        )
    }

    val systemsById: Map<Int, SolarSystem> = systems.associateBy { it.id }
    private val systemsByLowerName: Map<String, SolarSystem> = systems.associateBy { it.name.lowercase() }

    val regions: Map<Int, String> = regionNames
    val regionIdsByLowerName: Map<String, Int> = dto.regions.associate { it.n.lowercase() to it.i }

    val ships: List<ShipDto> = dto.ships

    /** Adjacency list over stargates, keyed by system id. */
    private val adjacency: Map<Int, IntArray> = buildMap<Int, MutableList<Int>> {
        dto.jumps.forEach { pair ->
            val (a, b) = pair
            getOrPut(a) { mutableListOf() }.add(b)
            getOrPut(b) { mutableListOf() }.add(a)
        }
    }.mapValues { it.value.toIntArray() }

    fun system(id: Int): SolarSystem? = systemsById[id]

    fun systemByName(name: String): SolarSystem? = systemsByLowerName[name.lowercase()]

    fun neighbours(id: Int): IntArray = adjacency[id] ?: IntArray(0)

    fun systemsInRegions(regionIds: Set<Int>): List<SolarSystem> =
        systems.filter { it.regionId in regionIds }

    /**
     * Breadth-first jump distance from [origin] to every reachable system.
     *
     * Run once per location change rather than per message — over ~8k systems this is well under a
     * millisecond, so the tablet can recompute on every jump without noticing.
     */
    fun distancesFrom(origin: Int): Map<Int, Int> {
        if (origin !in systemsById) return emptyMap()
        val distances = HashMap<Int, Int>(systemsById.size)
        distances[origin] = 0
        var frontier = intArrayOf(origin)
        var depth = 0
        while (frontier.isNotEmpty()) {
            depth++
            val next = ArrayList<Int>()
            for (node in frontier) {
                for (neighbour in neighbours(node)) {
                    if (distances.putIfAbsentCompat(neighbour, depth)) next.add(neighbour)
                }
            }
            frontier = next.toIntArray()
        }
        return distances
    }

    /** Jump distance between two systems, or null if unreachable (e.g. across a wormhole). */
    fun jumps(from: Int, to: Int): Int? {
        if (from == to) return 0
        return distancesFrom(from)[to]
    }

    companion object {
        private val json = Json { ignoreUnknownKeys = true }

        fun parse(text: String): Universe = Universe(json.decodeFromString(UniverseDto.serializer(), text))
    }
}

/** `putIfAbsent` is JVM-only; this keeps [Universe] in common code. Returns true if inserted. */
private fun HashMap<Int, Int>.putIfAbsentCompat(key: Int, value: Int): Boolean {
    if (containsKey(key)) return false
    put(key, value)
    return true
}
