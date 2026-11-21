package com.camstudio.pro.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
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
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.camstudio.pro.ConnStatus
import com.camstudio.pro.Engine

@Composable
fun HealthScreen(engine: Engine, go: (Screen) -> Unit) {
    val h by engine.health.collectAsState()
    val conn by engine.conn.collectAsState()
    val ctx = LocalContext.current
    val isLive = conn.status == ConnStatus.Live

    Column(Modifier.fillMaxSize().background(C.Ground).windowInsetsPadding(WindowInsets.statusBars)) {
        Row(Modifier.padding(start = 16.dp, end = 16.dp, top = 14.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            IconBtn(Ic.Back, "Back to camera", bg = C.Card, border = C.Border) { go(Screen.Live) }
            T("Stream health", 20, C.Text, 700, Modifier.weight(1f), ls = -0.3f)
            if (isLive) Row(Modifier.height(28.dp).clip(RoundedCornerShape(7.dp)).background(C.LiveRed).padding(horizontal = 9.dp),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Dot(6, Color.White)
                T("LIVE", 11, Color.White, 600, mono = true)
            }
        }

        Column(
            Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            // Score
            CardBox(Modifier.fillMaxWidth(), radius = 18) {
                Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                    val score = if (isLive) h.score else null
                    val color = when {
                        score == null -> C.Muted
                        score >= 80 -> C.Green
                        score >= 55 -> C.AccentText
                        else -> C.Red
                    }
                    val ring = C.Track
                    Box(Modifier.size(72.dp).semantics { contentDescription = "Quality score ${score ?: "unknown"} out of 100" },
                        contentAlignment = Alignment.Center) {
                        Canvas(Modifier.fillMaxSize()) {
                            val sw = 6.dp.toPx()
                            val inset = sw / 2
                            val arc = Size(size.width - sw, size.height - sw)
                            drawArc(ring, 0f, 360f, false, Offset(inset, inset), arc, style = Stroke(sw))
                            if (score != null) drawArc(color, -90f, 360f * score / 100f, false, Offset(inset, inset), arc,
                                style = Stroke(sw, cap = StrokeCap.Round))
                        }
                        T(score?.toString() ?: "—", 20, C.Text, 600, mono = true)
                    }
                    Column(Modifier.weight(1f)) {
                        T(when {
                            !isLive -> "Not streaming"
                            score == null -> "Measuring…"
                            score >= 80 -> "Excellent"
                            score >= 55 -> "Fair"
                            else -> "Poor"
                        }, 17, C.Text, 700, ls = -0.2f)
                        val via = if (conn.transport == "usb") "USB" else listOfNotNull("Wi‑Fi", h.band).joinToString(" ")
                        val mins = ((System.currentTimeMillis() - conn.liveSince) / 60000).coerceAtLeast(0)
                        T(if (isLive) "${conn.pcName} · $via · $mins min" else "Not connected", 13, C.Muted,
                            modifier = Modifier.padding(top = 4.dp), lh = 19, maxLines = 1)
                        if (h.throttled) T("Hot: frame rate lowered", 12, C.AccentText, 500, Modifier.padding(top = 4.dp))
                    }
                }
            }

            // Tiles
            CardBox(Modifier.fillMaxWidth(), radius = 18) {
                Row {
                    Tile("Delay", h.delayMs?.toString() ?: "—", " ms", Modifier.weight(1f))
                    Box(Modifier.width(1.dp).height(78.dp).background(C.Border))
                    Tile("Frame rate", (h.recvFps ?: if (isLive) h.sentFps else null)?.toString() ?: "—", " / ${h.targetFps} fps", Modifier.weight(1f))
                }
                Hairline(C.Border)
                Row {
                    Tile("Bitrate", if (isLive && h.bitrateBps > 0) "%.1f".format(h.bitrateBps / 1e6) else "—", " Mbps", Modifier.weight(1f))
                    Box(Modifier.width(1.dp).height(78.dp).background(C.Border))
                    Tile("Dropped frames", h.dropPct?.let { "%.1f".format(it) } ?: "—", " %", Modifier.weight(1f))
                }
            }

            // Delay chart
            CardBox(Modifier.fillMaxWidth(), radius = 18) {
                Column(Modifier.padding(14.dp)) {
                    val hist = h.delayHistory
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.Bottom) {
                        T("Delay · 5 min", 14, C.Text, 600, Modifier.weight(1f))
                        T(if (hist.isEmpty()) "—" else "avg ${hist.average().toInt()} ms", 11, C.Muted, mono = true)
                    }
                    Spacer(Modifier.height(12.dp))
                    val accent = C.Accent
                    val grid = C.Track
                    Canvas(Modifier.fillMaxWidth().height(80.dp).semantics { contentDescription = "Delay chart" }) {
                        val dash = PathEffect.dashPathEffect(floatArrayOf(3.dp.toPx(), 4.dp.toPx()))
                        listOf(0.33f, 0.68f).forEach { f ->
                            drawLine(grid, Offset(0f, size.height * f), Offset(size.width, size.height * f), 1.dp.toPx(), pathEffect = dash)
                        }
                        if (hist.size >= 2) {
                            val hi = (hist.maxOrNull() ?: 1).coerceAtLeast(40) * 1.15f
                            val step = size.width / 299f
                            val x0 = size.width - (hist.size - 1) * step
                            val p = Path()
                            hist.forEachIndexed { i, v ->
                                val x = x0 + i * step
                                val y = size.height * (1f - v / hi)
                                if (i == 0) p.moveTo(x, y) else p.lineTo(x, y)
                            }
                            drawPath(p, accent, style = Stroke(2.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round))
                            drawCircle(accent, 3.5.dp.toPx(), Offset(size.width, size.height * (1f - hist.last() / hi)))
                        }
                    }
                    Row(Modifier.fillMaxWidth().padding(top = 4.dp)) {
                        T("-5:00", 10, C.Muted, mono = true, modifier = Modifier.weight(1f))
                        T("now", 10, C.Muted, mono = true)
                    }
                }
            }

            // Device rows
            CardBox(Modifier.fillMaxWidth(), radius = 18) {
                InfoRow("Battery", h.battery?.let { "$it%" + if (h.charging) " · charging" else "" } ?: "—")
                Hairline()
                val t = h.tempC
                InfoRow("Temperature", t?.let { "%.0f°C · %s".format(it, if (it >= 42) "hot" else if (it >= 37) "warm" else "normal") } ?: "—",
                    if (t != null && t >= 37) C.AccentText else C.Text2)
                Hairline()
                InfoRow("Wi‑Fi signal", h.rssi?.let { "$it dBm · " + when {
                    it >= -55 -> "strong"
                    it >= -67 -> "good"
                    it >= -75 -> "fair"
                    else -> "weak"
                } } ?: if (conn.transport == "usb" && isLive) "USB" else "—")
            }
        }

        Row(Modifier.windowInsetsPadding(WindowInsets.navigationBars).padding(start = 16.dp, end = 16.dp, bottom = 20.dp, top = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Btn("Copy details", Modifier.weight(1f), height = 50) {
                val cm = ctx.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                cm.setPrimaryClip(ClipData.newPlainText("CamStudio diagnostics", engine.diagnostics()))
                engine.toast.tryEmit("Diagnostics copied")
            }
            Btn("Disconnect", Modifier.weight(1f), height = 50, enabled = conn.status != ConnStatus.Idle,
                bg = C.DangerBg, fg = C.DangerText, borderColor = C.DangerLine) {
                engine.disconnect()
                go(Screen.Connect)
            }
        }
    }
}

@Composable
private fun Tile(label: String, value: String, unit: String, modifier: Modifier) {
    Column(modifier.padding(16.dp)) {
        SectionLabel(label)
        Row(Modifier.padding(top = 6.dp), verticalAlignment = Alignment.Bottom) {
            T(value, 24, C.Text, 600, mono = true)
            T(unit, 12, C.Muted, mono = true, modifier = Modifier.padding(bottom = 3.dp))
        }
    }
}

@Composable
private fun InfoRow(label: String, value: String, color: Color = C.Text2, mono: Boolean = true) {
    Row(Modifier.fillMaxWidth().height(48.dp).padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
        T(label, 14, C.Text, modifier = Modifier.weight(1f))
        T(value, if (mono) 12 else 13, color, mono = mono, maxLines = 1, align = TextAlign.End)
    }
}
