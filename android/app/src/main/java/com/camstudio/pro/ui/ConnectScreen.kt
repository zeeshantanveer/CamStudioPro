package com.camstudio.pro.ui

import android.content.Intent
import android.net.Uri
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
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.camstudio.pro.ConnStatus
import com.camstudio.pro.Engine
import com.camstudio.pro.R
import com.camstudio.pro.net.PcTarget
import com.camstudio.pro.net.Proto
import com.camstudio.pro.net.parsePairUri

private fun usedLabel(ts: Long): String {
    val day = 24 * 3600 * 1000L
    val cal = java.util.Calendar.getInstance()
    cal.set(java.util.Calendar.HOUR_OF_DAY, 0); cal.set(java.util.Calendar.MINUTE, 0)
    cal.set(java.util.Calendar.SECOND, 0); cal.set(java.util.Calendar.MILLISECOND, 0)
    val startOfToday = cal.timeInMillis
    return when {
        ts >= startOfToday -> "used today"
        ts >= startOfToday - day -> "used yesterday"
        else -> "used ${((startOfToday - ts) / day + 1)} days ago"
    }
}

@Composable
fun Logo(size: Int = 34) {
    Canvas(Modifier.size(size.dp).clip(RoundedCornerShape((size * 10 / 34).dp)).background(C.Chip)) {
        val u = this.size.width / 34f
        drawCircle(C.Accent, radius = 9f * u, style = Stroke(2f * u))
        drawCircle(C.Accent, radius = 3.5f * u)
    }
}

@Composable
fun ConnectScreen(engine: Engine, go: (Screen) -> Unit, camGranted: Boolean, askPerms: () -> Unit) {
    val conn by engine.conn.collectAsState()
    val discovered by engine.discovered.collectAsState()
    val saved by engine.savedPcs.collectAsState()
    val health by engine.health.collectAsState()
    val autoTarget by engine.autoTarget.collectAsState()
    val autoPaused by engine.autoPaused.collectAsState()
    val settings by engine.settings.collectAsState()
    var tab by rememberSaveable { mutableIntStateOf(0) }
    var showIp by remember { mutableStateOf(false) }
    var showHelp by remember { mutableStateOf(false) }
    val busy = conn.status != ConnStatus.Idle

    DisposableEffect(Unit) {
        engine.startDiscovery()
        onDispose { engine.stopDiscovery() }
    }

    Column(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.systemBars)) {
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState())) {
            // Header
            Row(Modifier.fillMaxWidth().padding(start = 20.dp, end = 20.dp, top = 16.dp, bottom = 8.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Logo()
                Spacer(Modifier.width(10.dp))
                T("CamStudio", 18, C.Text, 700, ls = -0.3f)
                Spacer(Modifier.width(10.dp))
                Box(Modifier.border(1.dp, C.AccentText, RoundedCornerShape(5.dp)).padding(horizontal = 6.dp, vertical = 2.dp)) {
                    T("PRO", 10, C.AccentText, 700, ls = 1f)
                }
                Spacer(Modifier.weight(1f))
                IconBtn(Ic.Help, "Help", bg = C.Card, tint = C.Text2, border = C.Border) { showHelp = true }
            }

            // Hero
            CardBox(Modifier.padding(start = 20.dp, end = 20.dp, top = 8.dp).fillMaxWidth(), radius = 20) {
                Column(Modifier.padding(20.dp).fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        DeviceTile(Ic.Phone)
                        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                            val mid = if (conn.status == ConnStatus.Live) C.Green else C.Accent
                            val far = C.Outline
                            val near = C.Placeholder
                            Dot(5, far); Dot(5, near); Dot(7, mid); Dot(5, near); Dot(5, far)
                        }
                        DeviceTile(Ic.Laptop)
                    }
                    Spacer(Modifier.height(16.dp))
                    val title = when (conn.status) {
                        ConnStatus.Idle -> "Ready to pair"
                        ConnStatus.Connecting -> "Connecting to ${conn.pcName}"
                        ConnStatus.Waiting -> "Approve on ${conn.pcName}"
                        ConnStatus.Live -> "Streaming to ${conn.pcName}"
                        ConnStatus.Reconnecting -> "Reconnecting to ${conn.pcName}"
                    }
                    T(title, 22, C.Text, 700, ls = -0.4f, align = TextAlign.Center, lh = 28)
                    Spacer(Modifier.height(6.dp))
                    val sub = when (conn.status) {
                        ConnStatus.Idle -> "Send this phone's camera and mic to your computer."
                        ConnStatus.Connecting -> "One moment…"
                        ConnStatus.Waiting -> "Allow this phone in the desktop app."
                        ConnStatus.Live -> "Live on your computer."
                        ConnStatus.Reconnecting -> "Connection lost. Retrying…"
                    }
                    T(sub, 13, C.Muted, align = TextAlign.Center, lh = 19)
                    conn.error?.let {
                        Spacer(Modifier.height(10.dp))
                        Box(Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).background(C.DangerBg)
                            .border(1.dp, C.DangerLine, RoundedCornerShape(10.dp))
                            .clickable { engine.clearError() }.padding(12.dp)) {
                            T(it, 12, C.DangerText, 500, lh = 17)
                        }
                    }
                    if (!busy && autoTarget != null) {
                        Spacer(Modifier.height(12.dp))
                        Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).background(C.Card)
                            .border(1.dp, C.Border, RoundedCornerShape(10.dp)).padding(start = 12.dp, end = 6.dp, top = 6.dp, bottom = 6.dp),
                            verticalAlignment = Alignment.CenterVertically) {
                            Dot(7, C.Accent)
                            Spacer(Modifier.width(10.dp))
                            T("Waiting for $autoTarget…", 12, C.Text2,
                                modifier = Modifier.weight(1f), lh = 17)
                            Spacer(Modifier.width(8.dp))
                            Btn("Stop", height = 36, radius = 10, size = 12) { engine.pauseAuto() }
                        }
                    } else if (!busy && autoPaused && settings.autoConnect && saved.isNotEmpty()) {
                        Spacer(Modifier.height(12.dp))
                        Btn("Auto‑connect to ${saved.first().name}", Modifier.fillMaxWidth(), height = 40, radius = 12, size = 13) {
                            engine.resumeAuto()
                        }
                    }
                    if (busy) {
                        Spacer(Modifier.height(14.dp))
                        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            if (conn.status == ConnStatus.Live) {
                                Btn("Open camera", Modifier.weight(1f), primary = true, height = 44, radius = 12) { go(Screen.Live) }
                                Btn("Disconnect", Modifier.weight(1f), height = 44, radius = 12, bg = C.DangerBg,
                                    fg = C.DangerText, borderColor = C.DangerLine) { engine.disconnect() }
                            } else {
                                Btn("Cancel", Modifier.weight(1f), height = 44, radius = 12) { engine.disconnect() }
                            }
                        }
                    }
                }
            }

            // Connection type tabs
            Row(
                Modifier.padding(start = 20.dp, end = 20.dp, top = 16.dp).fillMaxWidth().clip(RoundedCornerShape(14.dp))
                    .background(C.Card).border(1.dp, C.Border, RoundedCornerShape(14.dp)).padding(4.dp),
                horizontalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                listOf(Triple("Wi‑Fi", Ic.Wifi, 0), Triple("USB", Ic.Usb, 1), Triple("QR code", Ic.Qr, 2)).forEach { (label, ic, i) ->
                    val sel = tab == i
                    Row(
                        Modifier.weight(1f).height(44.dp).clip(RoundedCornerShape(10.dp))
                            .background(if (sel) C.Sel else Color.Transparent).clickable(role = Role.Tab) { tab = i },
                        horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically,
                    ) {
                        I(ic, 16, if (sel) C.Text else C.Muted)
                        Spacer(Modifier.width(6.dp))
                        T(label, 13, if (sel) C.Text else C.Muted, if (sel) 600 else 500, maxLines = 1)
                    }
                }
            }

            when (tab) {
                0 -> {
                    Row(Modifier.padding(start = 24.dp, end = 24.dp, top = 20.dp, bottom = 8.dp).fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically) {
                        SectionLabel("Nearby computers")
                        Spacer(Modifier.weight(1f))
                        Dot(6, C.Accent)
                        Spacer(Modifier.width(6.dp))
                        T("Searching", 12, C.Muted)
                    }
                    CardBox(Modifier.padding(horizontal = 20.dp).fillMaxWidth(), radius = 18) {
                        if (discovered.isEmpty()) {
                            Column(Modifier.padding(16.dp)) {
                                T("No computers found", 15, C.Text, 600)
                                Spacer(Modifier.height(4.dp))
                                T("Open CamStudio Pro on your computer, on the same Wi‑Fi.", 12, C.Muted, lh = 18)
                            }
                        }
                        val sorted = discovered.sortedByDescending { d -> saved.firstOrNull { it.id == d.id }?.lastUsed ?: 0L }
                        sorted.forEachIndexed { i, pc ->
                            val known = saved.firstOrNull { it.id == pc.id }
                            if (i > 0) Hairline(C.Border)
                            Row(Modifier.padding(start = 16.dp, end = 14.dp, top = 14.dp, bottom = 14.dp),
                                verticalAlignment = Alignment.CenterVertically) {
                                Column(Modifier.weight(1f)) {
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        T(pc.name, 15, C.Text, 600, Modifier.weight(1f, fill = false), maxLines = 1)
                                        if (known != null) {
                                            Spacer(Modifier.width(8.dp))
                                            Box(Modifier.clip(RoundedCornerShape(5.dp)).background(C.GoodBg).padding(horizontal = 6.dp, vertical = 2.dp)) {
                                                T("TRUSTED", 10, C.GoodText, 700, ls = 0.4f)
                                            }
                                        }
                                    }
                                    val parts = listOfNotNull(pc.os.ifEmpty { null }, health.band, known?.let { usedLabel(it.lastUsed) })
                                    T(parts.joinToString(" · ").ifEmpty { pc.host }, 12, C.Muted, modifier = Modifier.padding(top = 3.dp), maxLines = 1)
                                }
                                Spacer(Modifier.width(12.dp))
                                Btn("Connect", primary = i == 0, height = 44, radius = 12, enabled = !busy) {
                                    engine.connect(PcTarget(listOf(pc.host), pc.port, pc.name, pc.os, pc.id))
                                }
                            }
                        }
                    }
                    Row(Modifier.padding(start = 20.dp, end = 20.dp, top = 12.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        Btn("Scan QR code", Modifier.weight(1f), size = 13, bg = C.Card, borderColor = C.Border) { tab = 2 }
                        Btn("Enter IP", Modifier.weight(1f), size = 13, bg = C.Card, borderColor = C.Border, enabled = !busy) { showIp = true }
                    }
                }

                1 -> {
                    CardBox(Modifier.padding(start = 20.dp, end = 20.dp, top = 16.dp).fillMaxWidth(), radius = 18) {
                        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                            T("Connect with USB", 15, C.Text, 600)
                            Step(1, "Turn on USB debugging (Developer options).")
                            Step(2, "Plug in the phone and tap Allow.")
                            Step(3, "Open CamStudio Pro on the computer.")
                            Btn("Connect over USB", Modifier.fillMaxWidth(), primary = true, enabled = !busy) {
                                engine.connect(PcTarget(listOf("127.0.0.1"), Proto.TCP_PORT, "USB computer", transport = "usb"))
                            }
                        }
                    }
                }

                else -> {
                    CardBox(Modifier.padding(start = 20.dp, end = 20.dp, top = 16.dp).fillMaxWidth(), radius = 18) {
                        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                            T("Scan the code on your computer", 15, C.Text, 600)
                            if (!camGranted) {
                                T("Camera access is needed to scan.", 12, C.Muted, lh = 18)
                                Btn("Allow camera", Modifier.fillMaxWidth(), primary = true, onClick = askPerms)
                            } else if (busy) {
                                T("Disconnect first to scan another code.", 12, C.Muted, lh = 18)
                            } else {
                                var lastBad by remember { mutableLongStateOf(0L) }
                                DisposableEffect(Unit) {
                                    engine.setQrScanner { text ->
                                        val target = parsePairUri(text)
                                        if (target != null) {
                                            engine.setQrScanner(null)
                                            engine.connect(target)
                                        } else if (System.currentTimeMillis() - lastBad > 3000) {
                                            lastBad = System.currentTimeMillis()
                                            engine.toast.tryEmit("That isn't a CamStudio Pro pairing code")
                                        }
                                    }
                                    onDispose { engine.setQrScanner(null) }
                                }
                                Box(Modifier.fillMaxWidth().aspectRatio(1f).clip(RoundedCornerShape(14.dp)).border(1.dp, C.Border2, RoundedCornerShape(14.dp))) {
                                    CameraPreview(engine, Modifier.fillMaxSize(), cover = true, gestures = false)
                                    Canvas(Modifier.fillMaxSize().padding(40.dp)) {
                                        val l = 26.dp.toPx()
                                        val w = 3.dp.toPx()
                                        val s = size
                                        fun corner(x: Float, y: Float, dx: Float, dy: Float) {
                                            drawLine(C.Accent, Offset(x, y), Offset(x + dx * l, y), w)
                                            drawLine(C.Accent, Offset(x, y), Offset(x, y + dy * l), w)
                                        }
                                        corner(0f, 0f, 1f, 1f); corner(s.width, 0f, -1f, 1f)
                                        corner(0f, s.height, 1f, -1f); corner(s.width, s.height, -1f, -1f)
                                    }
                                }
                                T("Scanning also trusts this phone.", 12, C.Muted, lh = 18)
                            }
                        }
                    }
                }
            }

            val url = stringResource(R.string.download_url)
            val ctx = LocalContext.current
            BasicText(
                buildAnnotatedString {
                    append("Get the desktop app: ")
                    withStyle(SpanStyle(color = C.AccentLink, textDecoration = TextDecoration.Underline)) { append(url) }
                },
                Modifier.padding(start = 20.dp, end = 20.dp, top = 20.dp, bottom = 16.dp).clickable(enabled = url.startsWith("http")) {
                    runCatching { ctx.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) }
                },
                style = TextStyle(color = C.Muted, fontSize = 12.sp, lineHeight = 18.sp, fontFamily = Inter),
            )
        }
        BottomNav(Screen.Connect, go)
    }

    if (showIp) {
        var ip by remember { mutableStateOf("") }
        Sheet("Enter IP address", onDismiss = { showIp = false }) {
            T("Shown in the desktop app under Pair a phone.", 13, C.Muted, lh = 19)
            Spacer(Modifier.height(12.dp))
            BasicTextField(
                value = ip,
                onValueChange = { v -> ip = v.filter { it.isDigit() || it == '.' || it == ':' }.take(21) },
                singleLine = true,
                textStyle = TextStyle(color = C.Text, fontSize = 16.sp, fontFamily = Mono, fontWeight = FontWeight.Medium),
                cursorBrush = SolidColor(C.Accent),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(C.Ground)
                    .border(1.dp, C.Outline, RoundedCornerShape(12.dp)).padding(horizontal = 14.dp, vertical = 14.dp),
                decorationBox = { inner ->
                    Box { if (ip.isEmpty()) T("192.168.1.5", 16, C.Placeholder, 500, mono = true); inner() }
                },
            )
            Spacer(Modifier.height(14.dp))
            val host = ip.substringBefore(':').trim()
            val port = ip.substringAfter(':', "").toIntOrNull() ?: Proto.TCP_PORT
            val valid = Regex("""\d{1,3}(\.\d{1,3}){3}""").matches(host)
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Btn("Cancel", Modifier.weight(1f)) { showIp = false }
                Btn("Connect", Modifier.weight(1f), primary = true, enabled = valid) {
                    showIp = false
                    engine.connect(PcTarget(listOf(host), port, host))
                }
            }
        }
    }

    if (showHelp) {
        Sheet("How it works", onDismiss = { showHelp = false }) {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Step(1, "Open CamStudio Pro on your Windows PC.")
                Step(2, "Connect by Wi‑Fi, USB or QR code.")
                Step(3, "Watch, record and edit on the computer.")
                T("Video stays on your local network.", 12, C.Muted, lh = 18)
                Btn("Got it", Modifier.fillMaxWidth(), primary = true) { showHelp = false }
            }
        }
    }
}

@Composable
private fun DeviceTile(ic: androidx.compose.ui.graphics.vector.ImageVector) {
    Box(Modifier.size(52.dp).clip(RoundedCornerShape(14.dp)).background(C.Chip).border(1.dp, C.Border2, RoundedCornerShape(14.dp)),
        contentAlignment = Alignment.Center) { I(ic, 24, C.Text) }
}

@Composable
fun Dot(size: Int, color: Color) {
    Box(Modifier.size(size.dp).clip(CircleShape).background(color))
}

@Composable
private fun Step(n: Int, text: String) {
    Row {
        Box(Modifier.size(22.dp).clip(CircleShape).background(C.Sel), contentAlignment = Alignment.Center) {
            T("$n", 11, C.Text, 600, mono = true)
        }
        Spacer(Modifier.width(10.dp))
        T(text, 13, C.Text2, lh = 19, modifier = Modifier.weight(1f))
    }
}
