package com.camstudio.pro.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.camstudio.pro.BuildConfig
import com.camstudio.pro.Engine
import com.camstudio.pro.R
import com.camstudio.pro.data.Preset
import com.camstudio.pro.data.THEMES
import com.camstudio.pro.data.resLongName

private enum class Picker { None, Preset, Resolution, Bitrate, Codec, Orientation }

private val BITRATES_KBPS = listOf(2500, 4000, 8000, 12000, 20000)

@Composable
fun SettingsScreen(engine: Engine, go: (Screen) -> Unit) {
    val s by engine.settings.collectAsState()
    val caps by engine.camera.caps.collectAsState()
    val has4k by engine.has4k.collectAsState()
    val userPresets by engine.userPresets.collectAsState()
    var picker by remember { mutableStateOf(Picker.None) }
    val ctx = LocalContext.current
    val micAllowed = remember { engine.hasMicPermission() }

    Column(Modifier.fillMaxSize().background(C.Ground)) {
        T("Settings", 26, C.Text, 700, Modifier.windowInsetsPadding(WindowInsets.statusBars).padding(start = 20.dp, top = 20.dp, bottom = 8.dp), ls = -0.5f)

        Column(
            Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(start = 16.dp, end = 16.dp, bottom = 16.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Group("Appearance", first = true)
            CardBox(Modifier.fillMaxWidth()) {
                Row(Modifier.fillMaxWidth().height(56.dp).padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                    T("Theme", 14, C.Text, modifier = Modifier.weight(1f))
                    Segmented(listOf("System", "Light", "Dark"), THEMES.indexOf(s.theme).coerceAtLeast(0)) { i ->
                        engine.setSettings { copy(theme = THEMES[i]) }
                    }
                }
            }

            Group("Preset")
            // Active preset
            CardBox(Modifier.fillMaxWidth()) {
                Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    Column(Modifier.weight(1f)) {
                        T(s.preset.ifEmpty { "Custom" }, 15, C.Text, 600)
                        val current = (Preset.BUILT_IN + userPresets).firstOrNull { it.name == s.preset }
                        val summary = current?.takeIf {
                            it.resolution == s.resolution && it.fps == s.fps && it.lowLatency == s.lowLatency
                        }?.summary ?: "${resLongName(s.resolution).substringBefore(' ')}${s.fps} · H.264 · " +
                            if (s.lowLatency) "low latency" else "high quality"
                        T(summary, 12, C.Muted, modifier = Modifier.padding(top = 4.dp), mono = true)
                    }
                    Btn("Change", height = 44, radius = 12, size = 13) { picker = Picker.Preset }
                }
            }

            Group("Video")
            CardBox(Modifier.fillMaxWidth()) {
                NavRow("Resolution", resLongName(s.resolution)) { picker = Picker.Resolution }
                Hairline()
                Row(Modifier.fillMaxWidth().height(56.dp).padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                    T("Frame rate", 14, C.Text, modifier = Modifier.weight(1f))
                    val opts = listOf(24, 30, 60)
                    Segmented(opts.map { it.toString() }, opts.indexOf(s.fps), mono = true,
                        enabled = { !caps.ready || opts[it] in caps.fpsChoices }) { i ->
                        engine.setSettings { copy(fps = opts[i], preset = "") }
                    }
                }
                Hairline()
                NavRow("Bitrate", if (s.adaptive) "Auto · ${mbps(s.bitrateKbps)}" else mbps(s.bitrateKbps)) { picker = Picker.Bitrate }
                Hairline()
                NavRow("Codec", "H.264") { picker = Picker.Codec }
                Hairline()
                NavRow("Orientation", when (s.orientation) {
                    "portrait" -> "Portrait"
                    "auto" -> "Auto"
                    else -> "Landscape"
                }) { picker = Picker.Orientation }
            }

            Group("Connection")
            CardBox(Modifier.fillMaxWidth()) {
                SwitchRow("Auto‑connect", "Reconnect to the last computer", s.autoConnect) { v -> engine.setSettings { copy(autoConnect = v) } }
            }

            Group("Audio")
            CardBox(Modifier.fillMaxWidth()) {
                SwitchRow("Send microphone",
                    if (micAllowed) null else "Microphone permission is off",
                    s.micEnabled && micAllowed, enabled = micAllowed) { v -> engine.setSettings { copy(micEnabled = v) } }
                Hairline()
                SwitchRow("Noise suppression", null, s.noiseSuppression, enabled = s.micEnabled) { v -> engine.setSettings { copy(noiseSuppression = v) } }
            }

            Group("Performance")
            CardBox(Modifier.fillMaxWidth()) {
                SwitchRow("Low latency", "Best for calls", s.lowLatency) { v ->
                    engine.setSettings { copy(lowLatency = v, preset = "") }
                }
                Hairline()
                SwitchRow("Screen off while streaming", "Saves battery", s.screenOff) { v -> engine.setSettings { copy(screenOff = v) } }
                Hairline()
                SwitchRow("Overheat protection", "Lowers frame rate when hot", s.overheatProtect) { v ->
                    engine.setSettings { copy(overheatProtect = v) }
                }
            }

            Group("About")
            CardBox(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    T("CamStudio Pro ${BuildConfig.VERSION_NAME}", 14, C.Text, 500)
                    T(stringResource(R.string.download_url), 12, C.Muted, lh = 17)
                }
            }
        }
        BottomNav(Screen.Settings, go)
    }

    when (picker) {
        Picker.None -> {}
        Picker.Preset -> Sheet("Presets", { picker = Picker.None }) {
            Column(Modifier.heightIn(max = 420.dp).verticalScroll(rememberScrollState())) {
                (Preset.BUILT_IN + userPresets).forEach { p ->
                    OptionRow(p.name, p.summary + if (p.pro != null) " · pro camera" else "", selected = p.name == s.preset,
                        trailing = if (!p.builtIn) ({
                            IconBtn(Ic.Trash, "Delete preset ${p.name}", bg = androidx.compose.ui.graphics.Color.Transparent,
                                tint = C.Muted, size = 40) { engine.deleteUserPreset(p.name) }
                        }) else null) {
                        engine.applyPreset(p)
                        picker = Picker.None
                    }
                }
            }
            Spacer(Modifier.height(8.dp))
            T("Save your own in Camera › Pro.", 12, C.Muted, lh = 17)
        }
        Picker.Resolution -> Sheet("Resolution", { picker = Picker.None }) {
            listOf(720, 1080, 2160).forEach { r ->
                val ok = r != 2160 || has4k
                OptionRow(resLongName(r), if (!ok) "This phone's encoder can't do 4K" else null, selected = s.resolution == r, enabled = ok) {
                    engine.setSettings { copy(resolution = r, preset = "") }
                    picker = Picker.None
                }
            }
        }
        Picker.Bitrate -> Sheet("Bitrate", { picker = Picker.None }) {
            T("Auto lowers quality on busy Wi‑Fi instead of stuttering.", 12, C.Muted, lh = 17)
            Spacer(Modifier.height(8.dp))
            Column(Modifier.heightIn(max = 440.dp).verticalScroll(rememberScrollState())) {
                BITRATES_KBPS.forEach { b ->
                    OptionRow("Auto, up to ${mbps(b)}", null, selected = s.adaptive && s.bitrateKbps == b) {
                        engine.setSettings { copy(adaptive = true, bitrateKbps = b, preset = "") }
                        picker = Picker.None
                    }
                }
                BITRATES_KBPS.forEach { b ->
                    OptionRow("Fixed ${mbps(b)}", null, selected = !s.adaptive && s.bitrateKbps == b) {
                        engine.setSettings { copy(adaptive = false, bitrateKbps = b, preset = "") }
                        picker = Picker.None
                    }
                }
            }
        }
        Picker.Codec -> Sheet("Codec", { picker = Picker.None }) {
            OptionRow("H.264", "Works with every PC", selected = true) { picker = Picker.None }
            OptionRow("HEVC (H.265)", "Needs a newer desktop app", selected = false, enabled = false) {}
        }
        Picker.Orientation -> Sheet("Orientation", { picker = Picker.None }) {
            listOf("landscape" to "Landscape 16:9", "portrait" to "Portrait 9:16", "auto" to "Auto (follow phone)").forEach { (k, label) ->
                OptionRow(label, if (k == "auto") "Switches when you turn the phone" else null, selected = s.orientation == k) {
                    engine.setSettings { copy(orientation = k) }
                    picker = Picker.None
                }
            }
        }
    }
}

private fun mbps(kbps: Int): String = if (kbps % 1000 == 0) "${kbps / 1000} Mbps" else "%.1f Mbps".format(kbps / 1000f)

@Composable
private fun Group(text: String, first: Boolean = false) {
    SectionLabel(text, modifier = Modifier.padding(start = 4.dp, top = if (first) 4.dp else 12.dp))
}

@Composable
private fun NavRow(title: String, value: String, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().height(56.dp).clickable(role = Role.Button, onClick = onClick).padding(horizontal = 16.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        T(title, 14, C.Text, modifier = Modifier.weight(1f))
        T(value, 14, C.Muted, maxLines = 1)
        I(Ic.Chevron, 16, C.Muted)
    }
}
