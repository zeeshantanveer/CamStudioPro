package com.camstudio.pro.ui

import android.content.Intent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.camstudio.pro.Engine
import com.camstudio.pro.data.Bg
import com.camstudio.pro.data.FxState
import com.camstudio.pro.data.LOOK_NAMES
import com.camstudio.pro.data.BeautyState
import com.camstudio.pro.data.BEAUTY_CONTROLS
import org.json.JSONObject

@Composable
fun EffectsScreen(engine: Engine, go: (Screen) -> Unit) {
    val fx by engine.fx.collectAsState()
    val beautyTracking by engine.pipeline.beautyTracking.collectAsState()
    val roomCapture by engine.roomCapture.collectAsState()
    var tab by rememberSaveable { mutableIntStateOf(0) }
    val ctx = LocalContext.current
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) {
            runCatching { ctx.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
            engine.setFx { copy(bg = Bg.UPLOAD, uploadUri = uri.toString()) }
        }
    }
    val videoPicker = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) {
            runCatching { ctx.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
            engine.setFx { copy(bg = Bg.VIDEO, videoUri = uri.toString()) }
        }
    }

    Box(Modifier.fillMaxSize().background(C.Ground)) {
        // Preview (always dark: the controls sit on the video)
        OverVideo { Box(Modifier.fillMaxWidth().height(350.dp)) {
            CameraPreview(engine, Modifier.fillMaxSize(), cover = true)
            Row(
                Modifier.fillMaxWidth().background(Brush.verticalGradient(listOf(Color(0x99000000), Color.Transparent)))
                    .windowInsetsPadding(WindowInsets.statusBars).padding(14.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                IconBtn(Ic.Back, "Back to camera") { go(Screen.Live) }
                Spacer(Modifier.weight(1f))
                // Hold to see the unprocessed image
                Box(
                    Modifier.height(44.dp).clip(RoundedCornerShape(12.dp)).background(C.Glass)
                        .pointerInput(Unit) {
                            detectTapGestures(onPress = {
                                engine.setToggles { copy(compare = true) }
                                tryAwaitRelease()
                                engine.setToggles { copy(compare = false) }
                            })
                        }
                        .semantics { contentDescription = "Compare: hold to see the original image" }
                        .padding(horizontal = 14.dp),
                    contentAlignment = Alignment.Center,
                ) { T("Compare", 13, C.Text, 600) }
                Spacer(Modifier.size(8.dp))
                Btn("Reset", height = 44, radius = 12, size = 13, bg = C.Glass, borderColor = Color.Transparent) {
                    engine.setFx { FxState() }
                    engine.setToggles { copy(mode = 0) }
                }
            }
            val n = fx.activeCount
            Box(Modifier.align(Alignment.BottomStart).padding(start = 14.dp, bottom = 28.dp).clip(RoundedCornerShape(6.dp))
                .background(Color(0xCC08090B)).padding(horizontal = 8.dp, vertical = 4.dp)) {
                T(if (n == 0) "NO EFFECTS" else "$n ACTIVE", 10, if (n == 0) C.Muted else C.Green, 600, mono = true)
            }
        } }

        // Sheet
        Column(
            Modifier.fillMaxSize().padding(top = 334.dp, bottom = 0.dp)
                .clip(RoundedCornerShape(topStart = 22.dp, topEnd = 22.dp)).background(C.Nav)
                .border(1.dp, C.Border, RoundedCornerShape(topStart = 22.dp, topEnd = 22.dp)),
        ) {
            Box(Modifier.padding(top = 10.dp).align(Alignment.CenterHorizontally).size(36.dp, 4.dp).clip(RoundedCornerShape(2.dp)).background(C.Outline))
            Column(
                Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(start = 16.dp, end = 16.dp, top = 12.dp, bottom = 16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                // Category tabs
                Row(
                    Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(C.Card)
                        .border(1.dp, C.Border, RoundedCornerShape(12.dp)).padding(4.dp),
                    horizontalArrangement = Arrangement.spacedBy(4.dp),
                ) {
                    listOf("Background", "Looks", "Beauty", "Adjust").forEachIndexed { i, label ->
                        val sel = i == tab
                        Box(
                            Modifier.weight(1f).height(36.dp).clip(RoundedCornerShape(8.dp)).background(if (sel) C.Sel else Color.Transparent)
                                .clickable(role = Role.Tab) { tab = i },
                            contentAlignment = Alignment.Center,
                        ) { T(label, 12, if (sel) C.Text else C.Muted, if (sel) 600 else 500, maxLines = 1) }
                    }
                }

                when (tab) {
                    0 -> {
                        roomCapture?.let { status ->
                            CardBox(Modifier.fillMaxWidth()) {
                                Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                                    Column(Modifier.weight(1f)) {
                                        T(status, 15, C.Text, 600)
                                        T("Keep the phone still and step out of view.",
                                            12, C.Muted, modifier = Modifier.padding(top = 4.dp), lh = 17)
                                    }
                                    Spacer(Modifier.size(12.dp))
                                    Btn("Cancel", height = 40, radius = 10, size = 12) { engine.cancelRoomCapture() }
                                }
                            }
                        }
                        val hasRoom = fx.roomVersion != 0L
                        val tiles: List<@Composable (Modifier) -> Unit> = listOf(
                            { m -> Tile("None", fx.bg == Bg.NONE, m, icon = Ic.None) { engine.setFx { copy(bg = Bg.NONE) }; engine.setToggles { copy(mode = 0) } } },
                            { m -> Tile("Blur", fx.bg == Bg.BLUR, m,
                                fill = Brush.radialGradient(listOf(Color(0xFF8A765C), Color(0xFF2B2724)))) { engine.setFx { copy(bg = Bg.BLUR) } } },
                            { m -> Tile("My room", fx.bg == Bg.ROOM, m, icon = Ic.Camera, dashed = !hasRoom) {
                                if (hasRoom && fx.bg != Bg.ROOM) engine.setFx { copy(bg = Bg.ROOM) } else engine.captureRoom()
                            } },
                            { m -> Tile("Photo", fx.bg == Bg.UPLOAD, m, icon = Ic.Plus, dashed = fx.uploadUri == null) {
                                if (fx.uploadUri != null && fx.bg != Bg.UPLOAD) engine.setFx { copy(bg = Bg.UPLOAD) }
                                else picker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
                            } },
                            { m -> Tile("Video", fx.bg == Bg.VIDEO, m, icon = Ic.Plus, dashed = fx.videoUri == null) {
                                if (fx.videoUri != null && fx.bg != Bg.VIDEO) engine.setFx { copy(bg = Bg.VIDEO) }
                                else videoPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.VideoOnly))
                            } },
                            { m -> Tile("Office", fx.bg == Bg.OFFICE, m,
                                fill = Brush.verticalGradient(0f to Color(0xFFDDE3EA), 0.55f to Color(0xFFDDE3EA), 0.55f to Color(0xFF8C7B66), 1f to Color(0xFF8C7B66))) {
                                engine.setFx { copy(bg = Bg.OFFICE) }
                            } },
                            { m -> Tile("Library", fx.bg == Bg.LIBRARY, m,
                                fill = Brush.horizontalGradient(listOf(Color(0xFF6B4F3A), Color(0xFF3E5A6B), Color(0xFF7A6A4A), Color(0xFF2A2420)))) {
                                engine.setFx { copy(bg = Bg.LIBRARY) }
                            } },
                        )
                        for (row in tiles.chunked(4)) {
                            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                row.forEach { tile -> tile(Modifier.weight(1f)) }
                                repeat(4 - row.size) { Spacer(Modifier.weight(1f)) }
                            }
                        }
                        val replaced = fx.bg != Bg.NONE && fx.bg != Bg.BLUR
                        when (fx.bg) {
                            Bg.ROOM -> Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                                T("Recapture after moving the phone or if the light changes.",
                                    12, C.Muted, modifier = Modifier.weight(1f), lh = 17)
                                Btn("Recapture", height = 40, radius = 10, size = 12, enabled = roomCapture == null) { engine.captureRoom() }
                            }
                            Bg.VIDEO -> Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                                T("Loops muted. A short, steady clip works best.",
                                    12, C.Muted, modifier = Modifier.weight(1f), lh = 17)
                                Btn("Change", height = 40, radius = 10, size = 12) {
                                    videoPicker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.VideoOnly))
                                }
                            }
                            Bg.UPLOAD -> Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                                T("A real room photo at eye height looks best.",
                                    12, C.Muted, modifier = Modifier.weight(1f), lh = 17)
                                Btn("Change", height = 40, radius = 10, size = 12) {
                                    picker.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
                                }
                            }
                            Bg.OFFICE, Bg.LIBRARY -> T("Photographic room. Match your face lighting to the scene for a natural result.",
                                12, C.Muted, lh = 17)
                            else -> {}
                        }
                        CardBox(Modifier.fillMaxWidth()) {
                            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                                if (fx.bg == Bg.BLUR) {
                                    LabeledSlider("Blur strength", fx.blurStrength, 0, 100) { v -> engine.setFx { copy(blurStrength = v) } }
                                }
                                LabeledSlider("Hair edge detail", fx.edge, 0, 100, enabled = fx.bg != Bg.NONE) { v -> engine.setFx { copy(edge = v) } }
                                LabeledSlider("Light wrap", fx.lightWrap, 0, 100, enabled = fx.bg != Bg.NONE) { v -> engine.setFx { copy(lightWrap = v) } }
                                LabeledSlider("Camera grain", fx.grain, 0, 100, enabled = fx.bg != Bg.NONE) { v -> engine.setFx { copy(grain = v) } }
                                LabeledSlider("Depth of field", fx.depth, 0, 100, enabled = replaced) { v -> engine.setFx { copy(depth = v) } }
                                LabeledSlider("Match room lighting", fx.matchLight, 0, 100, enabled = replaced) { v -> engine.setFx { copy(matchLight = v) } }
                            }
                        }
                        if (fx.bg != Bg.NONE) {
                            T("Tip: face the light and sit 1 m from the wall.", 12, C.Muted, lh = 17)
                        }
                    }
                    1 -> {
                        val swatches = listOf(
                            listOf(Color(0xFFB8A48E), Color(0xFF5E5146)),
                            listOf(Color(0xFFE8B070), Color(0xFF7A4A2A)),
                            listOf(Color(0xFF8FB4D8), Color(0xFF34506B)),
                            listOf(Color(0xFFD0D0D0), Color(0xFF404040)),
                            listOf(Color(0xFFFF7A59), Color(0xFF4AD0A0)),
                            listOf(Color(0xFFC9B48A), Color(0xFF3B4A44)),
                        )
                        for (row in LOOK_NAMES.indices.chunked(3)) {
                            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                row.forEach { i ->
                                    Tile(LOOK_NAMES[i], fx.look == i, Modifier.weight(1f), fill = Brush.linearGradient(swatches[i])) {
                                        engine.setFx { copy(look = i) }
                                    }
                                }
                            }
                        }
                    }
                    2 -> CardBox(Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                            SwitchRow("Beauty effects", "Synced with Windows", fx.beauty.enabled) { enabled ->
                                engine.setFx { copy(beauty = beauty.copy(enabled = enabled)) }
                            }
                            Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                            T(beautyTracking, 12, C.Muted, lh = 17)
                            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                Btn("Reset", height = 40, size = 12) { engine.setFx { copy(beauty = BeautyState()) } }
                                Box(Modifier.height(40.dp).clip(RoundedCornerShape(14.dp)).background(C.Chip)
                                    .border(1.dp, C.Outline, RoundedCornerShape(14.dp))
                                    .pointerInput(Unit) {
                                        detectTapGestures(onPress = {
                                            engine.setToggles { copy(beautyCompare = true) }
                                            try { tryAwaitRelease() } finally { engine.setToggles { copy(beautyCompare = false) } }
                                        })
                                    }.semantics { contentDescription = "Hold to compare without beauty effects" }
                                    .padding(horizontal = 14.dp), contentAlignment = Alignment.Center) { T("Hold original", 12, C.Text, 600) }
                            }
                            BEAUTY_CONTROLS.forEach { control ->
                                LabeledSlider(control.label, fx.beauty.values().getValue(control.key), control.min, control.max,
                                    enabled = fx.beauty.enabled) { value ->
                                    engine.setFx { copy(beauty = beauty.patch(JSONObject().put(control.key, value))) }
                                }
                            }
                            T("+ narrows, lengthens or lifts. Start small.", 12, C.Muted, lh = 17)
                            }
                        }
                    }
                    else -> CardBox(Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                            LabeledSlider("Brightness", fx.brightness, -100, 100) { v -> engine.setFx { copy(brightness = v) } }
                            LabeledSlider("Contrast", fx.contrast, -100, 100) { v -> engine.setFx { copy(contrast = v) } }
                            LabeledSlider("Saturation", fx.saturation, -100, 100) { v -> engine.setFx { copy(saturation = v) } }
                            LabeledSlider("Warmth", fx.warmth, -100, 100) { v -> engine.setFx { copy(warmth = v) } }
                            LabeledSlider("Sharpness", fx.sharpness, 0, 100) { v -> engine.setFx { copy(sharpness = v) } }
                        }
                    }
                }

                CardBox(Modifier.fillMaxWidth()) {
                    SwitchRow("Auto‑framing", "Keeps you centred", fx.autoFrame) { v -> engine.setFx { copy(autoFrame = v) } }
                    Hairline(C.Border)
                    SwitchRow("Low‑light boost", null, fx.lowLight) { v ->
                        engine.setFx { copy(lowLight = v) }
                        engine.setToggles { copy(mode = if (v) 3 else 0) }
                    }
                }
            }
            BottomNav(Screen.Effects, go)
        }
    }
}

@Composable
fun SwitchRow(title: String, subtitle: String?, checked: Boolean, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Row(
        Modifier.fillMaxWidth().heightIn(min = 56.dp).padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Column(Modifier.weight(1f)) {
            T(title, 14, if (enabled) C.Text else C.Muted, 500)
            if (subtitle != null) T(subtitle, 12, C.Muted, modifier = Modifier.padding(top = 2.dp), lh = 16, maxLines = 2)
        }
        Toggle(checked, title, enabled, onChange = onChange)
    }
}

@Composable
private fun Tile(
    label: String,
    selected: Boolean,
    modifier: Modifier,
    icon: androidx.compose.ui.graphics.vector.ImageVector? = null,
    fill: Brush? = null,
    dashed: Boolean = false,
    onClick: () -> Unit,
) {
    Column(
        modifier.clip(RoundedCornerShape(12.dp)).clickable(role = Role.Button, onClickLabel = label, onClick = onClick).padding(vertical = 2.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        val shape = RoundedCornerShape(14.dp)
        Box(
            Modifier.size(62.dp).clip(shape)
                .then(if (fill != null) Modifier.background(fill) else Modifier.background(C.Card))
                .border(if (selected) 2.dp else 1.dp, if (selected) C.Accent else if (dashed) C.Placeholder else C.Border2, shape),
            contentAlignment = Alignment.Center,
        ) {
            if (icon != null) I(icon, 22, if (selected) C.AccentText else C.Text2)
        }
        Spacer(Modifier.height(6.dp))
        T(label, 11, if (selected) C.Text else C.Muted, if (selected) 600 else 500, maxLines = 1)
    }
}
