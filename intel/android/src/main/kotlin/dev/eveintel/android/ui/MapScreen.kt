package dev.eveintel.android.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import dev.eveintel.android.IntelUiState
import dev.eveintel.model.Keyword
import dev.eveintel.universe.SolarSystem
import kotlin.math.max
import kotlin.math.min

private const val INTEL_FRESH_MS = 10 * 60 * 1000L

/**
 * Region map.
 *
 * EVE's 3D coordinates are projected on the x/z plane, which is the conventional top-down view of
 * New Eden and keeps constellations recognisably grouped. Systems are laid out once per region set
 * and then panned/zoomed as a unit.
 */
@Composable
fun MapScreen(state: IntelUiState, now: Long, modifier: Modifier = Modifier) {
    val universe = state.universe
    if (universe == null) {
        Centered("Loading universe…")
        return
    }

    // Prefer the region the alert characters are in; fall back to the channel's scope.
    val regionIds = remember(state.scopeRegionIds, state.primaryLocation?.systemId) {
        val here = state.primaryLocation?.let { universe.system(it.systemId)?.regionId }
        when {
            here != null -> setOf(here)
            state.scopeRegionIds.isNotEmpty() -> state.scopeRegionIds
            else -> emptySet()
        }
    }

    val systems = remember(regionIds) {
        if (regionIds.isEmpty()) emptyList() else universe.systemsInRegions(regionIds)
    }
    if (systems.isEmpty()) {
        Centered("No region selected.\nConnect and pick a character to follow.")
        return
    }

    val bounds = remember(systems) { Bounds.of(systems) }
    val edges = remember(systems) {
        val ids = systems.mapTo(HashSet()) { it.id }
        systems.flatMap { system ->
            universe.neighbours(system.id)
                .filter { it in ids && it > system.id }
                .map { system.id to it }
        }
    }
    val positions = remember(systems) { systems.associateBy { it.id } }

    // Most recent intel per system, for colouring.
    val intelBySystem = remember(state.messages, now) {
        buildMap<Int, Pair<Long, Boolean>> {
            state.messages.forEach { message ->
                val clear = Keyword.CLEAR in message.keywords
                message.systemIds.forEach { id ->
                    val existing = this[id]
                    if (existing == null || existing.first < message.timestampMillis) {
                        put(id, message.timestampMillis to clear)
                    }
                }
            }
        }
    }

    var scale by remember { mutableFloatStateOf(1f) }
    var offset by remember { mutableStateOf(Offset.Zero) }

    Box(
        modifier
            .fillMaxSize()
            .background(IntelColors.Background)
            .pointerInput(Unit) {
                detectTransformGestures { _, pan, zoom, _ ->
                    scale = (scale * zoom).coerceIn(0.5f, 8f)
                    offset += pan
                }
            },
    ) {
        Canvas(Modifier.fillMaxSize()) {
            val project: (SolarSystem) -> Offset = { system ->
                bounds.project(system, size.width, size.height) * scale + offset
            }

            edges.forEach { (fromId, toId) ->
                val from = positions[fromId] ?: return@forEach
                val to = positions[toId] ?: return@forEach
                drawLine(
                    color = IntelColors.SurfaceRaised,
                    start = project(from),
                    end = project(to),
                    strokeWidth = 1.5f * scale,
                )
            }

            systems.forEach { system ->
                val point = project(system)
                val intel = intelBySystem[system.id]
                val isYou = system.id in state.alertSystemIds
                val jumps = state.jumpsTo(system.id)
                val inRange = state.isInRange(jumps)
                val radius = (if (isYou) 7f else 4f) * max(0.7f, min(scale, 2f))

                // Sovereignty tint underneath everything, so the map reads like a sov map even
                // where nothing has been reported.
                sovColor(state, system.id)?.let { sov ->
                    drawCircle(sov.copy(alpha = 0.22f), radius = radius * 2.6f, center = point)
                }

                // Kill activity from ESI, independent of whether anyone reported it.
                val pvp = state.stats[system.id]?.pvpKills ?: 0
                if (pvp > 0) {
                    val intensity = (pvp / 20f).coerceIn(0.15f, 0.7f)
                    drawCircle(
                        IntelColors.Warning.copy(alpha = intensity),
                        radius = radius * (2.2f + intensity * 2f),
                        center = point,
                    )
                }

                val color = systemColor(intel, now)
                if (intel != null && now - intel.first < INTEL_FRESH_MS && !intel.second) {
                    drawCircle(color.copy(alpha = 0.18f), radius = radius * 3.2f, center = point)
                }
                drawCircle(color, radius = radius, center = point)

                // Systems inside the alert radius get a ring, matching the feed's banding.
                if (inRange && !isYou) {
                    drawCircle(
                        IntelColors.forJumps(jumps).copy(alpha = 0.7f),
                        radius = radius + 3f,
                        center = point,
                        style = androidx.compose.ui.graphics.drawscope.Stroke(1.5f),
                    )
                }
                if (isYou) {
                    drawCircle(
                        IntelColors.You,
                        radius = radius + 4f,
                        center = point,
                        style = androidx.compose.ui.graphics.drawscope.Stroke(2f),
                    )
                }

                if (scale > 1.4f || intel != null || isYou || inRange) {
                    drawLabel(system.name, point, color)
                }
            }
        }

        Text(
            text = regionIds.mapNotNull { universe.regions[it] }.joinToString(),
            color = IntelColors.Muted,
            fontSize = 14.sp,
            modifier = Modifier.align(Alignment.TopStart).padding(12.dp),
        )
        Text(
            text = "pinch to zoom · drag to pan",
            color = IntelColors.Muted.copy(alpha = 0.6f),
            fontSize = 12.sp,
            modifier = Modifier.align(Alignment.BottomEnd).padding(12.dp),
        )
    }
}

/**
 * A stable colour per sovereignty holder.
 *
 * Derived from the holder id rather than a lookup table, so every alliance gets a consistent colour
 * without anyone maintaining a list. Hue only — saturation and lightness are fixed so no holder can
 * come out near the reds and ambers that mean "hostile" and "kills".
 */
private fun sovColor(state: IntelUiState, systemId: Int): Color? {
    val holderId = state.sovereignty.systems[systemId] ?: return null
    val hue = ((holderId % 360L).toFloat() + 200f) % 360f
    return Color.hsl(hue, saturation = 0.55f, lightness = 0.55f)
}

private fun systemColor(intel: Pair<Long, Boolean>?, now: Long): Color {
    if (intel == null) return IntelColors.Muted.copy(alpha = 0.45f)
    val (timestamp, clear) = intel
    val age = now - timestamp
    if (age > INTEL_FRESH_MS) return IntelColors.Muted.copy(alpha = 0.45f)
    if (clear) return IntelColors.Clear
    val freshness = 1f - (age.toFloat() / INTEL_FRESH_MS)
    return IntelColors.Hostile.copy(alpha = 0.45f + 0.55f * freshness)
}

private fun DrawScope.drawLabel(text: String, at: Offset, color: Color) {
    drawContext.canvas.nativeCanvas.apply {
        val paint = android.graphics.Paint().apply {
            this.color = color.toArgb()
            textSize = 22f
            isAntiAlias = true
        }
        drawText(text, at.x + 8f, at.y + 6f, paint)
    }
}

private fun Color.toArgb(): Int = android.graphics.Color.argb(
    (alpha * 255).toInt(),
    (red * 255).toInt(),
    (green * 255).toInt(),
    (blue * 255).toInt(),
)

/** Normalises a region's coordinates into the drawing area, preserving aspect ratio. */
private class Bounds(
    private val minX: Double,
    private val maxX: Double,
    private val minZ: Double,
    private val maxZ: Double,
) {
    fun project(system: SolarSystem, width: Float, height: Float): Offset {
        val spanX = (maxX - minX).takeIf { it > 0 } ?: 1.0
        val spanZ = (maxZ - minZ).takeIf { it > 0 } ?: 1.0
        val span = max(spanX, spanZ)
        val padding = 0.08f
        val usable = min(width, height) * (1 - 2 * padding)
        val x = ((system.x - minX) / span).toFloat() * usable + width * padding
        // EVE's z grows "north"; flip so the map reads the same way as the in-game star map.
        val y = (1.0 - (system.z - minZ) / span).toFloat() * usable + height * padding
        return Offset(x, y)
    }

    companion object {
        fun of(systems: List<SolarSystem>) = Bounds(
            minX = systems.minOf { it.x },
            maxX = systems.maxOf { it.x },
            minZ = systems.minOf { it.z },
            maxZ = systems.maxOf { it.z },
        )
    }
}

@Composable
private fun Centered(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = IntelColors.Muted, fontSize = 17.sp)
    }
}
