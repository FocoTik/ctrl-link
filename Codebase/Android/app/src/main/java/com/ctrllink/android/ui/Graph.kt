package com.ctrllink.android.ui

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.PathMeasure
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.em
import androidx.compose.ui.unit.sp
import com.ctrllink.android.R
import kotlin.math.abs
import kotlin.math.log10
import kotlin.math.min
import kotlin.math.pow
import kotlin.math.roundToInt

/**
 * A node in the Devices graph: this device, a paired peer, or the "+N more" overflow. [caps]: the detail is an IPv4
 * address or a platform name, set in the uppercase label style; an IPv6 address keeps its own case.
 */
data class GraphNode(
    val name: String, val detail: String, val pf: String, val online: Boolean, val current: Boolean = false, val more: Int = 0,
    val caps: Boolean = true,
)

private val sky = Color(0xFF3AA4F2)
private const val TILE = 40f
private const val DOT = 12f

/**
 * Tile-centre positions (dp from the box centre) for this device and up to four other nodes, picked so the labels to
 * the right of each tile never collide. One peer: stacked vertically; two: peers left, this device right (Figma 69:3849).
 */
private fun layout(n: Int): Pair<Offset, List<Offset>> = when (n) {
    0 -> Offset(-50f, 0f) to emptyList()
    1 -> Offset(-50f, -54f) to listOf(Offset(-50f, 54f))
    2 -> Offset(20f, 0f) to listOf(Offset(-110f, -110f), Offset(-110f, 110f))
    3 -> Offset(-100f, 0f) to listOf(Offset(-120f, -150f), Offset(20f, -75f), Offset(-120f, 150f))
    else -> Offset(-100f, 0f) to listOf(Offset(-120f, -150f), Offset(20f, -75f), Offset(20f, 75f), Offset(-120f, 150f))
}

/**
 * The side of the tile at [c] a connector to [to] leaves from, as (point, outward direction): the left middle when the
 * other node is mostly to the left, otherwise the top or bottom middle. Never the right side, where the label is.
 */
private fun exit(c: Offset, to: Offset): Pair<Offset, Offset> {
    val d = to - c
    return when {
        d.x < 0 && abs(d.y) < abs(d.x) -> Offset(c.x - TILE / 2, c.y) to Offset(-1f, 0f)
        d.y < 0 -> Offset(c.x, c.y - TILE / 2) to Offset(0f, -1f)
        else -> Offset(c.x, c.y + TILE / 2) to Offset(0f, 1f)
    }
}

/**
 * Figma Device-connection graph: this device and its paired peers joined by dashed connectors with green dots at the
 * ends. Online links carry a small sky-blue pulse travelling towards this device; offline peers are dimmed.
 */
@Composable
fun DeviceGraph(me: GraphNode, peers: List<GraphNode>, modifier: Modifier = Modifier) {
    val (mePos, slots) = layout(peers.size)
    // "Remove animations" on: the links stay, the travelling pulse doesn't run.
    val reduce = reduceMotion()
    val pulse = if (reduce) 0f else rememberInfiniteTransition(label = "pulse").animateFloat(
        0f, 1f, infiniteRepeatable(tween(1800, easing = LinearEasing), RepeatMode.Restart), label = "pulse",
    ).value
    val line = T.text.copy(alpha = 0.68f)
    val dotColor = T.positive
    val bg = T.bg
    val hair = T.hairline
    BoxWithConstraints(modifier.fillMaxSize().semantics {
        contentDescription = "This device, ${me.name}" + peers.joinToString("") { p ->
            if (p.more > 0) ", and ${p.more} more" else ", ${p.name}, ${if (p.online) "online" else "offline"}"
        }
    }) {
        val w = maxWidth.value
        val h = maxHeight.value
        // Shrink vertically on short screens; the layouts span ±170 dp including tiles.
        val sy = min(1f, (h - 24f) / 340f).coerceAtLeast(0.4f)
        val c = Offset(w / 2, h / 2)
        fun at(o: Offset) = Offset(c.x + o.x, c.y + o.y * sy)
        val mc = at(mePos)
        val pcs = slots.map(::at)

        Canvas(Modifier.fillMaxSize().clearAndSetSemantics {}) {
            val px = density
            peers.forEachIndexed { i, p ->
                val pc = pcs[i]
                // Leave each tile straight out of its side, then bend towards the other (Figma's curved connectors).
                val (a, da) = exit(mc, pc)
                val (b, db) = exit(pc, mc)
                val k = (b - a).getDistance() * 0.45f
                val c1 = a + da * k
                val c2 = b + db * k
                val path = Path().apply {
                    moveTo(a.x * px, a.y * px)
                    cubicTo(c1.x * px, c1.y * px, c2.x * px, c2.y * px, b.x * px, b.y * px)
                }
                val dim = if (p.online) 1f else 0.4f
                drawPath(path, line.copy(alpha = line.alpha * dim),
                    style = Stroke(1.dp.toPx(), pathEffect = PathEffect.dashPathEffect(floatArrayOf(5.7f * px, 3.8f * px))))
                if (p.online && p.more == 0 && !reduce) {
                    val m = PathMeasure().apply { setPath(path, false) }
                    val len = m.length
                    // Peer → this device; a short fading trail behind the head.
                    for (k in 0..5) {
                        val t = (1f - pulse) - k * 0.035f
                        if (t < 0f) continue
                        val pos = m.getPosition(len * t)
                        val r = (if (k == 0) 4f else 3f - k * 0.4f) * px
                        drawCircle(Brush.radialGradient(listOf(sky.copy(alpha = 0.9f - k * 0.15f), Color.Transparent), pos, r * 2.2f), r * 2.2f, pos)
                    }
                    val head = m.getPosition(len * (1f - pulse))
                    drawCircle(Color.White.copy(alpha = 0.9f), 1.5f * px, head)
                }
                for (e in listOf(a, b)) {
                    val o = Offset(e.x * px, e.y * px)
                    drawCircle(if (p.online) dotColor else hair, DOT / 2 * px, o)
                    drawCircle(bg, DOT / 2 * px, o, style = Stroke(1.dp.toPx()))
                }
            }
        }

        Node(me, mc, w)
        peers.forEachIndexed { i, p -> Node(p, pcs[i], w) }
    }
}

@Composable
private fun Node(n: GraphNode, c: Offset, boxW: Float) {
    val d = LocalDensity.current
    val left = c.x - TILE / 2
    val top = c.y - TILE / 2
    val labelMax = (boxW - left - TILE - 8f - 8f).coerceAtLeast(48f)
    Row(
        Modifier.offset { with(d) { IntOffset(left.dp.roundToPx(), top.dp.roundToPx()) } }.alpha(if (n.online || n.current) 1f else 0.4f),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        DeviceTile(n)
        Column(Modifier.widthIn(max = labelMax.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(n.name, color = T.text, fontSize = 15.sp, lineHeight = 20.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(if (n.caps) n.detail.uppercase() else n.detail, color = T.secondary, fontSize = 11.sp, lineHeight = 14.sp, fontWeight = FontWeight.Medium,
                letterSpacing = 0.06.em, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

private val desktopMac = Regex("mini|imac|studio", RegexOption.IGNORE_CASE)

/** Figma Device-icons: phone, Windows PC, MacBook, or the Mac-mini glyph for desktop Macs (by name). */
fun deviceIcon(pf: String, name: String) = when (pf) {
    "windows" -> R.drawable.ic_device_windows
    "mac" -> if (desktopMac.containsMatchIn(name)) R.drawable.ic_device_desktop else R.drawable.ic_device_laptop
    else -> R.drawable.ic_device_phone
}

/** 40 dp, 12 dp corners: sky fill for this device; black with a green rim for an online peer; hairline rim offline. */
@Composable
fun DeviceTile(n: GraphNode) {
    val shape = RoundedCornerShape(12.dp)
    val m = when {
        n.current -> Modifier.shadow(4.dp, shape, ambientColor = Color(0xFF9BCCF1), spotColor = Color(0xFF9BCCF1)).background(sky, shape)
        n.online -> Modifier.shadow(4.dp, shape, ambientColor = T.positive, spotColor = T.positive).background(T.bg, shape).border(1.dp, T.positive, shape)
        else -> Modifier.background(T.bg, shape).border(1.dp, T.hairline, shape)
    }
    Box(m.size(TILE.dp), contentAlignment = Alignment.Center) {
        if (n.more > 0) Text("+${n.more}", color = T.text, fontSize = 15.sp)
        else Icon(painterResource(deviceIcon(n.pf, n.name)), null, Modifier.size(24.dp), tint = Color.White)
    }
}

/** A "nice" axis step (1, 2 or 5 × 10ⁿ) so four steps cover [max]. */
private fun niceStep(max: Float): Float {
    val raw = (max / 4f).coerceAtLeast(0.25f)
    val p = 10f.pow(kotlin.math.floor(log10(raw)))
    return listOf(1f, 2f, 5f, 10f).map { it * p }.first { it >= raw }
}

private fun tick(v: Float) = if (v == v.roundToInt().toFloat()) v.roundToInt().toString() else "%.1f".format(v)

/**
 * Figma Transfer Graph: MB/s on the left, progress 0–100 % along the bottom, a filled area under a line and a white
 * dot at the current point. [samples] are (progress 0–1, MB/s).
 */
@Composable
fun TransferGraph(samples: List<Pair<Float, Float>>, modifier: Modifier = Modifier) {
    val top = niceStep(samples.maxOfOrNull { it.second } ?: 1f) * 4f
    val ticks = (4 downTo 0).map { top * it / 4f }
    val grid = Color(0xFF141414).takeIf { T.dark } ?: T.hairline
    val lineC = T.text.copy(alpha = 0.68f)
    val fillTop = if (T.dark) Color(0xFF383838) else T.hairline
    val bg = T.bg
    val dot = if (T.dark) Color(0xFFDFDFDF) else T.text
    val axis = @Composable { s: String, m: Modifier ->
        Text(s, m, color = T.secondary, fontSize = 9.sp, lineHeight = 10.sp, fontWeight = FontWeight.Medium, letterSpacing = 0.01.em, maxLines = 1)
    }
    Column(modifier.fillMaxWidth().semantics { contentDescription = "Transfer speed graph" }, verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Column(Modifier.width(24.dp).height(84.dp), verticalArrangement = Arrangement.SpaceBetween, horizontalAlignment = Alignment.End) {
                ticks.forEach { axis(tick(it), Modifier) }
            }
            Canvas(Modifier.weight(1f).height(84.dp)) {
                val sw = 1.dp.toPx()
                drawRect(grid, style = Stroke(sw))
                // Grid rows line up with the tick labels (5 px label half-height in), columns at the x ticks.
                val y0 = 6f / 84f * size.height
                val y1 = 78f / 84f * size.height
                for (i in 0..4) { val y = y0 + (y1 - y0) * i / 4; drawLine(grid, Offset(0f, y), Offset(size.width, y), sw) }
                for (i in 1..3) { val x = size.width * i / 4; drawLine(grid, Offset(x, 0f), Offset(x, size.height), sw) }
                if (samples.isEmpty()) return@Canvas
                fun pt(s: Pair<Float, Float>) = Offset(s.first.coerceIn(0f, 1f) * size.width, y1 - (y1 - y0) * (s.second / top).coerceIn(0f, 1f))
                val pts = samples.map(::pt)
                val line = Path().apply { moveTo(pts[0].x, pts[0].y); pts.drop(1).forEach { lineTo(it.x, it.y) } }
                val area = Path().apply { addPath(line); lineTo(pts.last().x, size.height); lineTo(pts[0].x, size.height); close() }
                drawPath(area, Brush.verticalGradient(listOf(fillTop, bg), startY = pts.minOf { it.y }, endY = y1))
                drawPath(line, lineC, style = Stroke(sw))
                drawCircle(dot, 4.dp.toPx(), pts.last())
            }
        }
        Row(Modifier.fillMaxWidth()) {
            Box(Modifier.width(32.dp))
            Row(Modifier.weight(1f), horizontalArrangement = Arrangement.SpaceBetween) {
                listOf("0%", "25%", "50%", "75%", "100%").forEach { axis(it, Modifier) }
            }
        }
    }
}
