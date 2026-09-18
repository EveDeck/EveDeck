package dev.eveintel.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil3.compose.AsyncImage
import dev.eveintel.android.ConnectionState
import dev.eveintel.android.IntelUiState
import dev.eveintel.model.DisplaySettings
import dev.eveintel.model.IntelMessage
import dev.eveintel.model.Keyword
import dev.eveintel.model.Token
import kotlin.math.max

private fun DisplaySettings.parsedTextColor(): Color =
    runCatching { Color(android.graphics.Color.parseColor(textColor)) }.getOrDefault(IntelColors.OnSurface)

/**
 * Renders [text] once, or — when glow and/or drop shadow are enabled — layers extra copies
 * behind it, since Compose's [TextStyle] only supports a single [Shadow] per [Text].
 */
@Composable
private fun ShadowedText(
    text: String,
    color: Color,
    display: DisplaySettings,
    modifier: Modifier = Modifier,
    fontWeight: FontWeight? = null,
    fontFamily: FontFamily? = null,
    fontStyle: FontStyle? = null,
    fontSize: TextUnit = TextUnit.Unspecified,
    maxLines: Int = Int.MAX_VALUE,
) {
    if (!display.glowEnabled && !display.dropShadowEnabled) {
        Text(
            text,
            color = color,
            modifier = modifier,
            fontWeight = fontWeight,
            fontFamily = fontFamily,
            fontStyle = fontStyle,
            fontSize = fontSize,
            maxLines = maxLines,
        )
        return
    }
    Box(modifier) {
        if (display.glowEnabled) {
            Text(
                text,
                color = color.copy(alpha = 0.85f),
                fontWeight = fontWeight,
                fontFamily = fontFamily,
                fontStyle = fontStyle,
                fontSize = fontSize,
                maxLines = maxLines,
                style = TextStyle(shadow = Shadow(color.copy(alpha = 0.85f), Offset.Zero, blurRadius = 18f)),
            )
        }
        if (display.dropShadowEnabled) {
            Text(
                text,
                color = color,
                fontWeight = fontWeight,
                fontFamily = fontFamily,
                fontStyle = fontStyle,
                fontSize = fontSize,
                maxLines = maxLines,
                style = TextStyle(shadow = Shadow(Color.Black.copy(alpha = 0.75f), Offset(2f, 2f), blurRadius = 4f)),
            )
        }
        Text(
            text,
            color = color,
            fontWeight = fontWeight,
            fontFamily = fontFamily,
            fontStyle = fontStyle,
            fontSize = fontSize,
            maxLines = maxLines,
        )
    }
}

@Composable
fun FeedScreen(state: IntelUiState, now: Long, modifier: Modifier = Modifier) {
    val listState = rememberLazyListState()

    LaunchedEffect(state.messages.firstOrNull()?.id) {
        if (listState.firstVisibleItemIndex <= 2) listState.animateScrollToItem(0)
    }

    if (state.messages.isEmpty()) {
        EmptyFeed(state, modifier)
        return
    }

    LazyColumn(
        state = listState,
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(vertical = 4.dp),
        verticalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        items(state.messages, key = { it.id }) { message ->
            IntelRow(state = state, message = message, now = now)
        }
    }
}

@Composable
private fun EmptyFeed(state: IntelUiState, modifier: Modifier) {
    Box(modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(
            text = when (state.connection) {
                ConnectionState.CONNECTED -> "Waiting for intel…"
                ConnectionState.CONNECTING -> "Connecting to the daemon…"
                ConnectionState.DISCONNECTED ->
                    if (state.settings?.isConfigured == true) {
                        "Cannot reach the daemon"
                    } else {
                        "Set the server address in Settings"
                    }
            },
            color = IntelColors.Muted,
            fontSize = (18 * state.display.fontScale).sp,
        )
    }
}

@Composable
private fun IntelRow(state: IntelUiState, message: IntelMessage, now: Long) {
    val jumps = state.nearestJumps(message)
    val inRange = state.isInRange(jumps)
    val isClear = Keyword.CLEAR in message.keywords
    val accent = if (isClear) IntelColors.Clear else IntelColors.forJumps(jumps)
    val system = message.tokens.filterIsInstance<Token.System>().firstOrNull()

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 8.dp)
            .clip(RoundedCornerShape(5.dp))
            // Anything inside the configured jump range gets a visible edge and a lifted
            // background, so "near me" is readable from across the room without reading the number.
            .background(if (inRange) IntelColors.SurfaceRaised else IntelColors.Surface)
            .then(
                if (inRange) {
                    Modifier.border(1.dp, accent.copy(alpha = 0.55f), RoundedCornerShape(5.dp))
                } else {
                    Modifier
                },
            )
            .padding(horizontal = 10.dp, vertical = 8.dp),
        verticalAlignment = Alignment.Top,
    ) {
        JumpBadge(state = state, jumps = jumps, color = accent, inRange = inRange)
        Spacer(Modifier.width(10.dp))

        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                ShadowedText(
                    // "~" flags a system this line never actually named -- IntelPipeline borrowed
                    // it from the channel's last stated report because this was a bare follow-up
                    // (`nv`, a ship name with nothing else). Never show a guess as a stated fact.
                    text = (if (system?.inferred == true) "~" else "") + (system?.name ?: "—"),
                    color = if (system?.inferred == true) accent.copy(alpha = 0.7f) else accent,
                    display = state.display,
                    fontStyle = if (system?.inferred == true) FontStyle.Italic else FontStyle.Normal,
                    fontWeight = FontWeight.Bold,
                    fontFamily = FontFamily.Monospace,
                    fontSize = (19 * state.display.fontScale).sp,
                )
                system?.let { token ->
                    state.sovHolderOf(token.systemId)?.let { holder ->
                        Spacer(Modifier.width(6.dp))
                        SovChip(state, holder.ticker ?: holder.name)
                    }
                }
                Spacer(Modifier.weight(1f))
                ChannelChip(state, message.channel)
                Spacer(Modifier.width(8.dp))
                Text(
                    text = age(now - message.timestampMillis),
                    color = IntelColors.Muted,
                    fontFamily = FontFamily.Monospace,
                    fontSize = (14 * state.display.fontScale).sp,
                )
            }

            Spacer(Modifier.height(5.dp))
            Ships(state, message)
            Hostiles(state, message)

            val extras = extraLabels(message)
            if (extras.isNotEmpty()) {
                val extrasColor = if (isClear) IntelColors.Clear else IntelColors.Warning
                ShadowedText(
                    text = extras.joinToString(" · "),
                    color = extrasColor,
                    display = state.display,
                    fontSize = (15 * state.display.fontScale).sp,
                    fontWeight = FontWeight.Bold,
                )
            }

            Text(
                text = "${message.author}: ${message.raw}",
                color = IntelColors.Muted,
                fontSize = (14 * state.display.fontScale).sp,
                maxLines = 1,
            )
        }
    }
}

/** Ship icons come from the daemon's image proxy, so the tablet never touches the internet. */
@Composable
private fun Ships(state: IntelUiState, message: IntelMessage) {
    val ships = message.ships
    if (ships.isEmpty()) return

    Row(verticalAlignment = Alignment.CenterVertically) {
        ships.take(6).forEach { ship ->
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.padding(end = 10.dp),
            ) {
                val url = state.shipIconUrl(ship.typeId)
                if (url != null) {
                    AsyncImage(
                        model = url,
                        contentDescription = ship.name,
                        modifier = Modifier.size((26 * state.display.iconScale).dp).clip(RoundedCornerShape(3.dp)),
                    )
                    Spacer(Modifier.width(5.dp))
                }
                ShadowedText(
                    text = ship.count?.let { "${it}× " }.orEmpty() + ship.name,
                    color = state.display.parsedTextColor(),
                    display = state.display,
                    fontSize = (16 * state.display.fontScale).sp,
                    fontWeight = if (ship.count != null) FontWeight.Bold else FontWeight.Normal,
                )
            }
        }
    }
}

@Composable
private fun Hostiles(state: IntelUiState, message: IntelMessage) {
    val players = message.players
    if (players.isEmpty()) return

    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 3.dp)) {
        players.take(6).forEach { name ->
            val info = state.characters[name]
            Row(
                verticalAlignment = Alignment.CenterVertically,
                modifier = Modifier.padding(end = 10.dp),
            ) {
                state.portraitUrl(name)?.let { url ->
                    AsyncImage(
                        model = url,
                        contentDescription = name,
                        modifier = Modifier.size((24 * state.display.iconScale).dp).clip(CircleShape),
                    )
                    Spacer(Modifier.width(5.dp))
                }
                ShadowedText(
                    name,
                    color = state.display.parsedTextColor(),
                    display = state.display,
                    fontSize = (15 * state.display.fontScale).sp,
                )
                info?.allianceTicker?.let { ticker ->
                    Spacer(Modifier.width(4.dp))
                    Text(
                        text = "[$ticker]",
                        color = IntelColors.Accent,
                        fontSize = (13 * state.display.fontScale).sp,
                        fontFamily = FontFamily.Monospace,
                    )
                }
            }
        }
        if (players.size > 6) {
            Text("+${players.size - 6}", color = IntelColors.Muted, fontSize = (14 * state.display.fontScale).sp)
        }
    }
}

@Composable
private fun ChannelChip(state: IntelUiState, channel: String) {
    Text(
        text = channel,
        color = IntelColors.Muted,
        fontSize = (12 * state.display.fontScale).sp,
        fontFamily = FontFamily.Monospace,
        modifier = Modifier
            .clip(RoundedCornerShape(3.dp))
            .background(IntelColors.SurfaceRaised)
            .padding(horizontal = 6.dp, vertical = 2.dp),
    )
}

@Composable
private fun SovChip(state: IntelUiState, label: String) {
    Text(
        text = label,
        color = IntelColors.You,
        fontSize = (12 * state.display.fontScale).sp,
        fontFamily = FontFamily.Monospace,
        modifier = Modifier
            .clip(RoundedCornerShape(3.dp))
            .background(IntelColors.You.copy(alpha = 0.15f))
            .padding(horizontal = 5.dp, vertical = 2.dp),
    )
}

@Composable
private fun JumpBadge(state: IntelUiState, jumps: Int?, color: Color, inRange: Boolean) {
    Box(
        modifier = Modifier
            .size(width = 48.dp, height = 40.dp)
            .clip(RoundedCornerShape(4.dp))
            .background(color.copy(alpha = if (inRange) 0.28f else 0.13f)),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text = when (jumps) {
                null -> "—"
                0 -> "HERE"
                else -> "${jumps}j"
            },
            color = color,
            fontWeight = FontWeight.Bold,
            fontFamily = FontFamily.Monospace,
            fontSize = ((if (jumps == 0) 13 else 17) * state.display.fontScale).sp,
        )
    }
}

private fun extraLabels(message: IntelMessage): List<String> {
    val parts = mutableListOf<String>()
    message.keywords.forEach { parts += it.label() }
    message.questions.forEach { parts += "${it.name.lowercase().replace('_', ' ')}?" }
    if (message.ships.isEmpty()) {
        message.tokens.filterIsInstance<Token.Count>().forEach { parts += "${it.value}" }
    }
    return parts
}

private fun Keyword.label(): String = when (this) {
    Keyword.CLEAR -> "CLEAR"
    Keyword.STATUS -> "status"
    Keyword.NO_VISUAL -> "no visual"
    Keyword.SPIKE -> "SPIKE"
    Keyword.GATE_CAMP -> "GATECAMP"
    Keyword.BUBBLES -> "bubbles"
    Keyword.WORMHOLE -> "wormhole"
    Keyword.CYNO -> "CYNO"
    Keyword.ESS -> "ess"
    Keyword.SKYHOOK -> "skyhook"
    Keyword.COMBAT_PROBES -> "probes"
    Keyword.DOCKED -> "docked"
    Keyword.D_SCAN -> "dscan"
    // Upper case marks the ones that raise an alert on their own, matching SPIKE and GATECAMP.
    Keyword.NEUTRAL -> "NEUT"
    Keyword.HOSTILE -> "HOSTILE"
}

private fun age(millis: Long): String {
    val seconds = max(0L, millis / 1000)
    return when {
        seconds < 60 -> "${seconds}s"
        seconds < 3600 -> "${seconds / 60}m"
        else -> "${seconds / 3600}h"
    }
}
