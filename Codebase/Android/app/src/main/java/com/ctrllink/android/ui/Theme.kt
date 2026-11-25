package com.ctrllink.android.ui

import androidx.compose.animation.core.animateIntAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontVariation
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.em
import androidx.compose.ui.unit.sp
import com.ctrllink.android.R
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.drawWithCache
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Paint
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.graphics.drawscope.drawIntoCanvas
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.unit.Dp

/** design/DESIGN.md tokens. */
class Tokens(
    val bg: Color, val surface: Color, val hairline: Color, val text: Color, val secondary: Color,
    val positive: Color, val danger: Color, val dark: Boolean,
)

private val DarkT = Tokens(Color(0xFF000000), Color(0xFF0F0F10), Color(0xFF2A2A2C), Color.White, Color(0xFF8E8E93),
    Color(0xFF3DD68C), Color(0xFFF0472E), dark = true)
private val LightT = Tokens(Color.White, Color(0xFFF4F4F5), Color(0xFFDEDEE0), Color.Black, Color(0xFF6B6B70),
    Color(0xFF1E9E5E), Color(0xFFD63A22), dark = false)

/** Clip-kind data colors, the only hues in the app. */
val kindColors = linkedMapOf(
    "text" to Color(0xFFF5C26B), "url" to Color(0xFF3AA4F2), "image" to Color(0xFFE9B6E0), "files" to Color(0xFF2549B8),
)

val LocalTokens = staticCompositionLocalOf { DarkT }
val T: Tokens @Composable get() = LocalTokens.current

@OptIn(androidx.compose.ui.text.ExperimentalTextApi::class)
private fun newsreader(weight: Int) = FontFamily(Font(R.font.newsreader, FontWeight(weight),
    variationSettings = FontVariation.Settings(FontVariation.weight(weight), FontVariation.Setting("opsz", 72f))))

val SerifLight = newsreader(300)
val Serif = newsreader(400)

@Composable
fun ClipTheme(content: @Composable () -> Unit) {
    val t = if (isSystemInDarkTheme()) DarkT else LightT
    // Material components (switches, dialogs, text fields) take the same monochrome tokens; no dynamic color.
    val scheme = (if (t.dark) darkColorScheme() else lightColorScheme()).copy(
        primary = t.text, onPrimary = t.bg, secondary = t.text, onSecondary = t.bg,
        background = t.bg, onBackground = t.text, surface = t.bg, onSurface = t.text, onSurfaceVariant = t.secondary,
        surfaceVariant = t.surface, surfaceContainer = t.surface, surfaceContainerHigh = t.surface, surfaceContainerHighest = t.surface,
        outline = t.hairline, outlineVariant = t.hairline, error = t.danger,
    )
    CompositionLocalProvider(LocalTokens provides t) { MaterialTheme(colorScheme = scheme, content = content) }
}

// ---- kit ----

@Composable
fun Label(text: String, modifier: Modifier = Modifier, color: Color = T.secondary, align: TextAlign? = null) = Text(
    text.uppercase(), modifier, color = color, fontSize = 11.sp, lineHeight = 14.sp, fontWeight = FontWeight.Medium,
    letterSpacing = 0.06.em, textAlign = align,
)

/** Serif/Headline L (34/40), centered by default. */
@Composable
fun Headline(text: String, modifier: Modifier = Modifier, align: TextAlign = TextAlign.Center, size: TextUnit = 34.sp) = Text(
    text, modifier.fillMaxWidth(), color = T.text, fontFamily = Serif, fontSize = size, lineHeight = size * 1.18f,
    textAlign = align, maxLines = 2, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
)

fun heroStyle(size: TextUnit) = TextStyle(fontFamily = SerifLight, fontSize = size, lineHeight = size, letterSpacing = (-0.02).em)

/** The one huge serif element of a screen; counts up over 400 ms when [value] changes. */
@Composable
fun Hero(value: Int, unit: String? = null, size: TextUnit = 120.sp, delta: String? = null) {
    val shown by animateIntAsState(value, tween(400), label = "hero")
    HeroText(shown.toString(), unit, size, delta)
}

@Composable
fun HeroText(text: String, unit: String? = null, size: TextUnit = 120.sp, delta: String? = null) {
    Row(verticalAlignment = Alignment.Bottom) {
        Text(text, style = heroStyle(size), color = T.text, maxLines = 1)
        if (unit != null || delta != null) Column(Modifier.padding(start = 6.dp, bottom = (size.value * 0.18f).dp)) {
            delta?.let { Text(it, color = T.positive, fontSize = 15.sp) }
            unit?.let { Text(it, color = T.secondary, fontSize = 15.sp) }
        }
    }
}

/**
 * Figma "Horizon": a 1.8×-width sphere whose top rim glows #1C1C1E and fades to nothing, so a clipped parent never
 * shows an edge. [top] is the rim's distance from the top of [modifier]'s box. Dark mode only.
 */
@Composable
fun Horizon(modifier: Modifier, top: Dp) {
    if (!T.dark) return
    Canvas(modifier.clipToBounds()) {
        val r = size.width * 1.815f / 2
        val y = top.toPx()
        val rim = Offset(size.width / 2, y)
        drawCircle(
            Brush.radialGradient(0f to Color(0xFF1C1C1E), 0.4f to Color(0x8C121214), 0.7f to Color.Transparent, center = rim, radius = r),
            r, Offset(size.width / 2, y + r),
        )
    }
}

@Composable
fun Hairline(modifier: Modifier = Modifier) = HorizontalDivider(modifier, thickness = 1.dp, color = T.hairline)

/**
 * Figma Segmented Pill: a hairline capsule with a 2 dp inset and equal-width segments; the active one is a translucent
 * white capsule filling its share. Used for the tabs and the file-size limit.
 */
@Composable
fun Pills(options: List<String>, selected: Int, onSelect: (Int) -> Unit, modifier: Modifier = Modifier) {
    Row(modifier.fillMaxWidth().border(1.dp, T.hairline, CircleShape).padding(2.dp), verticalAlignment = Alignment.CenterVertically) {
        options.forEachIndexed { i, o ->
            val on = i == selected
            // Every segment fills an equal share (Figma: Segmented Pill Item set to fill), so the active capsule spans its third.
            val m = if (on) Modifier.weight(1f).background(T.text.copy(alpha = 0.32f), CircleShape).border(1.dp, T.text.copy(alpha = 0.68f), CircleShape)
            else Modifier.weight(1f)
            Box(
                m.height(26.dp).clip(CircleShape).clickable(role = Role.Tab) { onSelect(i) }.padding(horizontal = 12.dp)
                    .semantics { this.selected = on },
                contentAlignment = Alignment.Center,
            ) { Text(o, color = T.text, fontSize = 13.sp, lineHeight = 18.sp, maxLines = 1) }
        }
    }
}

private val blobOrange = Color(0xFFF5C26B)
private val blobBlue = Color(0xFF3AA4F2)

/**
 * Figma Button, 40 dp capsule. Filled: text-colour fill with two soft clip-colour glows (amber left of centre, sky
 * right). Outlined: a strong hairline that picks up green at the bottom left and red at the bottom right.
 */
@Composable
fun PillButton(text: String, filled: Boolean, onClick: () -> Unit, modifier: Modifier = Modifier, enabled: Boolean = true) {
    val c = T.text
    val dark = T.dark
    Box(
        modifier.alpha(if (enabled) 1f else 0.35f).height(40.dp).clip(CircleShape)
            .then(if (filled) Modifier.drawWithCache {
                val r = 30.dp.toPx()
                val left = Offset(size.width / 2 - 32.dp.toPx(), size.height / 2)
                val right = Offset(size.width / 2 + 44.dp.toPx(), size.height / 2)
                onDrawBehind {
                    drawRect(c)
                    if (dark) {
                        drawCircle(Brush.radialGradient(listOf(blobOrange.copy(alpha = 0.75f), Color.Transparent), left, r), r, left)
                        drawCircle(Brush.radialGradient(listOf(blobBlue.copy(alpha = 0.75f), Color.Transparent), right, r), r, right)
                    }
                }
            } else Modifier.gradientOutline(c))
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick).padding(horizontal = 20.dp),
        contentAlignment = Alignment.Center,
    ) { Text(text, color = if (filled) T.bg else c, fontSize = 14.sp, lineHeight = 20.sp, fontWeight = FontWeight.Medium, maxLines = 1) }
}

private val edgeGreen = Color(0xFF3DD68C)
private val edgeRed = Color(0xFFF0472E)

/** 1 dp capsule outline in [c], tinted green → red along the bottom edge (dark mode). */
fun Modifier.gradientOutline(c: Color): Modifier = drawWithCache {
    val w = 1.dp.toPx()
    val tl = Offset(w / 2, w / 2)
    val sz = Size(size.width - w, size.height - w)
    val cr = CornerRadius(sz.height / 2)
    val tint = Brush.horizontalGradient(0f to edgeGreen, 0.4f to edgeGreen.copy(alpha = 0f), 0.6f to edgeRed.copy(alpha = 0f), 1f to edgeRed)
    val mask = Brush.verticalGradient(0f to Color.Black.copy(alpha = 0.2f), 1f to Color.Black)
    onDrawBehind {
        drawRoundRect(c, tl, sz, cr, style = Stroke(w))
        if (c.luminance() < 0.5f) return@onDrawBehind // light mode: plain outline
        drawIntoCanvas { cv ->
            cv.saveLayer(androidx.compose.ui.geometry.Rect(Offset.Zero, size), Paint())
            drawRoundRect(tint, tl, sz, cr, style = Stroke(w))
            drawRect(mask, blendMode = BlendMode.DstIn)
            cv.restore()
        }
    }
}

private fun Color.luminance() = 0.2126f * red + 0.7152f * green + 0.0722f * blue

/** 40 dp outlined circle with an icon; the Send clipboard button in the Devices footer. */
@Composable
fun RoundIconButton(icon: Int, description: String, onClick: () -> Unit, modifier: Modifier = Modifier) {
    Box(
        modifier.size(40.dp).clip(CircleShape).gradientOutline(T.text).clickable(role = Role.Button, onClick = onClick)
            .semantics { contentDescription = description },
        contentAlignment = Alignment.Center,
    ) { androidx.compose.material3.Icon(androidx.compose.ui.res.painterResource(icon), null, Modifier.size(20.dp), tint = T.text) }
}

/** True when the system "Remove animations" setting is on; motion then snaps instead of moving. */
@Composable
fun reduceMotion(): Boolean {
    val ctx = androidx.compose.ui.platform.LocalContext.current
    return androidx.compose.runtime.remember {
        android.provider.Settings.Global.getFloat(ctx.contentResolver, android.provider.Settings.Global.ANIMATOR_DURATION_SCALE, 1f) == 0f
    }
}

/**
 * Figma Toggle, Platform=Android (19:617 / 19:619), 52 x 32. On: a text-colour → amber gradient track with a 24 dp
 * background-colour knob at the right. Off: a 2 dp text-colour outline with a 16 dp secondary knob at the left.
 * Visual only: the whole settings row is the touch target and carries the switch semantics.
 */
@Composable
fun Toggle(on: Boolean, modifier: Modifier = Modifier) {
    val t by androidx.compose.animation.core.animateFloatAsState(
        if (on) 1f else 0f,
        if (reduceMotion()) androidx.compose.animation.core.snap() else tween(180, easing = androidx.compose.animation.core.CubicBezierEasing(0.23f, 1f, 0.32f, 1f)),
        label = "toggle",
    )
    val text = T.text
    val bg = T.bg
    val knobOff = T.secondary
    val amber = kindColors.getValue("text")
    Canvas(modifier.size(52.dp, 32.dp)) {
        val r = size.height / 2
        val sw = 2.dp.toPx()
        if (t > 0f) drawRoundRect(Brush.horizontalGradient(listOf(text, amber)), cornerRadius = CornerRadius(r), alpha = t)
        if (t < 1f) drawRoundRect(text, Offset(sw / 2, sw / 2), Size(size.width - sw, size.height - sw), CornerRadius(r - sw / 2),
            style = Stroke(sw), alpha = 1f - t)
        val cx = (18f + 18f * t).dp.toPx()
        val kr = (8f + 4f * t).dp.toPx()
        drawCircle(androidx.compose.ui.graphics.lerp(knobOff, bg, t), kr, Offset(cx, r))
    }
}

/** Two columns split by a hairline: uppercase label over a medium serif numeral with an optional sans unit ("18.4 MB"). */
@Composable
fun StatRow(vararg stats: Pair<String, String>) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        stats.forEachIndexed { i, (label, value) ->
            if (i > 0) Box(Modifier.padding(horizontal = 16.dp).size(1.dp, 48.dp).background(T.hairline))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Label(label)
                val num = value.substringBefore(' ')
                val unit = value.substringAfter(' ', "")
                Row(verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(num, color = T.text, fontFamily = Serif, fontSize = 26.sp, lineHeight = 32.sp)
                    if (unit.isNotEmpty()) Text(unit, color = T.secondary, fontSize = 13.sp, modifier = Modifier.padding(bottom = 5.dp))
                }
            }
        }
    }
}
