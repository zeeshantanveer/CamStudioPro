package com.camstudio.pro.ui

import android.graphics.Paint
import android.graphics.Typeface
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.nativeCanvas
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.camstudio.pro.Engine
import com.camstudio.pro.data.ProState
import com.camstudio.pro.media.CameraCaps
import kotlin.math.abs
import kotlin.math.roundToInt

private enum class Param(val label: String) { ISO("ISO"), SHUTTER("SHUTTER"), WB("WB"), FOCUS("FOCUS"), EV("EV") }

private val ISO_STOPS = listOf(50, 64, 80, 100, 125, 160, 200, 250, 320, 400, 500, 640, 800, 1000, 1250, 1600, 2000, 2500, 3200, 4000, 5000, 6400, 8000, 10000, 12800)
private val SHUTTER_DENOMS = listOf(8000, 6400, 5000, 4000, 3200, 2500, 2000, 1600, 1250, 1000, 800, 640, 500, 400, 320, 250, 200, 160, 125, 100, 80, 60, 50, 40, 30, 25, 20, 15, 13, 10, 8, 6, 5, 4)
private val KELVIN_STOPS = (2500..8000 step 100).toList()
private val FOCUS_STOPS = (0..50).map { it / 50f }

@Composable
fun ProScreen(engine: Engine, go: (Screen) -> Unit) {
    val pro by engine.pro.collectAsState()
    val caps by engine.camera.caps.collectAsState()
    val live by engine.camera.live.collectAsState()
    val hist by engine.pipeline.histogram.collectAsState()
    var param by remember { mutableStateOf(Param.ISO) }
    var savePrompt by remember { mutableStateOf(false) }

    DisposableEffect(Unit) {
        engine.acquireHistogram()
        onDispose { engine.releaseHistogram() }
    }

    Box(Modifier.fillMaxSize().background(C.Ground)) {
        // Preview
        Box(Modifier.fillMaxWidth().height(400.dp)) {
            CameraPreview(engine, Modifier.fillMaxSize(), cover = true)
            Row(
                Modifier.fillMaxWidth().background(Brush.verticalGradient(listOf(Color(0x99000000), Color.Transparent)))
                    .windowInsetsPadding(WindowInsets.statusBars).padding(14.dp),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                IconBtn(Ic.Back, "Back to camera") { go(Screen.Live) }
                Box(Modifier.height(32.dp).clip(RoundedCornerShape(8.dp)).background(C.Accent).padding(horizontal = 10.dp),
                    contentAlignment = Alignment.Center) { T("PRO", 12, C.OnAccent, 700, ls = 0.8f) }
                Spacer(Modifier.weight(1f))
                val lockColor = if (pro.locked) C.Accent else C.Text3
                Row(
                    Modifier.height(36.dp).clip(RoundedCornerShape(8.dp)).background(C.Glass)
                        .border(1.dp, if (pro.locked) C.Accent else C.Outline, RoundedCornerShape(8.dp))
                        .clickable(role = Role.Switch) { engine.setPro { copy(locked = !locked) } }
                        .semantics { contentDescription = if (pro.locked) "Unlock exposure and focus" else "Lock exposure and focus" }
                        .padding(horizontal = 10.dp),
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(5.dp),
                ) {
                    I(Ic.Lock, 13, lockColor)
                    T("AE/AF LOCK", 11, lockColor, 600, mono = true)
                }
            }
            // Histogram
            Box(
                Modifier.align(Alignment.BottomStart).padding(start = 14.dp, bottom = 30.dp).size(124.dp, 58.dp)
                    .clip(RoundedCornerShape(10.dp)).background(Color(0xCC08090B)).border(1.dp, Color(0x14FFFFFF), RoundedCornerShape(10.dp))
                    .padding(6.dp).semantics { contentDescription = "Histogram" },
            ) {
                val bins = hist
                Canvas(Modifier.fillMaxSize()) {
                    if (bins == null || bins.isEmpty()) return@Canvas
                    val max = (bins.maxOrNull() ?: 1f).coerceAtLeast(1e-6f)
                    val p = Path().apply {
                        moveTo(0f, size.height)
                        bins.forEachIndexed { i, v ->
                            lineTo(i * size.width / (bins.size - 1).coerceAtLeast(1), size.height * (1f - v / max))
                        }
                        lineTo(size.width, size.height)
                        close()
                    }
                    drawPath(p, Color(0xCCF4F5F7))
                }
            }
            Row(Modifier.align(Alignment.BottomEnd).padding(end = 14.dp, bottom = 30.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                if (pro.peaking) Badge("PEAKING", C.Green)
                if (pro.zebra) Badge("ZEBRA 95", C.Text3)
            }
        }

        // Sheet
        Column(
            Modifier.fillMaxSize().padding(top = 384.dp).clip(RoundedCornerShape(topStart = 22.dp, topEnd = 22.dp))
                .background(C.Nav).border(1.dp, C.Border, RoundedCornerShape(topStart = 22.dp, topEnd = 22.dp))
                .windowInsetsPadding(WindowInsets.navigationBars),
        ) {
            Box(Modifier.padding(top = 10.dp).align(Alignment.CenterHorizontally).size(36.dp, 4.dp).clip(RoundedCornerShape(2.dp)).background(C.Outline))
            Column(
                Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                // Parameter tabs
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    Param.entries.forEach { p ->
                        val sel = p == param
                        Column(
                            Modifier.weight(1f).height(58.dp).clip(RoundedCornerShape(12.dp))
                                .background(if (sel) C.Chip else C.Card)
                                .border(1.dp, if (sel) C.Accent else C.Border, RoundedCornerShape(12.dp))
                                .clickable(role = Role.Tab) { param = p },
                            verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally,
                        ) {
                            T(p.label, 10, if (sel) C.Accent else C.Muted, 600, ls = 0.6f, maxLines = 1)
                            Spacer(Modifier.height(3.dp))
                            T(valueLabel(p, pro, caps, live.iso, live.shutterNs, live.focusDiopters), 14, C.Text, 600, mono = true, maxLines = 1)
                        }
                    }
                }

                ParamPanel(engine, param, pro, caps, live.iso, live.shutterNs, live.focusDiopters)

                // Assist toggles
                CardBox {
                    Row {
                        AssistCell("Focus peaking", "Green outline", Modifier.weight(1f), pro.peaking) { engine.setPro { copy(peaking = it) } }
                        Box(Modifier.width(1.dp).height(56.dp).background(C.Border))
                        AssistCell("Zebra stripes", "Above 95%", Modifier.weight(1f), pro.zebra) { engine.setPro { copy(zebra = it) } }
                    }
                    Hairline(C.Border)
                    Row {
                        AssistCell("Stabilization", if (caps.stabilization) "Electronic" else "Not on this camera",
                            Modifier.weight(1f), pro.stabilization, enabled = caps.stabilization) { engine.setPro { copy(stabilization = it) } }
                        Box(Modifier.width(1.dp).height(56.dp).background(C.Border))
                        Row(Modifier.weight(1f).height(56.dp).padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                T("Anti‑flicker", 13, C.Text, 600)
                                T("Mains lighting", 11, C.Muted, modifier = Modifier.padding(top = 2.dp))
                            }
                            Box(
                                Modifier.height(32.dp).clip(RoundedCornerShape(8.dp)).border(1.dp, C.Outline, RoundedCornerShape(8.dp))
                                    .clickable(role = Role.Button, onClickLabel = "Change anti-flicker frequency") {
                                        engine.setPro { copy(antiFlicker = if (antiFlicker == 50) 60 else 50) }
                                    }.padding(horizontal = 10.dp),
                                contentAlignment = Alignment.Center,
                            ) { T("${pro.antiFlicker}Hz", 12, C.Text, 600, mono = true) }
                        }
                    }
                }
            }
            Row(Modifier.padding(start = 16.dp, end = 16.dp, bottom = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Btn("Reset to auto", Modifier.weight(1f)) {
                    engine.setPro { ProState(peaking = peaking, zebra = zebra, stabilization = stabilization, antiFlicker = antiFlicker) }
                    engine.setToggles { copy(mode = 0) }
                }
                Btn("Save as preset", Modifier.weight(1f), primary = true) { savePrompt = true }
            }
        }
    }

    if (savePrompt) SavePresetDialog(onDismiss = { savePrompt = false }) { name ->
        engine.saveUserPreset(name)
        engine.toast.tryEmit("Saved preset “$name”")
        savePrompt = false
    }
}

@Composable
private fun Badge(text: String, color: Color) {
    Box(Modifier.clip(RoundedCornerShape(6.dp)).background(Color(0xCC08090B)).padding(horizontal = 7.dp, vertical = 4.dp)) {
        T(text, 10, color, 600, mono = true)
    }
}

@Composable
private fun AssistCell(title: String, sub: String, modifier: Modifier, checked: Boolean, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Row(modifier.height(56.dp).padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            T(title, 13, if (enabled) C.Text else C.Muted, 600, maxLines = 1)
            T(sub, 11, C.Muted, modifier = Modifier.padding(top = 2.dp), maxLines = 1)
        }
        Toggle(checked, title, enabled, onChange = onChange)
    }
}

private fun focusLabel(f: Float, minDiopters: Float): String {
    if (f <= 0.001f || minDiopters <= 0f) return "∞"
    val d = f * minDiopters
    val m = 1f / d
    return if (m >= 1f) "%.1fm".format(m) else "${(m * 100).roundToInt()}cm"
}

private fun valueLabel(p: Param, pro: ProState, caps: CameraCaps, iso: Int?, shutter: Long?, focusD: Float?): String = when (p) {
    Param.ISO -> if (pro.isoManual) pro.iso.toString() else iso?.toString() ?: "Auto"
    Param.SHUTTER -> shutterLabel(if (pro.shutterManual) pro.shutterNs else shutter)
    Param.WB -> if (pro.wbManual) "${pro.kelvin}K" else "Auto"
    Param.FOCUS -> if (pro.focusManual) focusLabel(pro.focus, caps.minFocusDiopters)
        else if (pro.locked) "AF‑L" else "AF‑C"
    Param.EV -> if (caps.evStep > 0f) evLabel(pro.evIndex, caps.evStep) else "—"
}

@Composable
private fun ParamPanel(engine: Engine, p: Param, pro: ProState, caps: CameraCaps, iso: Int?, shutter: Long?, focusD: Float?) {
    val supported = when (p) {
        Param.ISO, Param.SHUTTER -> caps.manualSensor
        Param.WB -> caps.manualWb || caps.awbModes.size > 1
        Param.FOCUS -> caps.minFocusDiopters > 0f
        Param.EV -> caps.evMax > caps.evMin
    }
    val manual = when (p) {
        Param.ISO -> pro.isoManual
        Param.SHUTTER -> pro.shutterManual
        Param.WB -> pro.wbManual
        Param.FOCUS -> pro.focusManual
        Param.EV -> pro.evIndex != 0
    }
    val title = when (p) {
        Param.ISO -> "ISO sensitivity"
        Param.SHUTTER -> "Shutter speed"
        Param.WB -> "White balance"
        Param.FOCUS -> "Focus distance"
        Param.EV -> "Exposure compensation"
    }

    // Stops for the ruler and the currently selected index
    val isoStops = ISO_STOPS.filter { it in caps.isoMin..caps.isoMax }.ifEmpty { listOf(caps.isoMin, caps.isoMax).distinct() }
    val shutterStops = SHUTTER_DENOMS.map { 1_000_000_000L / it }.filter { it in caps.shutterMinNs..caps.shutterMaxNs }
        .ifEmpty { listOf(16_666_667L) }
    val evStops = (caps.evMin..caps.evMax).toList().ifEmpty { listOf(0) }
    val count: Int
    val index: Int
    val labelAt: (Int) -> String
    when (p) {
        Param.ISO -> {
            count = isoStops.size
            val cur = if (pro.isoManual) pro.iso else iso ?: 400
            index = isoStops.indices.minByOrNull { abs(isoStops[it] - cur) } ?: 0
            labelAt = { isoStops[it].toString() }
        }
        Param.SHUTTER -> {
            count = shutterStops.size
            val cur = if (pro.shutterManual) pro.shutterNs else shutter ?: 16_666_667L
            index = shutterStops.indices.minByOrNull { abs(shutterStops[it] - cur) } ?: 0
            labelAt = { shutterLabel(shutterStops[it]) }
        }
        Param.WB -> {
            count = KELVIN_STOPS.size
            index = KELVIN_STOPS.indices.minByOrNull { abs(KELVIN_STOPS[it] - pro.kelvin) } ?: 0
            labelAt = { "${KELVIN_STOPS[it] / 100 * 100}" }
        }
        Param.FOCUS -> {
            count = FOCUS_STOPS.size
            val cur = if (pro.focusManual) pro.focus else ((focusD ?: 0f) / caps.minFocusDiopters.coerceAtLeast(0.01f)).coerceIn(0f, 1f)
            index = FOCUS_STOPS.indices.minByOrNull { abs(FOCUS_STOPS[it] - cur) } ?: 0
            labelAt = { focusLabel(FOCUS_STOPS[it], caps.minFocusDiopters) }
        }
        Param.EV -> {
            count = evStops.size
            index = evStops.indexOf(pro.evIndex.coerceIn(caps.evMin, caps.evMax)).coerceAtLeast(0)
            labelAt = { if (caps.evStep > 0f) evLabel(evStops[it], caps.evStep) else "0" }
        }
    }

    fun setIndex(i: Int) {
        when (p) {
            Param.ISO -> engine.setPro { copy(isoManual = true, iso = isoStops[i]) }
            Param.SHUTTER -> engine.setPro { copy(shutterManual = true, shutterNs = shutterStops[i]) }
            Param.WB -> engine.setPro { copy(wbManual = true, kelvin = KELVIN_STOPS[i]) }
            Param.FOCUS -> engine.setPro { copy(focusManual = true, focus = FOCUS_STOPS[i]) }
            Param.EV -> engine.setPro { copy(evIndex = evStops[i]) }
        }
        engine.setToggles { copy(mode = 1) }
    }

    fun setAuto(auto: Boolean) {
        if (auto) when (p) {
            Param.ISO -> engine.setPro { copy(isoManual = false) }
            Param.SHUTTER -> engine.setPro { copy(shutterManual = false) }
            Param.WB -> engine.setPro { copy(wbManual = false) }
            Param.FOCUS -> engine.setPro { copy(focusManual = false) }
            Param.EV -> engine.setPro { copy(evIndex = 0) }
        } else setIndex(index)
    }

    CardBox(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(start = 14.dp, end = 14.dp, top = 14.dp, bottom = 12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Row(Modifier.weight(1f), verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    T(valueLabel(p, pro, caps, iso, shutter, focusD), 28, C.Text, 600, mono = true, ls = -0.5f, maxLines = 1)
                    T(title, 12, C.Muted, modifier = Modifier.padding(bottom = 5.dp), maxLines = 1)
                }
                if (p != Param.EV) {
                    Segmented(listOf("Auto", "Manual"), if (manual) 1 else 0, enabled = { it == 0 || supported }) { setAuto(it == 0) }
                } else {
                    Btn("0", height = 32, radius = 8, size = 12, enabled = supported && pro.evIndex != 0) { setAuto(true) }
                }
            }
            Spacer(Modifier.height(12.dp))
            if (supported) {
                RulerDial(count, index, labelAt) { setIndex(it) }
            } else {
                Box(Modifier.fillMaxWidth().height(50.dp), contentAlignment = Alignment.Center) {
                    T(if (caps.ready) "This camera doesn't allow manual ${p.label.lowercase()}" else "Camera starting…", 12, C.Muted)
                }
            }
        }
    }
}

/** Horizontal tick ruler. Drag to move between [count] stops; the centre marker is the value. */
@Composable
private fun RulerDial(count: Int, index: Int, labelAt: (Int) -> String, onIndex: (Int) -> Unit) {
    val spacingDp = 14.dp
    val spacing = spacingDp.px
    var drag by remember { mutableFloatStateOf(0f) }
    var start by remember { mutableIntStateOf(index) }
    val cur by rememberUpdatedState(index)
    val cb by rememberUpdatedState(onIndex)
    val labelColor = C.Muted.toArgb()
    val selColor = C.Text.toArgb()
    val density = androidx.compose.ui.platform.LocalDensity.current
    val paint = remember {
        Paint(Paint.ANTI_ALIAS_FLAG).apply {
            typeface = Typeface.MONOSPACE
            textAlign = Paint.Align.CENTER
            textSize = with(density) { 10.sp.toPx() }
        }
    }
    val accent = C.Accent
    Box(
        Modifier.fillMaxWidth().height(54.dp)
            .pointerInput(count) {
                detectHorizontalDragGestures(
                    onDragStart = { start = cur; drag = 0f },
                    onDragEnd = { drag = 0f },
                    onDragCancel = { drag = 0f },
                ) { change, dx ->
                    change.consume()
                    drag += dx
                    val target = (start - (drag / spacing).roundToInt()).coerceIn(0, count - 1)
                    if (target != cur) cb(target)
                }
            }
            .semantics { contentDescription = "Value dial: ${labelAt(index)}. Drag left or right to change." },
    ) {
        Canvas(Modifier.fillMaxSize()) {
            val cx = size.width / 2
            val visible = (size.width / spacing / 2).toInt() + 1
            for (k in -visible..visible) {
                val i = index + k
                if (i < 0 || i >= count) continue
                val x = cx + k * spacing
                val major = i % 3 == 0
                drawLine(if (major) Color(0xFF8A909B) else Color(0xFF3F444E), Offset(x, 6.dp.toPx()),
                    Offset(x, (if (major) 22 else 14).dp.toPx()), 1.dp.toPx())
                if (major || k == 0) {
                    paint.color = if (k == 0) selColor else labelColor
                    paint.isFakeBoldText = k == 0
                    drawContext.canvas.nativeCanvas.drawText(labelAt(i), x, size.height - 2.dp.toPx(), paint)
                }
            }
            drawLine(accent, Offset(cx, 0f), Offset(cx, 28.dp.toPx()), 2.dp.toPx())
        }
    }
}

@Composable
fun SavePresetDialog(onDismiss: () -> Unit, onSave: (String) -> Unit) {
    var name by remember { mutableStateOf("My preset") }
    Sheet("Save as preset", onDismiss) {
        T("Saves the current resolution, frame rate, bitrate and pro camera settings.", 13, C.Muted, lh = 19)
        Spacer(Modifier.height(12.dp))
        BasicTextField(
            value = name, onValueChange = { name = it.take(32) }, singleLine = true,
            textStyle = TextStyle(color = C.Text, fontSize = 15.sp, fontFamily = Inter),
            cursorBrush = SolidColor(C.Accent),
            modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(C.Ground)
                .border(1.dp, C.Outline, RoundedCornerShape(12.dp)).padding(horizontal = 14.dp, vertical = 13.dp)
                .semantics { contentDescription = "Preset name" },
        )
        Spacer(Modifier.height(16.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Btn("Cancel", Modifier.weight(1f), onClick = onDismiss)
            Btn("Save", Modifier.weight(1f), primary = true, enabled = name.isNotBlank()) { onSave(name.trim()) }
        }
    }
}
