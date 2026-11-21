package com.camstudio.pro.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import com.camstudio.pro.ConnStatus
import com.camstudio.pro.Engine
import com.camstudio.pro.data.resLabel
import kotlinx.coroutines.delay
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

fun shutterLabel(ns: Long?): String {
    if (ns == null || ns <= 0) return "—"
    val sec = ns / 1e9
    return if (sec >= 0.4) "%.1fs".format(sec) else "1/${(1.0 / sec).roundToInt()}"
}

fun evLabel(index: Int, step: Float): String {
    val v = index * step
    return if (abs(v) < 0.05f) "0.0" else "%+.1f".format(v)
}

fun durationLabel(ms: Long): String {
    val s = (ms / 1000).coerceAtLeast(0)
    return "%02d:%02d:%02d".format(s / 3600, (s / 60) % 60, s % 60)
}

@Composable
fun LiveScreen(engine: Engine, go: (Screen) -> Unit) {
    val conn by engine.conn.collectAsState()
    val health by engine.health.collectAsState()
    val toggles by engine.toggles.collectAsState()
    val pro by engine.pro.collectAsState()
    val settings by engine.settings.collectAsState()
    val caps by engine.camera.caps.collectAsState()
    val live by engine.camera.live.collectAsState()
    val camError by engine.camera.error.collectAsState()
    val face by engine.pipeline.face.collectAsState()
    val mic by engine.micLevel.collectAsState()
    val isLive = conn.status == ConnStatus.Live
    val cameraEnabled by engine.cameraEnabled.collectAsState()
    val density = LocalDensity.current

    DisposableEffect(Unit) {
        engine.acquireFace()
        onDispose { engine.releaseFace() }
    }

    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(isLive) {
        while (isLive) {
            now = System.currentTimeMillis()
            delay(500)
        }
    }
    var peak by remember { mutableFloatStateOf(0f) }
    LaunchedEffect(mic) { peak = max(mic, peak * 0.92f) }

    var view by remember { mutableStateOf(IntSize.Zero) }

    Box(Modifier.fillMaxSize().background(Color.Black).onSizeChanged { view = it }) {
        CameraPreview(engine, Modifier.fillMaxSize(), cover = false)

        // Red frame while the stream is going out.
        if (isLive && cameraEnabled && !toggles.paused) Box(Modifier.fillMaxSize().border(3.dp, C.Red))

        // FACE AF reticle
        val f = face.takeIf { cameraEnabled }
        if (f != null && view.width > 0) {
            val a = engine.pipeline.outToView(f.left, f.top, view.width, view.height, false)
            val b = engine.pipeline.outToView(f.right, f.bottom, view.width, view.height, false)
            if (a != null && b != null) {
                val l = min(a.x, b.x); val t = min(a.y, b.y); val r = max(a.x, b.x); val bt = max(a.y, b.y)
                Canvas(Modifier.fillMaxSize()) {
                    val k = 20.dp.toPx()
                    val w = 2.dp.toPx()
                    fun corner(x: Float, y: Float, dx: Float, dy: Float) {
                        drawLine(C.Accent, Offset(x, y), Offset(x + dx * k, y), w)
                        drawLine(C.Accent, Offset(x, y), Offset(x, y + dy * k), w)
                    }
                    corner(l, t, 1f, 1f); corner(r, t, -1f, 1f); corner(l, bt, 1f, -1f); corner(r, bt, -1f, -1f)
                }
                Box(
                    Modifier.offset2(l, t - with(density) { 24.dp.toPx() })
                        .clip(RoundedCornerShape(4.dp)).background(C.Accent).padding(horizontal = 6.dp, vertical = 2.dp)
                ) { T("FACE AF", 10, C.OnAccent, 600, mono = true) }
            }
        }

        if (!cameraEnabled) {
            Box(Modifier.fillMaxSize().background(androidx.compose.ui.graphics.Color.Black), contentAlignment = Alignment.Center) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    T("Camera is off", 20, C.Text, 600)
                    T("Open camera from your computer when you need it.", 13, C.Muted)
                    androidx.compose.material3.TextButton(onClick = { engine.setCameraEnabled(true) }) {
                        androidx.compose.material3.Text("Open camera")
                    }
                }
            }
        }
        if (cameraEnabled && toggles.paused) {
            Box(Modifier.align(Alignment.Center).clip(RoundedCornerShape(10.dp)).background(C.Glass).padding(horizontal = 14.dp, vertical = 8.dp)) {
                T("VIDEO PAUSED", 12, C.Text, 600, mono = true)
            }
        }
        camError?.takeIf { cameraEnabled }?.let {
            Box(Modifier.align(Alignment.Center).padding(32.dp).clip(RoundedCornerShape(12.dp)).background(C.Glass).padding(14.dp)) {
                T(it, 13, C.Text, 500, lh = 19)
            }
        }

        // Top bar
        Box(
            Modifier.fillMaxWidth().background(Brush.verticalGradient(listOf(Color(0xB3000000), Color.Transparent)))
                .windowInsetsPadding(WindowInsets.statusBars).padding(start = 14.dp, end = 14.dp, top = 14.dp, bottom = 36.dp)
        ) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                IconBtn(Ic.Back, "Back to connections") { go(Screen.Connect) }
                if (isLive) {
                    Row(Modifier.height(32.dp).clip(RoundedCornerShape(8.dp)).background(C.LiveRed).padding(horizontal = 10.dp),
                        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        Dot(7, Color.White)
                        T("LIVE ${durationLabel(now - conn.liveSince)}", 12, Color.White, 600, mono = true)
                    }
                } else {
                    Box(Modifier.height(32.dp).clip(RoundedCornerShape(8.dp)).background(C.Glass).padding(horizontal = 10.dp),
                        contentAlignment = Alignment.Center) {
                        T(if (conn.status == ConnStatus.Idle) "PREVIEW" else "CONNECTING", 12, C.Text3, 600, mono = true)
                    }
                }
                Row(
                    Modifier.weight(1f).height(32.dp).clip(RoundedCornerShape(8.dp)).background(C.Glass)
                        .clickable(enabled = isLive, role = Role.Button) { go(Screen.Health) }
                        .semantics { contentDescription = "Stream health" }.padding(horizontal = 10.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Dot(7, if (isLive) C.Green else Color(0xFF5A606C))
                    Spacer(Modifier.width(6.dp))
                    T(if (conn.status == ConnStatus.Idle) "Not connected" else conn.pcName, 12, C.Text3, 500, Modifier.weight(1f), maxLines = 1)
                    if (isLive) T(health.delayMs?.let { "${it}ms" } ?: health.rttMs?.let { "${it}ms" } ?: "—", 12, Color(0xFF9BE8B4), 500, mono = true)
                }
                Row(Modifier.height(32.dp).clip(RoundedCornerShape(8.dp)).background(C.Glass).padding(horizontal = 8.dp),
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    I(Ic.Battery, 16, C.Text3, "Battery")
                    T(health.battery?.let { "$it%" } ?: "—", 12, C.Text3, 600, mono = true)
                }
            }
        }

        // Mic meter
        Column(
            Modifier.align(Alignment.CenterStart).padding(start = 14.dp, bottom = 120.dp).clip(RoundedCornerShape(10.dp))
                .background(C.Glass).padding(horizontal = 7.dp, vertical = 8.dp)
                .semantics { contentDescription = "Microphone level" },
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            val on = isLive && settings.micEnabled && !toggles.muted
            Row(Modifier.height(120.dp), horizontalArrangement = Arrangement.spacedBy(3.dp)) {
                for (v in listOf(mic, peak)) {
                    Box(Modifier.width(5.dp).fillMaxHeight().clip(RoundedCornerShape(3.dp)).background(Color(0xFF2A2E36)),
                        contentAlignment = Alignment.BottomCenter) {
                        val c = if (v > 0.92f) C.Red else if (v > 0.8f) C.Accent else C.Green
                        if (on) Box(Modifier.fillMaxWidth().fillMaxHeight(v.coerceIn(0f, 1f)).clip(RoundedCornerShape(3.dp)).background(c))
                    }
                }
            }
            Spacer(Modifier.height(6.dp))
            T(if (toggles.muted) "MUTE" else "MIC", 9, if (on) C.Text2 else C.Muted, 600, mono = true)
        }

        // Right rail
        Column(
            Modifier.align(Alignment.TopEnd).windowInsetsPadding(WindowInsets.statusBars).padding(top = 82.dp, end = 12.dp)
                .clip(RoundedCornerShape(16.dp)).background(C.Glass).padding(5.dp),
            verticalArrangement = Arrangement.spacedBy(2.dp),
        ) {
            RailBtn(Ic.Torch, "Torch", live.torchOn, enabled = caps.torch) { engine.camera.setTorch(!live.torchOn) }
            RailBtn(Ic.Grid, "Grid overlay", toggles.grid) { engine.setToggles { copy(grid = !grid) } }
            RailBtn(Ic.Mirror, "Mirror video", toggles.mirror) { engine.setToggles { copy(mirror = !mirror) } }
            RailBtn(Ic.Rotate, "Rotate stream", toggles.manualRot != 0) { engine.setToggles { copy(manualRot = (manualRot + 90) % 360) } }
            RailBtn(Ic.Moon, "Dim screen while streaming", false) { engine.setToggles { copy(dimmed = true) } }
        }

        // Bottom controls
        Column(
            Modifier.align(Alignment.BottomCenter).fillMaxWidth()
                .background(Brush.verticalGradient(0f to Color.Transparent, 0.38f to Color(0xEB000000), 1f to Color(0xEB000000)))
                .windowInsetsPadding(WindowInsets.navigationBars).padding(start = 14.dp, end = 14.dp, top = 70.dp, bottom = 18.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            // Zoom
            val presets = listOf(0.6f, 1f, 2f, 5f).filter { it == 1f || (it >= caps.zoomMin - 0.01f && it <= caps.zoomMax + 0.01f) }
            Row(Modifier.clip(CircleShape).background(Color(0xBF0C0D10)).padding(4.dp), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                val nearest = presets.minByOrNull { abs(it - live.zoom) }
                presets.forEach { z ->
                    val sel = z == nearest && abs(z - live.zoom) < 0.08f
                    val base = if (z < 1f) z.toString().removePrefix("0") else z.roundToInt().toString()
                    Box(
                        Modifier.size(if (sel) 48.dp else 44.dp, 36.dp).clip(CircleShape).background(if (sel) C.Text else Color.Transparent)
                            .clickable(role = Role.Button) { engine.camera.setZoom(z) },
                        contentAlignment = Alignment.Center,
                    ) { T(if (sel) "$base×" else base, 12, if (sel) C.Ground else C.Text3, 600, mono = true) }
                }
                if (nearest == null || abs(nearest - live.zoom) >= 0.08f) {
                    Box(Modifier.size(52.dp, 36.dp).clip(CircleShape).background(C.Text), contentAlignment = Alignment.Center) {
                        T("%.1f×".format(live.zoom), 12, C.Ground, 600, mono = true)
                    }
                }
            }

            // Parameter strip
            Row(
                Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(Color(0xE6111318))
                    .border(1.dp, C.Sel, RoundedCornerShape(14.dp)).clickable(role = Role.Button) { go(Screen.Pro) }
                    .semantics { contentDescription = "Open pro controls" }.padding(vertical = 8.dp, horizontal = 4.dp),
            ) {
                val (ow, oh) = engine.outSize()
                val items = listOf(
                    "ISO" to (live.iso?.toString() ?: "—"),
                    "SHUTTER" to shutterLabel(live.shutterNs),
                    "WB" to if (pro.wbManual) "${pro.kelvin}K" else "Auto",
                    "EV" to if (caps.evStep > 0f) evLabel(pro.evIndex, caps.evStep) else "—",
                    "OUTPUT" to "${resLabel(min(ow, oh))}${settings.fps}",
                )
                items.forEachIndexed { i, (k, v) ->
                    Column(Modifier.weight(1f), horizontalAlignment = Alignment.CenterHorizontally) {
                        T(k, 10, C.Muted, 600, ls = 0.6f, maxLines = 1)
                        Spacer(Modifier.height(2.dp))
                        T(v, 14, C.Text, 600, mono = true, maxLines = 1)
                    }
                    if (i < items.lastIndex) Box(Modifier.width(1.dp).height(34.dp).background(C.Sel))
                }
            }

            // Mode tabs
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                listOf("AUTO", "PRO", "PORTRAIT", "LOW LIGHT").forEachIndexed { i, label ->
                    val sel = toggles.mode == i
                    Box(
                        Modifier.clip(CircleShape).background(if (sel) C.Accent else Color.Transparent)
                            .clickable(role = Role.Tab) {
                                engine.setMode(i)
                                if (i == 1) go(Screen.Pro)
                            }.padding(horizontal = 12.dp, vertical = 7.dp)
                    ) { T(label, 12, if (sel) C.OnAccent else Color(0xFFD6DAE0), 600, ls = 0.6f) }
                }
            }

            // Shutter row
            Row(Modifier.fillMaxWidth().padding(horizontal = 6.dp), horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically) {
                RoundBtn(if (toggles.muted) Ic.MicOff else Ic.Mic, if (toggles.muted) "Unmute microphone" else "Mute microphone",
                    active = toggles.muted, enabled = settings.micEnabled) { engine.setToggles { copy(muted = !muted) } }
                RoundBtn(if (toggles.paused) Ic.Play else Ic.Pause, if (toggles.paused) "Resume video" else "Pause video",
                    active = toggles.paused, enabled = isLive) { engine.setToggles { copy(paused = !paused) } }
                Box(
                    Modifier.size(76.dp).clip(CircleShape).border(3.dp, C.Text, CircleShape)
                        .clickable(role = Role.Button) { if (conn.status == ConnStatus.Idle) go(Screen.Connect) else engine.disconnect() }
                        .semantics { contentDescription = if (isLive) "Stop streaming" else "Start streaming" },
                    contentAlignment = Alignment.Center,
                ) {
                    if (conn.status == ConnStatus.Idle) Box(Modifier.size(54.dp).clip(CircleShape).background(C.Red))
                    else Box(Modifier.size(28.dp).clip(RoundedCornerShape(7.dp)).background(C.Red))
                }
                RoundBtn(Ic.Camera, "Take snapshot", enabled = caps.ready) { engine.takeSnapshot() }
                RoundBtn(Ic.Switch, if (caps.front) "Switch to rear camera" else "Switch to front camera",
                    enabled = caps.hasFront && caps.hasBack) { engine.setToggles { copy(front = !front) } }
            }
        }
    }
}

private fun Modifier.offset2(x: Float, y: Float): Modifier =
    this.offset { IntOffset(x.roundToInt(), y.roundToInt()) }

@Composable
private fun RailBtn(v: androidx.compose.ui.graphics.vector.ImageVector, desc: String, on: Boolean, enabled: Boolean = true, onClick: () -> Unit) {
    IconBtn(v, desc, bg = if (on) C.Accent else Color.Transparent, tint = if (on) C.OnAccent else C.Text3, enabled = enabled, onClick = onClick)
}

@Composable
private fun RoundBtn(v: androidx.compose.ui.graphics.vector.ImageVector, desc: String, active: Boolean = false, enabled: Boolean = true, onClick: () -> Unit) {
    IconBtn(v, desc, bg = if (active) C.Text else Color(0xE6181B21), tint = if (active) C.Ground else C.Text,
        border = if (active) null else C.Outline, size = 52, radius = 26, iconSize = 21, enabled = enabled, onClick = onClick)
}
