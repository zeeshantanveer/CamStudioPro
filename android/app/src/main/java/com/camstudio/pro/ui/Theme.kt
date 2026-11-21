package com.camstudio.pro.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.semantics.ProgressBarRangeInfo
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.disabled
import androidx.compose.ui.semantics.progressBarRangeInfo
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.setProgress
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontVariation
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import com.camstudio.pro.R
import kotlin.math.roundToInt

/** Theme-dependent colours. Brand colours that read well in both themes live directly in [C]. */
@Immutable
data class Palette(
    val isDark: Boolean,
    val ground: Color, val card: Color, val border: Color, val border2: Color, val line: Color,
    val muted: Color, val text: Color, val text2: Color, val text3: Color,
    val sel: Color, val chip: Color, val outline: Color, val nav: Color, val glass: Color,
    val accentText: Color, val green: Color, val goodText: Color, val goodBg: Color,
    val dangerBg: Color, val dangerLine: Color, val dangerText: Color,
    val placeholder: Color, val track: Color, val toast: Color, val knob: Color, val thumb: Color,
    val segBg: Color, val segSel: Color,
)

val DarkPalette = Palette(
    isDark = true,
    ground = Color(0xFF08090B), card = Color(0xFF111318), border = Color(0xFF1E2128), border2 = Color(0xFF262A32),
    line = Color(0xFF1A1D23), muted = Color(0xFF9BA1AC), text = Color(0xFFF4F5F7), text2 = Color(0xFFC7CCD4),
    text3 = Color(0xFFE8EAEE), sel = Color(0xFF23272F), chip = Color(0xFF181B21), outline = Color(0xFF2E333D),
    nav = Color(0xFF0C0D10), glass = Color(0xB80C0D10),
    accentText = Color(0xFFFFB020), green = Color(0xFF4ADE80), goodText = Color(0xFF4ADE80), goodBg = Color(0xFF12261C),
    dangerBg = Color(0xFF2A1210), dangerLine = Color(0xFF5C1F1B), dangerText = Color(0xFFFF8A80),
    placeholder = Color(0xFF5A606C), track = Color(0xFF23272F), toast = Color(0xF0181B21),
    knob = Color(0xFFC7CCD4), thumb = Color(0xFFF4F5F7), segBg = Color(0xFF08090B), segSel = Color(0xFF23272F),
)

val LightPalette = Palette(
    isDark = false,
    ground = Color(0xFFF5F6F8), card = Color(0xFFFFFFFF), border = Color(0xFFE3E6EB), border2 = Color(0xFFD5D9E0),
    line = Color(0xFFECEEF2), muted = Color(0xFF5E6571), text = Color(0xFF14171C), text2 = Color(0xFF3A404A),
    text3 = Color(0xFF2A2F37), sel = Color(0xFFEDEFF3), chip = Color(0xFFEEF0F3), outline = Color(0xFFC9CED6),
    nav = Color(0xFFFFFFFF), glass = Color(0xE6FFFFFF),
    accentText = Color(0xFFA35200), green = Color(0xFF16A34A), goodText = Color(0xFF15803D), goodBg = Color(0xFFDCFCE7),
    dangerBg = Color(0xFFFEF0EF), dangerLine = Color(0xFFF5C2BE), dangerText = Color(0xFFB42318),
    placeholder = Color(0xFF8A919C), track = Color(0xFFE3E6EB), toast = Color(0xF7FFFFFF),
    knob = Color(0xFFFFFFFF), thumb = Color(0xFFFFFFFF), segBg = Color(0xFFECEEF2), segSel = Color(0xFFFFFFFF),
)

val LocalPalette = staticCompositionLocalOf { DarkPalette }

/** Forces the dark palette, for controls drawn over the camera image. */
@Composable
fun OverVideo(content: @Composable () -> Unit) = CompositionLocalProvider(LocalPalette provides DarkPalette, content = content)

object C {
    // Brand colours, identical in both themes (usable inside draw lambdas).
    val Accent = Color(0xFFFFB020)
    val OnAccent = Color(0xFF1A1200)
    val Red = Color(0xFFFF453A)
    val LiveRed = Color(0xFFD92D20)

    private val p: Palette @Composable @ReadOnlyComposable get() = LocalPalette.current
    val IsDark: Boolean @Composable @ReadOnlyComposable get() = p.isDark
    val Ground: Color @Composable @ReadOnlyComposable get() = p.ground
    val Card: Color @Composable @ReadOnlyComposable get() = p.card
    val Border: Color @Composable @ReadOnlyComposable get() = p.border
    val Border2: Color @Composable @ReadOnlyComposable get() = p.border2
    val Line: Color @Composable @ReadOnlyComposable get() = p.line
    val Muted: Color @Composable @ReadOnlyComposable get() = p.muted
    val Text: Color @Composable @ReadOnlyComposable get() = p.text
    val Text2: Color @Composable @ReadOnlyComposable get() = p.text2
    val Text3: Color @Composable @ReadOnlyComposable get() = p.text3
    val Sel: Color @Composable @ReadOnlyComposable get() = p.sel
    val Chip: Color @Composable @ReadOnlyComposable get() = p.chip
    val Outline: Color @Composable @ReadOnlyComposable get() = p.outline
    val Nav: Color @Composable @ReadOnlyComposable get() = p.nav
    val Glass: Color @Composable @ReadOnlyComposable get() = p.glass
    /** Accent for text and small icons: amber is unreadable on white, so light mode uses a deep amber. */
    val AccentText: Color @Composable @ReadOnlyComposable get() = p.accentText
    val AccentLink: Color @Composable @ReadOnlyComposable get() = p.accentText
    val Green: Color @Composable @ReadOnlyComposable get() = p.green
    val GoodText: Color @Composable @ReadOnlyComposable get() = p.goodText
    val GoodBg: Color @Composable @ReadOnlyComposable get() = p.goodBg
    val DangerBg: Color @Composable @ReadOnlyComposable get() = p.dangerBg
    val DangerLine: Color @Composable @ReadOnlyComposable get() = p.dangerLine
    val DangerText: Color @Composable @ReadOnlyComposable get() = p.dangerText
    val Placeholder: Color @Composable @ReadOnlyComposable get() = p.placeholder
    val Track: Color @Composable @ReadOnlyComposable get() = p.track
    val Toast: Color @Composable @ReadOnlyComposable get() = p.toast
    val Knob: Color @Composable @ReadOnlyComposable get() = p.knob
    val Thumb: Color @Composable @ReadOnlyComposable get() = p.thumb
    val SegBg: Color @Composable @ReadOnlyComposable get() = p.segBg
    val SegSel: Color @Composable @ReadOnlyComposable get() = p.segSel
}

@OptIn(androidx.compose.ui.text.ExperimentalTextApi::class)
private fun vf(res: Int, w: Int) = Font(res, FontWeight(w), variationSettings = FontVariation.Settings(FontVariation.weight(w)))

val Inter = FontFamily(vf(R.font.inter, 400), vf(R.font.inter, 500), vf(R.font.inter, 600), vf(R.font.inter, 700))
val Mono = FontFamily(vf(R.font.jetbrains_mono, 500), vf(R.font.jetbrains_mono, 600))

@Composable
fun T(
    text: String,
    size: Int,
    color: Color = C.Text,
    weight: Int = 400,
    modifier: Modifier = Modifier,
    mono: Boolean = false,
    ls: Float = 0f,
    align: TextAlign = TextAlign.Unspecified,
    lh: Int = 0,
    maxLines: Int = Int.MAX_VALUE,
) {
    BasicText(
        text = text,
        modifier = modifier,
        style = TextStyle(
            color = color, fontSize = size.sp, fontWeight = FontWeight(weight),
            fontFamily = if (mono) Mono else Inter, letterSpacing = ls.sp, textAlign = align,
            lineHeight = if (lh > 0) lh.sp else TextUnit.Unspecified,
        ),
        maxLines = maxLines,
        overflow = TextOverflow.Ellipsis,
    )
}

// ---------------------------------------------------------------- icons (24 px stroke set from the design)

private fun circle(cx: Float, cy: Float, r: Float) = "M${cx - r},${cy}a$r,$r 0 1,0 ${2 * r},0a$r,$r 0 1,0 ${-2 * r},0z"
private fun rrect(x: Float, y: Float, w: Float, h: Float, r: Float) =
    "M${x + r},${y}h${w - 2 * r}a$r,$r 0 0 1 $r,${r}v${h - 2 * r}a$r,$r 0 0 1 ${-r},${r}h${-(w - 2 * r)}a$r,$r 0 0 1 ${-r},${-r}v${-(h - 2 * r)}a$r,$r 0 0 1 $r,${-r}z"

private fun icon(vararg d: String, filled: List<String> = emptyList(), sw: Float = 2f): ImageVector =
    ImageVector.Builder(defaultWidth = 24.dp, defaultHeight = 24.dp, viewportWidth = 24f, viewportHeight = 24f).apply {
        d.forEach {
            addPath(addPathNodes(it), stroke = SolidColor(Color.Black), strokeLineWidth = sw,
                strokeLineCap = StrokeCap.Round, strokeLineJoin = StrokeJoin.Round)
        }
        filled.forEach { addPath(addPathNodes(it), fill = SolidColor(Color.Black)) }
    }.build()

object Ic {
    val Help = icon(circle(12f, 12f, 10f), "M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3", "M12 17h.01")
    val Phone = icon(rrect(6f, 2f, 12f, 20f, 2.5f), circle(12f, 7f, 1.6f), sw = 1.8f)
    val Laptop = icon(rrect(3f, 4f, 18f, 12f, 2f), "M2 20h20", sw = 1.8f)
    val Wifi = icon("M5 12.55a11 11 0 0 1 14.08 0", "M1.42 9a16 16 0 0 1 21.16 0", "M8.53 16.11a6 6 0 0 1 6.95 0", "M12 20h.01")
    val Usb = icon("M12 2v13", "M9 5l3-3 3 3", rrect(8f, 15f, 8f, 7f, 1.5f))
    val Qr = icon(rrect(3f, 3f, 7f, 7f, 1f), rrect(14f, 3f, 7f, 7f, 1f), rrect(3f, 14f, 7f, 7f, 1f),
        "M14 14h3v3h-3zM21 14v.01M14 21h.01M17 21h4v-4")
    val Link = icon("M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71",
        "M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71")
    val Camera = icon("M23 19a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4l2-3h6l2 3h4a2 2 0 0 1 2 2z", circle(12f, 13f, 4f))
    val Sparkles = icon("M12 3l1.8 5.2L19 10l-5.2 1.8L12 17l-1.8-5.2L5 10l5.2-1.8z",
        "M19 17l.7 1.8 1.8.7-1.8.7-.7 1.8-.7-1.8-1.8-.7 1.8-.7z")
    val Sliders = icon("M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6")
    val Back = icon("M15 18l-6-6 6-6")
    val Chevron = icon("M9 18l6-6-6-6")
    val Battery = icon(rrect(2f, 7f, 18f, 10f, 2f), "M22 11v2")
    val Torch = icon("M13 2L3 14h9l-1 8 10-12h-9l1-8z")
    val Grid = icon(rrect(3f, 3f, 18f, 18f, 2f), "M9 3v18M15 3v18M3 9h18M3 15h18")
    val Mirror = icon("M12 3v18", "M8 7l-5 5 5 5V7z", "M16 7l5 5-5 5V7z")
    val Rotate = icon("M21 12a9 9 0 1 1-3-6.7", "M21 3v6h-6")
    val Moon = icon("M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z")
    val Mic = icon(rrect(9f, 2f, 6f, 12f, 3f), "M19 10v1a7 7 0 0 1-14 0v-1", "M12 18v4")
    val MicOff = icon(rrect(9f, 2f, 6f, 12f, 3f), "M19 10v1a7 7 0 0 1-14 0v-1", "M12 18v4", "M3 3l18 18")
    val Pause = icon(filled = listOf(rrect(6f, 5f, 4f, 14f, 1f), rrect(14f, 5f, 4f, 14f, 1f)))
    val Play = icon(filled = listOf("M8 5l11 7-11 7z"))
    val Switch = icon("M23 4v6h-6", "M1 20v-6h6", "M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15")
    val Lock = icon(rrect(5f, 11f, 14f, 10f, 2f), "M8 11V7a4 4 0 0 1 8 0v4", sw = 2.4f)
    val None = icon(circle(12f, 12f, 9f), "M5.6 5.6l12.8 12.8")
    val Plus = icon("M12 5v14M5 12h14")
    val Check = icon("M5 12l5 5L20 7")
    val Trash = icon("M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13")
}

@Composable
fun I(v: ImageVector, size: Int = 20, tint: Color = C.Text, desc: String? = null, modifier: Modifier = Modifier) {
    Icon(v, desc, modifier.size(size.dp), tint)
}

// ---------------------------------------------------------------- shared components

@Composable
fun CardBox(modifier: Modifier = Modifier, radius: Int = 16, content: @Composable ColumnScope.() -> Unit) {
    Column(
        modifier.clip(RoundedCornerShape(radius.dp)).background(C.Card)
            .border(1.dp, C.Border, RoundedCornerShape(radius.dp)),
        content = content,
    )
}

@Composable
fun Toggle(checked: Boolean, label: String, enabled: Boolean = true, accent: Color = C.Accent, onChange: (Boolean) -> Unit) {
    Box(
        Modifier.alpha(if (enabled) 1f else 0.4f).size(44.dp, 26.dp).clip(RoundedCornerShape(13.dp))
            .background(if (checked) accent else C.Outline)
            .clickable(enabled = enabled, role = Role.Switch, onClickLabel = label) { onChange(!checked) }
            .semantics { contentDescription = label }
            .padding(3.dp),
        contentAlignment = if (checked) Alignment.CenterEnd else Alignment.CenterStart,
    ) {
        Box(Modifier.size(20.dp).shadow(1.dp, CircleShape).clip(CircleShape).background(if (checked) Color.White else C.Knob))
    }
}

/** Small segmented control (Auto/Manual, 24/30/60 …). */
@Composable
fun Segmented(options: List<String>, selected: Int, mono: Boolean = false, enabled: (Int) -> Boolean = { true }, onSelect: (Int) -> Unit) {
    Row(Modifier.clip(RoundedCornerShape(10.dp)).background(C.SegBg).padding(3.dp)) {
        options.forEachIndexed { i, o ->
            val en = enabled(i)
            Box(
                Modifier.height(32.dp).clip(RoundedCornerShape(8.dp))
                    .background(if (i == selected) C.SegSel else Color.Transparent)
                    .clickable(enabled = en) { onSelect(i) }
                    .alpha(if (en) 1f else 0.35f)
                    .padding(horizontal = 12.dp),
                contentAlignment = Alignment.Center,
            ) {
                T(o, 12, if (i == selected) C.Text else C.Muted, 600, mono = mono)
            }
        }
    }
}

@Composable
fun Btn(
    text: String,
    modifier: Modifier = Modifier,
    primary: Boolean = false,
    height: Int = 48,
    radius: Int = 14,
    size: Int = 14,
    enabled: Boolean = true,
    bg: Color? = null,
    fg: Color? = null,
    borderColor: Color? = null,
    onClick: () -> Unit,
) {
    val shape = RoundedCornerShape(radius.dp)
    val background = bg ?: if (primary) C.Accent else Color.Transparent
    Box(
        modifier.alpha(if (enabled) 1f else 0.4f).height(height.dp).clip(shape).background(background)
            .then(if (primary && borderColor == null) Modifier else Modifier.border(1.dp, borderColor ?: C.Outline, shape))
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick)
            .padding(horizontal = 14.dp),
        contentAlignment = Alignment.Center,
    ) {
        T(text, size, fg ?: if (primary) C.OnAccent else C.Text, if (primary) 700 else 600, maxLines = 1)
    }
}

@Composable
fun IconBtn(v: ImageVector, desc: String, modifier: Modifier = Modifier, bg: Color = C.Glass, tint: Color = C.Text,
            border: Color? = null, size: Int = 44, radius: Int = 12, iconSize: Int = 20, enabled: Boolean = true, onClick: () -> Unit) {
    val shape = RoundedCornerShape(radius.dp)
    Box(
        modifier.alpha(if (enabled) 1f else 0.35f).size(size.dp).clip(shape).background(bg)
            .then(if (border != null) Modifier.border(1.dp, border, shape) else Modifier)
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) { I(v, iconSize, tint, desc) }
}

/** Design slider: 4 dp track, accent fill, 20 dp white thumb. */
@Composable
fun Slider(value: Int, min: Int, max: Int, enabled: Boolean = true, accent: Color = C.Accent, onChange: (Int) -> Unit) {
    var width by remember { mutableIntStateOf(1) }
    var dragging by remember { mutableStateOf(false) }
    val cb by rememberUpdatedState(onChange)
    val current by rememberUpdatedState(value)
    fun at(x: Float) = (min + (x / width).coerceIn(0f, 1f) * (max - min)).roundToInt()
    fun emit(v: Int) {
        if (v != current) cb(v)
    }
    Box(
        Modifier.alpha(if (enabled) 1f else 0.4f).fillMaxWidth().height(36.dp).onSizeChanged { width = it.width.coerceAtLeast(1) }
            // Only horizontal drags move the slider. Vertical swipes (and taps that stop a fling)
            // fall through to the scrolling page, so scrolling never changes a value by accident.
            .pointerInput(enabled, min, max) {
                if (enabled) detectHorizontalDragGestures(
                    onDragStart = { dragging = true },
                    onDragEnd = { dragging = false },
                    onDragCancel = { dragging = false },
                ) { change, _ ->
                    change.consume()
                    emit(at(change.position.x))
                }
            }
            .semantics {
                progressBarRangeInfo = ProgressBarRangeInfo(value.toFloat(), min.toFloat()..max.toFloat(), max - min - 1)
                if (enabled) setProgress { target ->
                    emit(target.roundToInt().coerceIn(min, max))
                    true
                } else disabled()
            },
        contentAlignment = Alignment.CenterStart,
    ) {
        val frac = ((value - min).toFloat() / (max - min)).coerceIn(0f, 1f)
        Box(Modifier.fillMaxWidth().height(4.dp).clip(RoundedCornerShape(2.dp)).background(C.Border2))
        if (min < 0) {
            // Bipolar slider: fill from the centre.
            val lo = minOf(frac, 0.5f)
            val hi = maxOf(frac, 0.5f)
            Box(Modifier.offset { IntOffset((lo * width).roundToInt(), 0) }
                .width(with(androidx.compose.ui.platform.LocalDensity.current) { ((hi - lo) * width).toDp() })
                .height(4.dp).clip(RoundedCornerShape(2.dp)).background(accent))
        } else {
            Box(Modifier.fillMaxWidth(frac).height(4.dp).clip(RoundedCornerShape(2.dp)).background(accent))
        }
        val thumb = if (dragging) 24.dp else 20.dp
        val thumbPx = with(androidx.compose.ui.platform.LocalDensity.current) { thumb.toPx() }
        Box(Modifier.offset { IntOffset((frac * (width - thumbPx)).roundToInt(), 0) }
            .shadow(3.dp, CircleShape).size(thumb).clip(CircleShape).background(if (dragging) accent else C.Thumb))
    }
}

@Composable
fun LabeledSlider(label: String, value: Int, min: Int, max: Int, enabled: Boolean = true, onChange: (Int) -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            T(label, 13, if (enabled) C.Text else C.Muted, 600)
            T(if (min < 0 && value > 0) "+$value" else "$value", 12, C.Text2, 500, mono = true)
        }
        Slider(value, min, max, enabled, onChange = onChange)
    }
}

@Composable
fun SectionLabel(text: String, color: Color = C.Muted, modifier: Modifier = Modifier) {
    T(text.uppercase(), 11, color, 600, modifier, ls = 1f)
}

@Composable
fun Hairline(color: Color = C.Line) {
    Box(Modifier.fillMaxWidth().height(1.dp).background(color))
}

/** Sheet dialog used for pickers and prompts. */
@Composable
fun Sheet(title: String, onDismiss: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    Dialog(onDismissRequest = onDismiss) {
        Column(
            Modifier.fillMaxWidth().clip(RoundedCornerShape(20.dp)).background(C.Card)
                .border(1.dp, C.Border, RoundedCornerShape(20.dp)).padding(18.dp),
        ) {
            T(title, 17, C.Text, 700, ls = -0.2f)
            Spacer(Modifier.height(12.dp))
            content()
        }
    }
}

@Composable
fun OptionRow(title: String, subtitle: String? = null, selected: Boolean, enabled: Boolean = true,
              trailing: (@Composable () -> Unit)? = null, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().alpha(if (enabled) 1f else 0.4f).clip(RoundedCornerShape(12.dp))
            .background(if (selected) C.Sel else Color.Transparent)
            .clickable(enabled = enabled, onClick = onClick).padding(horizontal = 12.dp, vertical = 11.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            T(title, 14, C.Text, if (selected) 600 else 400)
            if (subtitle != null) T(subtitle, 12, C.Muted, modifier = Modifier.padding(top = 2.dp), lh = 17)
        }
        if (trailing != null) trailing()
        if (selected) I(Ic.Check, 18, C.AccentText, modifier = Modifier.padding(start = 8.dp))
    }
}

val Dp.px: Float @Composable get() = with(androidx.compose.ui.platform.LocalDensity.current) { toPx() }
