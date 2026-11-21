package com.camstudio.pro.ui

import android.Manifest
import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.platform.LocalContext
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.compose.LifecycleResumeEffect
import com.camstudio.pro.ConnStatus
import com.camstudio.pro.Engine
import kotlinx.coroutines.delay

enum class Screen { Connect, Live, Pro, Effects, Settings, Health }

@Composable
fun AppRoot(engine: Engine) {
    val settings by engine.settings.collectAsState()
    val dark = when (settings.theme) {
        "light" -> false
        "dark" -> true
        else -> isSystemInDarkTheme()
    }
    val palette = if (dark) DarkPalette else LightPalette
    val scheme = if (dark) darkColorScheme(primary = C.Accent, background = palette.ground, surface = palette.card)
        else lightColorScheme(primary = C.Accent, background = palette.ground, surface = palette.card)
    CompositionLocalProvider(LocalPalette provides palette) { MaterialTheme(colorScheme = scheme) {
        var screen by rememberSaveable { mutableStateOf(Screen.Connect) }
        // Camera screens are always dark (controls sit on the video), so their bar icons stay light.
        val overVideo = screen == Screen.Live || screen == Screen.Pro
        SystemBars(lightIcons = dark || overVideo || screen == Screen.Effects, lightNavIcons = dark || overVideo)
        var permTick by remember { mutableIntStateOf(0) }
        val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
            permTick++
            engine.onPermissionsChanged()
        }
        LifecycleResumeEffect(Unit) {
            permTick++
            onPauseOrDispose { }
        }
        val camGranted = remember(permTick) { engine.hasCameraPermission() }
        val askPerms = {
            // Notifications show the "streaming / waiting for your computer" status in the background.
            val notify = if (android.os.Build.VERSION.SDK_INT >= 33) arrayOf(Manifest.permission.POST_NOTIFICATIONS) else emptyArray()
            launcher.launch(arrayOf(Manifest.permission.CAMERA, Manifest.permission.RECORD_AUDIO) + notify)
        }
        val conn by engine.conn.collectAsState()
        val toggles by engine.toggles.collectAsState()

        // Jump to the live view when a session starts.
        var wasLive by remember { mutableStateOf(false) }
        LaunchedEffect(conn.status) {
            val live = conn.status == ConnStatus.Live
            if (live && !wasLive && screen == Screen.Connect) screen = Screen.Live
            wasLive = live
        }

        BackHandler(enabled = screen != Screen.Connect) {
            screen = when (screen) {
                Screen.Pro, Screen.Health -> Screen.Live
                else -> Screen.Connect
            }
        }

        var toast by remember { mutableStateOf<String?>(null) }
        LaunchedEffect(Unit) {
            engine.toast.collect {
                toast = it
            }
        }
        LaunchedEffect(toast) {
            if (toast != null) {
                delay(2800)
                toast = null
            }
        }

        Box(
            Modifier.fillMaxSize().background(C.Ground)
                .pointerInput(Unit) {
                    awaitPointerEventScope {
                        while (true) {
                            awaitPointerEvent(androidx.compose.ui.input.pointer.PointerEventPass.Initial)
                            engine.userActivity()
                        }
                    }
                }
        ) {
            val go: (Screen) -> Unit = { screen = it }
            val needsCamera = screen == Screen.Live || screen == Screen.Pro || screen == Screen.Effects
            if (needsCamera && !camGranted) {
                PermissionScreen(onAllow = askPerms, onBack = { screen = Screen.Connect })
            } else {
                when (screen) {
                    Screen.Connect -> ConnectScreen(engine, go, camGranted, askPerms)
                    Screen.Live -> OverVideo { LiveScreen(engine, go) }
                    Screen.Pro -> OverVideo { ProScreen(engine, go) }
                    Screen.Effects -> EffectsScreen(engine, go)
                    Screen.Settings -> SettingsScreen(engine, go)
                    Screen.Health -> HealthScreen(engine, go)
                }
            }

            toast?.let { msg ->
                Box(
                    Modifier.align(Alignment.TopCenter).windowInsetsPadding(WindowInsets.statusBars)
                        .padding(top = 64.dp, start = 24.dp, end = 24.dp)
                        .shadow(8.dp, RoundedCornerShape(12.dp)).clip(RoundedCornerShape(12.dp)).background(C.Toast)
                        .border(1.dp, C.Border2, RoundedCornerShape(12.dp)).padding(horizontal = 14.dp, vertical = 10.dp)
                ) { T(msg, 13, C.Text, 500, align = TextAlign.Center, lh = 18) }
            }

            if (toggles.dimmed) {
                Box(
                    Modifier.fillMaxSize().background(Color.Black)
                        .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null) {
                            engine.setToggles { copy(dimmed = false) }
                        },
                    contentAlignment = Alignment.Center,
                ) {
                    T(if (conn.status == ConnStatus.Live) "Streaming · tap to wake" else "Tap to wake", 12, Color(0xFF3A3F49), 500)
                }
            }
        }
    } }
}

/** Status and navigation bar icon colours for the current theme and screen. */
@Composable
private fun SystemBars(lightIcons: Boolean, lightNavIcons: Boolean) {
    val activity = LocalContext.current as? ComponentActivity ?: return
    LaunchedEffect(lightIcons, lightNavIcons) {
        val t = android.graphics.Color.TRANSPARENT
        activity.enableEdgeToEdge(
            statusBarStyle = if (lightIcons) SystemBarStyle.dark(t) else SystemBarStyle.light(t, t),
            navigationBarStyle = if (lightNavIcons) SystemBarStyle.dark(t) else SystemBarStyle.light(t, t),
        )
    }
}

@Composable
fun PermissionScreen(onAllow: () -> Unit, onBack: () -> Unit) {
    Column(
        Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.systemBars).padding(24.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Box(
            Modifier.clip(RoundedCornerShape(18.dp)).background(C.Chip).border(1.dp, C.Border2, RoundedCornerShape(18.dp)).padding(18.dp)
        ) { I(Ic.Camera, 30, C.AccentText) }
        Spacer(Modifier.height(20.dp))
        T("Allow camera and mic", 22, C.Text, 700, ls = -0.4f, align = TextAlign.Center, lh = 28)
        Spacer(Modifier.height(8.dp))
        T("Needed to send video and sound to your computer. Nothing leaves your network.",
            14, C.Muted, align = TextAlign.Center, lh = 21)
        Spacer(Modifier.height(24.dp))
        Btn("Allow access", Modifier.fillMaxWidth(), primary = true, onClick = onAllow)
        Spacer(Modifier.height(10.dp))
        Btn("Not now", Modifier.fillMaxWidth(), onClick = onBack)
        Spacer(Modifier.height(14.dp))
        T("Not asked? Turn them on in Settings › Apps.", 12, C.Muted, align = TextAlign.Center, lh = 18)
    }
}

@Composable
fun BottomNav(current: Screen, go: (Screen) -> Unit, modifier: Modifier = Modifier) {
    Column(modifier.fillMaxWidth().background(C.Nav).windowInsetsPadding(WindowInsets.navigationBars)) {
        Hairline(C.Line)
        Row(Modifier.fillMaxWidth().height(66.dp).padding(horizontal = 8.dp)) {
            val items = listOf(
                Triple(Screen.Connect, "Connect", Ic.Link),
                Triple(Screen.Live, "Camera", Ic.Camera),
                Triple(Screen.Effects, "Effects", Ic.Sparkles),
                Triple(Screen.Settings, "Settings", Ic.Sliders),
            )
            for ((s, label, ic) in items) {
                val sel = s == current
                Column(
                    Modifier.weight(1f).fillMaxSize().clip(RoundedCornerShape(12.dp))
                        .clickable(role = Role.Tab) { go(s) },
                    verticalArrangement = Arrangement.Center,
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    I(ic, 22, if (sel) C.AccentText else C.Muted)
                    Spacer(Modifier.height(4.dp))
                    T(label, 11, if (sel) C.AccentText else C.Muted, if (sel) 600 else 500)
                }
            }
        }
    }
}

/** The GL preview. Holding this composable keeps the camera open. */
@Composable
fun CameraPreview(engine: Engine, modifier: Modifier, cover: Boolean, gestures: Boolean = true) {
    DisposableEffect(Unit) {
        engine.acquireCamera()
        onDispose { engine.releaseCamera() }
    }
    var size by remember { mutableStateOf(IntSize.Zero) }
    Box(
        modifier.background(Color.Black).onSizeChanged { size = it }
            .pointerInput(gestures) {
                if (gestures) detectTapGestures { pos ->
                    engine.pipeline.viewToBuffer(pos.x, pos.y, size.width, size.height, cover)?.let { engine.camera.focusAt(it) }
                }
            }
            .pointerInput(gestures) {
                if (gestures) detectTransformGestures { _, _, zoom, _ ->
                    if (zoom != 1f) engine.camera.setZoom(engine.camera.live.value.zoom * zoom)
                }
            }
    ) {
        AndroidView(
            modifier = Modifier.fillMaxSize(),
            factory = { ctx ->
                SurfaceView(ctx).apply {
                    holder.addCallback(object : SurfaceHolder.Callback {
                        override fun surfaceCreated(h: SurfaceHolder) {}
                        override fun surfaceChanged(h: SurfaceHolder, format: Int, w: Int, hh: Int) {
                            engine.pipeline.setPreviewSurface(h.surface, w, hh, cover)
                        }

                        override fun surfaceDestroyed(h: SurfaceHolder) {
                            engine.pipeline.clearPreviewSurface(h.surface)
                        }
                    })
                }
            },
        )
    }
}
