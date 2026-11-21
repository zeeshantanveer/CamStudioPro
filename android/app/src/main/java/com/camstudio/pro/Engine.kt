package com.camstudio.pro

import android.Manifest
import android.app.Application
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.MediaStore
import android.provider.Settings
import android.util.Log
import android.view.OrientationEventListener
import androidx.core.content.ContextCompat
import com.camstudio.pro.data.AppSettings
import com.camstudio.pro.data.VideoOptions
import com.camstudio.pro.data.Bg
import com.camstudio.pro.data.FxState
import com.camstudio.pro.data.Preset
import com.camstudio.pro.data.ProState
import com.camstudio.pro.data.SavedPc
import com.camstudio.pro.data.Store
import com.camstudio.pro.media.AudioCapture
import com.camstudio.pro.media.Backdrops
import com.camstudio.pro.media.CameraController
import com.camstudio.pro.media.GlPipeline
import com.camstudio.pro.media.RenderParams
import com.camstudio.pro.media.VideoEncoder
import com.camstudio.pro.net.DiscoveredPc
import com.camstudio.pro.net.Discovery
import com.camstudio.pro.net.HandshakeException
import com.camstudio.pro.net.PcTarget
import com.camstudio.pro.net.Proto
import com.camstudio.pro.net.Session
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONArray
import org.json.JSONObject
import kotlin.math.abs
import kotlin.math.roundToInt

enum class ConnStatus { Idle, Connecting, Waiting, Live, Reconnecting }

data class ConnState(
    val status: ConnStatus = ConnStatus.Idle,
    val pcName: String = "",
    val pcOs: String = "",
    val transport: String = "wifi",
    val liveSince: Long = 0L,
    val error: String? = null,
)

/** Everything on the Health screen. Null means "not known". */
data class Health(
    val rttMs: Int? = null,
    val encodeMs: Int? = null,
    val pipelineMs: Int? = null,
    val delayMs: Int? = null,
    val sentFps: Int = 0,
    val recvFps: Int? = null,
    val targetFps: Int = 30,
    val bitrateBps: Long = 0,
    val targetBitrateBps: Int = 0,
    val droppedFrames: Long? = null,
    val totalFrames: Long? = null,
    val localDrops: Long = 0,
    val inUseBy: List<String> = emptyList(),
    val vcamInstalled: Boolean? = null,
    val delayHistory: List<Int> = emptyList(),   // one sample per second, up to 5 minutes
    val battery: Int? = null,
    val charging: Boolean = false,
    val tempC: Float? = null,
    val rssi: Int? = null,
    val band: String? = null,
    val throttled: Boolean = false,
) {
    val dropPct: Float?
        get() {
            val t = totalFrames ?: return null
            val d = droppedFrames ?: return null
            return if (t > 0) d * 100f / t else 0f
        }

    val score: Int?
        get() {
            val delay = delayMs ?: return null
            var s = 100f
            if (delay > 60) s -= ((delay - 60) / 4f).coerceAtMost(40f)
            val rf = recvFps ?: sentFps
            if (targetFps > 0) s -= ((1f - rf.toFloat() / targetFps).coerceIn(0f, 1f)) * 40f
            s -= ((dropPct ?: 0f) * 5f).coerceAtMost(20f)
            return s.roundToInt().coerceIn(0, 100)
        }
}

data class Toggles(
    val grid: Boolean = false,
    val mirror: Boolean = false,
    val manualRot: Int = 0,
    val mode: Int = 0,          // 0 AUTO, 1 PRO, 2 PORTRAIT, 3 LOW LIGHT
    val compare: Boolean = false,
    val beautyCompare: Boolean = false,
    val dimmed: Boolean = false,
    val front: Boolean = true,
    val muted: Boolean = false,
    val paused: Boolean = false,
)

class Engine(private val app: Application) : Session.Listener {
    companion object {
        private const val TAG = "Engine"
    }

    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val mainHandler = Handler(Looper.getMainLooper())
    private val store = Store(app)
    private val snapshot = store.loadBlocking()
    val deviceId: String = snapshot.deviceId

    val settings = MutableStateFlow(snapshot.settings)
    val fx = MutableStateFlow(snapshot.fx)
    val pro = MutableStateFlow(snapshot.pro)
    val toggles = MutableStateFlow(Toggles(front = !snapshot.settings.rearCamera))
    val savedPcs = MutableStateFlow(snapshot.pcs)
    val userPresets = MutableStateFlow(snapshot.presets)
    val discovered = MutableStateFlow<List<DiscoveredPc>>(emptyList())
    val conn = MutableStateFlow(ConnState())
    val health = MutableStateFlow(Health())
    val micLevel = MutableStateFlow(0f)
    val has4k = MutableStateFlow(false)
    val toast = MutableSharedFlow<String>(extraBufferCapacity = 8)

    /** Name of the computer auto-connect is currently waiting for; null when it isn't running. */
    val autoTarget = MutableStateFlow<String?>(null)
    /** Set when the user stops a connection; auto-connect then waits until they connect or resume. */
    val autoPaused = MutableStateFlow(false)

    /** Status text while "Capture my room" runs (countdown, capturing); null when idle. */
    val roomCapture = MutableStateFlow<String?>(null)

    val pipeline = GlPipeline()
    val cameraEnabled = MutableStateFlow(false)
    val camera = CameraController(app, pipeline)

    private val discovery = Discovery { list -> discovered.value = list }
    private var session: Session? = null
    private var encoder: VideoEncoder? = null
    private var audio: AudioCapture? = null
    private var lastTarget: PcTarget? = null
    private var connectJob: Job? = null
    private var userStopped = false

    private var activityStarted = false
    private var cameraUsers = 0
    private var faceUsers = 0
    private var histUsers = 0
    private var qrCallback: ((String) -> Unit)? = null
    private var cameraRunning = false
    private var boundKey = ""
    private var deviceRot = 0
    private var lastTouch = System.currentTimeMillis()

    // Rate counters
    private var lastFramesOut = 0L
    private var lastBytes = 0L
    @Volatile private var congestionAt = 0L
    private var currentBitrate = 0
    private var lastGoodAt = 0L
    private var rtt: Int? = null
    private var pcStats: JSONObject? = null
    private var tickCount = 0
    private var videoOptionsAck = ""
    private var videoOptionsError = ""
    private var beautyAck = ""
    private var wifiLock: WifiManager.WifiLock? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var autoJob: Job? = null
    private var lastFailReason: String? = null
    private var discoveryUsers = 0
    private var serviceRunning = false
    private var fxSaveJob: Job? = null
    private var beautySendJob: Job? = null
    private var beautySentAt = 0L
    private var roomJob: Job? = null
    private var bgLoadGen = 0
    private var isoForGrain: Int? = null
    private val audioDelay = Handler(android.os.HandlerThread("csp-audio-delay").apply { start() }.looper)

    private val orientation = object : OrientationEventListener(app) {
        override fun onOrientationChanged(o: Int) {
            if (o == ORIENTATION_UNKNOWN) return
            val snapped = ((o + 45) / 90 * 90) % 360
            // Hysteresis: only switch when clearly inside the new quadrant.
            val dist = abs(((o - snapped + 540) % 360) - 180)
            if (snapped != deviceRot && dist < 30) {
                deviceRot = snapped
                refresh()
            }
        }
    }

    init {
        scope.launch(Dispatchers.Default) { has4k.value = VideoEncoder.supports(3840, 2160, 30) }
        scope.launch { tickLoop() }
        refresh()
        loadBackground(fx.value)
        // Keep the foreground service and its notification in step with the connection.
        scope.launch {
            combine(conn, autoTarget) { c, a -> Triple(c.status, c.pcName, a) }
                .distinctUntilChanged()
                .collect { syncService() }
        }
        maybeStartAuto()
    }

    // ------------------------------------------------------------ permissions

    fun hasCameraPermission() =
        ContextCompat.checkSelfPermission(app, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED

    fun hasMicPermission() =
        ContextCompat.checkSelfPermission(app, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    // ------------------------------------------------------------ lifecycle / camera ownership

    fun onActivityStart() {
        activityStarted = true
        updateCamera()
        maybeStartAuto()
        syncService()
    }

    fun onActivityStop() {
        flushFx()
        activityStarted = false
        updateCamera()
    }

    fun acquireCamera() {
        cameraUsers++
        updateCamera()
    }

    fun releaseCamera() {
        cameraUsers = (cameraUsers - 1).coerceAtLeast(0)
        // Let the next screen claim the camera before deciding to close it.
        mainHandler.postDelayed({ updateCamera() }, 250)
    }

    fun acquireFace() { faceUsers++; refresh() }
    fun releaseFace() { faceUsers = (faceUsers - 1).coerceAtLeast(0); refresh() }
    fun acquireHistogram() { histUsers++; refresh() }
    fun releaseHistogram() { histUsers = (histUsers - 1).coerceAtLeast(0); refresh() }

    fun setQrScanner(cb: ((String) -> Unit)?) {
        qrCallback = cb
        updateCamera()
    }

    fun onPermissionsChanged() {
        updateCamera()
        maybeStartAuto()
        syncService()
        if (conn.value.status == ConnStatus.Live) startAudio()
    }

    fun userActivity() {
        lastTouch = System.currentTimeMillis()
    }

    private val isLive get() = conn.value.status == ConnStatus.Live

    private fun captureSize(): Pair<Int, Int> = when (settings.value.resolution) {
        720 -> 1280 to 720
        2160 -> 3840 to 2160
        else -> 1920 to 1080
    }

    private fun updateCamera() {
        val want = CapturePolicy.wantsCamera(hasCameraPermission(), cameraEnabled.value,
            activityStarted, cameraUsers, isLive, qrCallback != null)
        pipeline.setCaptureActive(want)
        val t = toggles.value
        val s = settings.value
        val (lw, sh) = captureSize()
        // While scanning a QR code always use the rear camera.
        val front = if (qrCallback != null) false else t.front
        val captureFps = if (health.value.throttled) minOf(15, s.fps) else if (fx.value.beauty.enabled) minOf(30, s.fps) else s.fps
        val key = "$front/$lw/$sh/$captureFps/${qrCallback != null}"
        if (want && (!cameraRunning || key != boundKey)) {
            cameraRunning = true
            boundKey = key
            loadBackground(fx.value)
            camera.start(front, lw, sh, captureFps, qrCallback)
            orientation.enable()
        } else if (!want && cameraRunning) {
            cameraRunning = false
            boundKey = ""
            camera.stop()
            orientation.disable()
            bgLoadGen++
            pipeline.setBackgroundVideo(app, null, 1f, null)
        }
        refresh()
    }

    // ------------------------------------------------------------ state setters

    /** Connection and capture are independent. Every new connection starts in standby. */
    fun setCameraEnabled(enabled: Boolean, requestId: String = "") {
        var error = if (enabled && !hasCameraPermission()) "Allow camera permission on the phone first." else ""
        val want = enabled && error.isEmpty()
        cameraEnabled.value = want
        if (want) {
            loadBackground(fx.value)
            updateCamera()
            session?.takeIf { isLive && encoder == null }?.let { startCapture(it) }
            if (isLive && encoder == null) {
                error = "The phone could not start video encoding. Try a lower quality."
                teardownStream()
            }
        } else {
            teardownStream()
            bgLoadGen++
            pipeline.setBackgroundVideo(app, null, 1f, null)
            pipeline.cancelRoomCapture()
            roomJob?.cancel()
            setToggles { copy(dimmed = false, paused = false) }
            updateCamera()
        }
        syncService()
        updateWakeLock()
        Log.i(TAG, "camera enabled=${cameraEnabled.value}, capture=$cameraRunning, encoder=${encoder != null}, mic=${audio != null}, wifiLock=${wifiLock?.isHeld == true}, wakeLock=${wakeLock?.isHeld == true}")
        session?.sendJson(Proto.CONTROL, JSONObject().put("cmd", "cameraState")
            .put("cameraControlVersion", 1).put("enabled", cameraEnabled.value)
            .put("requestId", requestId).put("error", error))
    }

    fun setSettings(f: AppSettings.() -> AppSettings) {
        val old = settings.value
        val new = old.f()
        if (new == old) return
        settings.value = new
        scope.launch { store.saveSettings(new) }
        if (new.resolution != old.resolution || new.fps != old.fps) updateCamera()
        if (new.autoConnect != old.autoConnect) {
            if (new.autoConnect) resumeAuto() else stopAuto()
        }
        if (isLive) {
            if (new.resolution != old.resolution || new.fps != old.fps || new.orientation != old.orientation ||
                new.lowLatency != old.lowLatency) restartEncoder()
            if (new.micEnabled != old.micEnabled || new.noiseSuppression != old.noiseSuppression) startAudio()
            if (new.bitrateKbps != old.bitrateKbps || new.adaptive != old.adaptive) {
                currentBitrate = bitrateCap()
                encoder?.updateBitrate(currentBitrate)
            }
        }
        refresh()
    }

    fun setFx(requestId: String? = null, f: FxState.() -> FxState) {
        val old = fx.value
        val new = old.f()
        if (new == old) {
            if (requestId != null) { beautyAck = requestId; sendBeautyState() }
            return
        }
        fx.value = new
        if (new.beauty.enabled != old.beauty.enabled) updateCamera()
        saveFxSoon()
        if (new.bg != old.bg || new.uploadUri != old.uploadUri || new.videoUri != old.videoUri ||
            new.roomVersion != old.roomVersion) loadBackground(new)
        refresh()
        if (new.beauty != old.beauty) {
            beautyAck = requestId ?: ""
            // The PC waits for its acknowledgement, so answer its edits at once; the phone's own
            // slider drags are reported at most every 100 ms (the picture itself updates per frame).
            if (requestId != null) sendBeautyState() else sendBeautyStateSoon()
        }
    }

    fun setPro(f: ProState.() -> ProState) {
        val old = pro.value
        val new = old.f()
        if (new == old) return
        pro.value = new
        scope.launch { store.savePro(new) }
        camera.applyPro(new)
        refresh()
    }

    fun setToggles(f: Toggles.() -> Toggles) {
        val old = toggles.value
        val new = old.f()
        if (new == old) return
        toggles.value = new
        if (new.front != old.front) {
            camera.setTorch(false)
            updateCamera()
            if (settings.value.rearCamera == new.front) setSettings { copy(rearCamera = !new.front) } // reopen on this camera
        }
        if (new.muted != old.muted) audio?.muted = new.muted
        if (new.paused != old.paused) session?.sendControl(if (new.paused) "pause" else "resume")
        refresh()
    }

    fun setMode(mode: Int) {
        when (mode) {
            0 -> {
                setPro { ProState(peaking = peaking, zebra = zebra, stabilization = stabilization, antiFlicker = antiFlicker) }
                setFx { copy(bg = if (bg == Bg.BLUR) Bg.NONE else bg, lowLight = false) }
            }
            2 -> setFx { copy(bg = Bg.BLUR, lowLight = false) }
            3 -> setFx { copy(lowLight = true, bg = if (bg == Bg.BLUR) Bg.NONE else bg) }
        }
        setToggles { copy(mode = mode) }
    }

    fun applyPreset(p: Preset) {
        val fpsOk = camera.caps.value.fpsChoices
        val res = if (p.resolution == 2160 && !has4k.value) 1080 else p.resolution
        setSettings {
            copy(resolution = res, fps = if (camera.caps.value.ready && p.fps !in fpsOk) 30 else p.fps,
                bitrateKbps = p.bitrateKbps, adaptive = p.adaptive, lowLatency = p.lowLatency, preset = p.name)
        }
        p.pro?.let { saved -> setPro { saved.copy(locked = false) } }
    }

    fun saveUserPreset(name: String) {
        val s = settings.value
        val p = Preset(name, false, s.resolution, s.fps, s.bitrateKbps, s.adaptive, s.lowLatency, pro.value.copy(locked = false))
        val list = userPresets.value.filter { it.name != name } + p
        userPresets.value = list
        scope.launch { store.savePresets(list) }
        setSettings { copy(preset = name) }
    }

    fun deleteUserPreset(name: String) {
        val list = userPresets.value.filter { it.name != name }
        userPresets.value = list
        scope.launch { store.savePresets(list) }
    }

    private fun loadBackground(f: FxState) {
        if (!cameraEnabled.value || !cameraRunning) {
            bgLoadGen++
            pipeline.setBackgroundVideo(app, null, 1f, null)
            return
        }
        val gen = ++bgLoadGen
        scope.launch(Dispatchers.Default) {
            if (f.bg == Bg.VIDEO) {
                val uri = f.videoUri?.let { Uri.parse(it) }
                val info = uri?.let { Backdrops.videoInfo(app, it) }
                if (gen != bgLoadGen) return@launch
                if (uri == null || info == null) {
                    toast.tryEmit("Couldn't play that video")
                    pipeline.setBackgroundVideo(app, null, 1f, null)
                    return@launch
                }
                pipeline.setBackground(null)
                pipeline.setBackgroundVideo(app, uri, info.aspect, info.mean)
                return@launch
            }
            val bmp: Bitmap? = when (f.bg) {
                Bg.OFFICE -> Backdrops.photographicOffice(app)
                Bg.LIBRARY -> Backdrops.photographicLibrary(app)
                Bg.UPLOAD -> f.uploadUri?.let { Backdrops.fromUri(app, Uri.parse(it)) }
                Bg.ROOM -> Backdrops.fromFile(roomFile())
                else -> null
            }
            if (gen != bgLoadGen) return@launch
            if (f.bg == Bg.UPLOAD && bmp == null) toast.tryEmit("Couldn't load that image")
            if (f.bg == Bg.ROOM && bmp == null) toast.tryEmit("Capture your room first")
            pipeline.setBackgroundVideo(app, null, 1f, null)
            pipeline.setBackground(bmp)
        }
    }

    private fun roomFile() = java.io.File(app.filesDir, "room.jpg")

    /**
     * "Capture my room": a 5 s countdown to step out of view, then the pipeline averages
     * several frames of the empty room. The photo matches this camera's lens, position,
     * perspective and lighting, which is what makes it look like a real wall.
     */
    fun captureRoom() {
        if (roomJob?.isActive == true) return
        roomJob = scope.launch {
            try {
                for (s in 5 downTo 1) {
                    roomCapture.value = "Step out of view… $s"
                    delay(1000)
                }
                roomCapture.value = "Capturing your empty room…"
                val result = CompletableDeferred<Pair<Bitmap?, String?>>()
                pipeline.captureRoom { bmp, err -> result.complete(bmp to err) }
                val (bmp, err) = withTimeoutOrNull(25_000) { result.await() }
                    ?: run {
                        pipeline.cancelRoomCapture()
                        null to "The camera stopped. Keep this screen open while capturing."
                    }
                if (bmp == null) {
                    err?.let { toast.tryEmit(it) }
                    return@launch
                }
                val saved = withContext(Dispatchers.IO) {
                    runCatching {
                        val tmp = java.io.File(app.filesDir, "room.jpg.tmp")
                        tmp.outputStream().use { bmp.compress(Bitmap.CompressFormat.JPEG, 94, it) }
                        if (!tmp.renameTo(roomFile())) {
                            roomFile().delete()
                            tmp.renameTo(roomFile())
                        }
                    }.isSuccess
                }
                if (!saved) {
                    toast.tryEmit("Couldn't save the room photo")
                    return@launch
                }
                setFx { copy(bg = Bg.ROOM, roomVersion = System.currentTimeMillis()) }
                toast.tryEmit("Room captured. Sit back down.")
            } finally {
                roomCapture.value = null
            }
        }
    }

    fun cancelRoomCapture() {
        roomJob?.cancel()
        roomJob = null
        pipeline.cancelRoomCapture()
        roomCapture.value = null
    }

    /** Output frame size for the current settings and how the phone is held. */
    fun outSize(): Pair<Int, Int> {
        val (l, s) = captureSize()
        val landscape = when (settings.value.orientation) {
            "portrait" -> false
            "auto" -> deviceRot % 180 != 0
            else -> true
        }
        return if (landscape) l to s else s to l
    }

    private var lastOut: Pair<Int, Int>? = null

    private fun refresh() {
        val s = settings.value
        val f = fx.value
        val p = pro.value
        val t = toggles.value
        val (w, h) = outSize()
        val throttled = health.value.throttled
        pipeline.params = RenderParams(
            outW = w, outH = h, deviceRot = deviceRot, manualRot = t.manualRot, mirror = t.mirror,
            frontCamera = camera.caps.value.front && qrCallback == null,
            brightness = f.brightness / 100f, contrast = f.contrast / 100f, saturation = f.saturation / 100f,
            warmth = f.warmth / 100f, sharpness = f.sharpness / 100f, look = f.look, smooth = f.smooth / 100f,
            lowLight = f.lowLight,
            bgMode = when (f.bg) { Bg.NONE -> 0; Bg.BLUR -> 1; else -> 2 },
            blurStrength = f.blurStrength / 100f, edge = f.edge / 100f, autoFrame = f.autoFrame,
            bgDepth = f.depth / 100f, bgMatch = f.matchLight / 100f, bgGrain = f.grain / 100f,
            bgWrap = f.lightWrap / 100f, sensorIso = isoForGrain,
            zebra = p.zebra, peaking = p.peaking, grid = t.grid, compare = t.compare,
            wantFace = faceUsers > 0, wantHist = histUsers > 0,
            encodePaused = t.paused,
            encodeFpsCap = if (throttled) minOf(15, s.fps) else s.fps,
            beauty = f.beauty, beautyOriginal = t.beautyCompare,
            thermalLimited = throttled,
        )
        if (isLive && lastOut != null && lastOut != (w to h)) restartEncoder()
    }

    // ------------------------------------------------------------ discovery / connection

    /** Reference counted: the connect screen and auto-connect can both need discovery. */
    fun startDiscovery() {
        if (discoveryUsers++ == 0) discovery.start()
    }

    fun stopDiscovery() {
        discoveryUsers = (discoveryUsers - 1).coerceAtLeast(0)
        if (discoveryUsers == 0) discovery.stop()
    }

    private fun deviceName(): String =
        runCatching { Settings.Global.getString(app.contentResolver, "device_name") }.getOrNull()
            ?.takeIf { it.isNotBlank() } ?: Build.MODEL

    fun connect(target: PcTarget) {
        if (conn.value.status != ConnStatus.Idle) return
        stopAuto()
        autoPaused.value = false
        userStopped = false
        lastTarget = target
        conn.value = ConnState(ConnStatus.Connecting, target.name.ifEmpty { target.hosts.first() }, target.os, target.transport)
        connectJob = scope.launch { attempt(target, reconnect = false) }
    }

    /**
     * One connection attempt. [reconnect] attempts are unattended (automatic reconnects and
     * auto-connect): they don't report errors in the UI, and they ask the PC not to show its
     * approval dialog, so only a phone the PC already trusts gets in.
     */
    private suspend fun attempt(target: PcTarget, reconnect: Boolean): Boolean {
        val s = Session(this)
        session = s
        lastFailReason = null
        val hello = JSONObject().put("v", Proto.VERSION).put("deviceId", deviceId)
            .put("cameraControlVersion", 1)
            .put("deviceName", deviceName()).put("model", "${Build.MANUFACTURER} ${Build.MODEL}")
            .put("transport", target.transport)
        target.pairKey?.let { hello.put("pairKey", it) }
        if (reconnect) hello.put("auto", true)
        try {
            val ack = withContext(Dispatchers.IO) {
                s.open(target, hello) {
                    mainHandler.post {
                        if (session === s && conn.value.status == ConnStatus.Connecting) {
                            conn.value = conn.value.copy(status = ConnStatus.Waiting)
                        }
                    }
                }
            }
            if (session !== s || userStopped) {
                s.close()
                return false
            }
            if (ack.optInt("cameraControlVersion") != 1) throw HandshakeException("version")
            val pcName = ack.optString("pcName", target.name)
            val pcOs = ack.optString("os", target.os)
            rememberPc(ack.optString("pcId", target.id), pcName, pcOs, s.connectedHost, target.port, target.transport)
            conn.value = ConnState(ConnStatus.Live, pcName, pcOs, target.transport, System.currentTimeMillis())
            s.start()
            onLive(s)
            beautyAck = ""
            sendBeautyState()
            return true
        } catch (e: Exception) {
            s.cancel()
            if (session === s) session = null
            lastFailReason = (e as? HandshakeException)?.reason
            if (userStopped) return false
            if (!reconnect) {
                val msg = when ((e as? HandshakeException)?.reason) {
                    "denied", "untrusted" -> "${target.name.ifEmpty { "The computer" }} declined the connection"
                    "busy" -> "Another phone is already streaming to this computer"
                    "version" -> "The desktop app is a different version. Update both apps."
                    "timeout" -> "No answer from the computer. Approve the request in the desktop app."
                    else -> if (target.transport == "usb")
                        "No USB link. Open the desktop app, plug in the cable and allow USB debugging."
                    else "Couldn't reach ${target.name.ifEmpty { target.hosts.first() }}. Check both devices are on the same Wi‑Fi."
                }
                conn.value = ConnState(error = msg)
            }
            return false
        }
    }

    private fun rememberPc(id: String, name: String, os: String, host: String, port: Int, transport: String) {
        if (id.isEmpty()) return
        val prev = savedPcs.value.firstOrNull { it.id == id }
        val wifiHost = if (transport == "usb") prev?.wifiHost.orEmpty() else host
        val pc = SavedPc(id, name, os, host, port, System.currentTimeMillis(), transport, wifiHost)
        val list = (savedPcs.value.filter { it.id != id } + pc)
            .sortedByDescending { it.lastUsed }.take(20)
        savedPcs.value = list
        scope.launch { store.savePcs(list) }
    }

    fun forgetPc(id: String) {
        val list = savedPcs.value.filter { it.id != id }
        savedPcs.value = list
        scope.launch { store.savePcs(list) }
    }

    private fun bitrateCap(): Int {
        var b = settings.value.bitrateKbps * 1000
        if (health.value.throttled) b = (b * 0.6f).toInt()
        return b
    }

    private fun onLive(s: Session) {
        currentBitrate = bitrateCap()
        lastGoodAt = System.currentTimeMillis()
        lastBytes = 0
        lastFramesOut = 0
        rtt = null
        pcStats = null
        health.value = health.value.copy(delayHistory = emptyList(), rttMs = null, pipelineMs = null,
            delayMs = null, recvFps = null, droppedFrames = null, totalFrames = null, inUseBy = emptyList(),
            vcamInstalled = null, localDrops = 0)
        setToggles { copy(paused = false) }
        setCameraEnabled(false)
    }

    private fun startCapture(s: Session) {
        startEncoder(s)
        startAudio()
        if (wifiLock == null) {
            runCatching {
                val wm = app.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
                val mode = if (Build.VERSION.SDK_INT >= 29) WifiManager.WIFI_MODE_FULL_LOW_LATENCY else WifiManager.WIFI_MODE_FULL_HIGH_PERF
                wifiLock = wm.createWifiLock(mode, "camstudio:stream").apply { setReferenceCounted(false); acquire() }
            }
        }
    }

    private fun startEncoder(s: Session) {
        val st = settings.value
        val (w, h) = outSize()
        lastOut = w to h
        s.maxVideoQueue = (st.fps * (if (st.lowLatency) 0.35f else 0.8f)).toInt().coerceAtLeast(6)
        s.sendJson(Proto.VIDEO_CONFIG, JSONObject().put("codec", "h264").put("width", w).put("height", h)
            .put("fps", st.fps).put("bitrate", currentBitrate))
        try {
            val enc = VideoEncoder(w, h, st.fps, currentBitrate, st.lowLatency) { pts, flags, data ->
                if (cameraEnabled.value && session === s) s.sendVideo(pts, flags, data)
            }
            enc.start()
            encoder = enc
            lastFramesOut = 0
            pipeline.setEncoder(enc)
        } catch (e: Exception) {
            Log.e(TAG, "encoder start failed", e)
            toast.tryEmit("This phone can't encode ${w}×$h at ${st.fps} fps. Try a lower resolution.")
        }
    }

    private fun stopEncoder() {
        val enc = encoder ?: return
        encoder = null
        pipeline.setEncoder(null)
        enc.stop()
    }

    private fun restartEncoder() {
        val s = session ?: return
        if (!isLive || !cameraEnabled.value) return
        stopEncoder()
        startEncoder(s)
    }

    private fun startAudio() {
        audio?.stop()
        audio = null
        micLevel.value = 0f
        val s = session ?: return
        val st = settings.value
        if (!isLive || !cameraEnabled.value || !st.micEnabled || !hasMicPermission()) return
        s.sendJson(Proto.AUDIO_CONFIG, JSONObject().put("sampleRate", AudioCapture.SAMPLE_RATE).put("channels", 1).put("format", "pcm_s16le"))
        val a = AudioCapture(st.noiseSuppression, { micLevel.value = it }) { pts, pcm, len ->
            // Video may be held back a few frames to line up with the background mask; hold the
            // audio back by the same amount so lips stay in sync.
            val delay = pipeline.videoDelayMs
            if (delay <= 0 && cameraEnabled.value && session === s) s.sendAudio(pts, pcm, len)
            else {
                val copy = pcm.copyOf(len)
                audioDelay.postDelayed({ if (cameraEnabled.value && session === s) s.sendAudio(pts, copy, len) }, delay.toLong())
            }
        }
        a.muted = toggles.value.muted
        if (a.start()) audio = a else toast.tryEmit("Microphone unavailable")
    }

    private fun teardownStream() {
        cameraEnabled.value = false
        stopEncoder()
        audio?.stop()
        audio = null
        audioDelay.removeCallbacksAndMessages(null)
        session?.dropCaptureQueue()
        micLevel.value = 0f
        lastOut = null
        runCatching { wifiLock?.release() }
        wifiLock = null
        bgLoadGen++
        pipeline.setBackgroundVideo(app, null, 1f, null)
        updateCamera()
        updateWakeLock()
    }

    /** User-initiated stop / disconnect / cancel. Also pauses auto-connect until the next connect. */
    fun disconnect() {
        stopAuto()
        autoPaused.value = true
        userStopped = true
        connectJob?.cancel()
        val s = session
        session = null
        teardownStream()
        if (s != null) {
            if (conn.value.status == ConnStatus.Live) s.close(true) else s.cancel()
        }
        conn.value = ConnState()
        setToggles { copy(paused = false, dimmed = false) }
        updateCamera()
    }

    fun clearError() {
        if (conn.value.error != null) conn.value = conn.value.copy(error = null)
    }

    // ------------------------------------------------------------ auto-connect

    private fun canAuto() = settings.value.autoConnect && !autoPaused.value &&
        savedPcs.value.isNotEmpty() && hasCameraPermission()

    /** Starts waiting for the last computer if auto-connect is on and nothing else is happening. */
    fun maybeStartAuto() {
        if (autoJob?.isActive == true || !canAuto() || conn.value.status != ConnStatus.Idle) return
        autoJob = scope.launch { autoLoop() }
    }

    /** "Stop" on the auto-connect banner or notification. */
    fun pauseAuto() {
        stopAuto()
        autoPaused.value = true
    }

    fun resumeAuto() {
        autoPaused.value = false
        userStopped = false
        clearError()
        maybeStartAuto()
    }

    private fun stopAuto() {
        val job = autoJob ?: return
        autoJob = null
        job.cancel()
        autoTarget.value = null
        // An unattended attempt may be in flight while the UI still shows Idle.
        if (conn.value.status == ConnStatus.Idle) {
            session?.cancel()
            session = null
        }
    }

    private suspend fun autoLoop() {
        val me = currentCoroutineContext()[Job]
        startDiscovery()
        try {
            // Give discovery a moment to learn the computer's current address (DHCP may change it).
            delay(1500)
            var failures = 0
            while (currentCoroutineContext().isActive && canAuto() && !userStopped &&
                conn.value.status == ConnStatus.Idle) {
                val pc = savedPcs.value.first()
                autoTarget.value = pc.name
                for (target in autoTargets(pc)) {
                    if (!currentCoroutineContext().isActive || userStopped) return
                    if (conn.value.status != ConnStatus.Idle) return
                    lastTarget = target
                    if (attempt(target, reconnect = true)) return
                    val stopMsg = when (lastFailReason) {
                        "untrusted", "denied", "timeout" ->
                            "${pc.name} no longer trusts this phone. Connect once and approve it on the computer to turn auto‑connect back on."
                        "version" -> "The desktop app is a different version. Update both apps."
                        else -> null
                    }
                    if (stopMsg != null) {
                        autoPaused.value = true
                        if (conn.value.status == ConnStatus.Idle) conn.value = ConnState(error = stopMsg)
                        return
                    }
                }
                failures++
                // Quick retries at first, then every 10 s; slower while another phone is streaming.
                delay(if (lastFailReason == "busy") 15_000L else minOf(2_000L + failures * 1_000L, 10_000L))
            }
        } finally {
            stopDiscovery()
            if (autoJob === me || autoJob == null) {
                autoJob = null
                autoTarget.value = null
            }
        }
    }

    /** Where to look for [pc]: its current discovered address, its last Wi-Fi address and USB. */
    private fun autoTargets(pc: SavedPc): List<PcTarget> {
        val seen = discovered.value.firstOrNull { it.id == pc.id }
        val hosts = listOfNotNull(seen?.host, pc.wifiHost.ifEmpty { null }).distinct()
        val wifi = if (hosts.isEmpty()) null else PcTarget(hosts, seen?.port ?: pc.port, pc.name, pc.os, pc.id)
        // Fails instantly unless the desktop app has set up "adb reverse" over a cable.
        val usb = PcTarget(listOf("127.0.0.1"), Proto.TCP_PORT, pc.name, pc.os, pc.id, transport = "usb")
        return if (pc.transport == "usb") listOfNotNull(usb, wifi) else listOfNotNull(wifi, usb)
    }

    // ------------------------------------------------------------ foreground service / wake lock

    /** Title and text for the foreground-service notification. */
    fun serviceText(): Pair<String, String> {
        val c = conn.value
        return when (c.status) {
            ConnStatus.Live -> if (cameraEnabled.value) "Streaming to ${c.pcName}" to "Camera and microphone are in use by your computer"
                else "Connected to ${c.pcName}" to "Camera off. Open camera from the computer when needed."
            ConnStatus.Connecting, ConnStatus.Waiting -> "Connecting to ${c.pcName}" to "Opening a link to the desktop app"
            ConnStatus.Reconnecting -> "Reconnecting to ${c.pcName}" to "The link dropped. Trying again."
            ConnStatus.Idle -> "Waiting for ${autoTarget.value ?: "your computer"}" to
                "Connects automatically when CamStudio Pro is running on the computer"
        }
    }

    private fun syncService() {
        val want = hasCameraPermission() && (conn.value.status != ConnStatus.Idle || autoTarget.value != null)
        when {
            // Android only allows a camera/microphone service to start while the app is on screen;
            // once started it keeps the camera usable in the background.
            want && !serviceRunning -> if (activityStarted) serviceRunning = StreamService.start(app)
            !want && serviceRunning -> {
                serviceRunning = false
                StreamService.stop(app)
            }
            want -> StreamService.refresh(app, this)
        }
    }

    /** Called by the service when the system or the user stops it. */
    internal fun onServiceStopped() {
        serviceRunning = false
    }

    /** Called from the notification's Stop action. */
    internal fun stopFromNotification() {
        if (conn.value.status == ConnStatus.Idle) pauseAuto() else disconnect()
    }

    /**
     * Keeps the CPU awake while streaming (so it continues with the screen off), and while
     * waiting for the computer on a charger. On battery, waiting retries may slow down while
     * the phone sleeps and pick up again when it wakes.
     */
    private fun updateWakeLock() {
        val want = isLive && cameraEnabled.value
        if (want) {
            // Re-armed every tick, so it lapses on its own if the engine ever stops ticking.
            runCatching {
                val wl = wakeLock ?: (app.getSystemService(Context.POWER_SERVICE) as PowerManager)
                    .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "camstudio:link")
                    .apply { setReferenceCounted(false) }
                wakeLock = wl
                wl.acquire(60_000L)
            }
        } else if (wakeLock?.isHeld == true) {
            runCatching { wakeLock?.release() }
        }
    }

    // ------------------------------------------------------------ Session.Listener (socket threads)

    override fun onStats(json: JSONObject) {
        mainHandler.post { pcStats = json }
    }

    override fun onControl(json: JSONObject) {
        val cmd = json.optString("cmd")
        if (cmd == "camera" && json.optInt("cameraControlVersion") == 1) mainHandler.post {
            setCameraEnabled(json.optBoolean("enabled"), json.optString("requestId"))
        }
        if (cmd == "keyframe") encoder?.requestKeyframe()
        if (cmd == "beauty") mainHandler.post {
            val changes = json.optJSONObject("changes") ?: return@post
            setFx(requestId = json.optString("requestId").take(80)) { copy(beauty = beauty.patch(changes)) }
        }
        if (cmd == "videoOptions") mainHandler.post {
            val changes = json.optJSONObject("changes") ?: return@post
            videoOptionsAck = json.optString("requestId").take(80)
            videoOptionsError = ""
            val options = VideoOptions.fromJson(changes, settings.value, fx.value)
            videoOptionsError = options.error(fx.value.uploadUri != null, roomFile().exists())
            if (videoOptionsError.isEmpty()) {
                if (options.changeQuality) setSettings { options.settings(this) }
                setFx { options.effects(this) }
            }
            session?.sendJson(Proto.CONTROL, videoOptionsReport().put("cmd", "videoOptionsState"))
        }
        if (cmd == "beautyCompare") {
            // A short lease automatically restores the effects if the PC closes or loses capture.
            pipeline.remoteBeautyOriginalUntil = if (json.optBoolean("original")) System.nanoTime() + 600_000_000L else 0L
        }
    }

    private fun videoOptionsReport() = JSONObject().put("videoOptionsVersion", 1)
        .put("videoResolution", settings.value.resolution).put("videoFps", settings.value.fps).put("videoBackground", fx.value.bg)
        .put("videoNatural", fx.value.depth > 0 || fx.value.matchLight > 0 || fx.value.lightWrap > 0)
        .put("videoOptionsAck", videoOptionsAck).put("videoOptionsError", videoOptionsError)

    private fun beautyReport() = JSONObject().put("beautyVersion", 1).put("beauty", fx.value.beauty.toJson())
        .put("beautyAck", beautyAck).put("beautyTracking", pipeline.beautyTracking.value)
        .put("beautyOriginal", pipeline.isBeautyOriginal)

    /**
     * Slider drags produce dozens of changes per second; writing each one to storage
     * (a full DataStore rewrite) queued up I/O. Persist the latest state once it settles.
     */
    private fun saveFxSoon() {
        fxSaveJob?.cancel()
        fxSaveJob = scope.launch {
            delay(400)
            fxSaveJob = null
            store.saveFx(fx.value)
        }
    }

    private fun flushFx() {
        if (fxSaveJob?.isActive != true) return
        fxSaveJob?.cancel()
        fxSaveJob = null
        val latest = fx.value
        scope.launch { store.saveFx(latest) }
    }

    private fun sendBeautyStateSoon() {
        val wait = 100L - (System.currentTimeMillis() - beautySentAt)
        if (wait <= 0) {
            beautySendJob?.cancel()
            beautySendJob = null
            sendBeautyState()
        } else if (beautySendJob?.isActive != true) {
            beautySendJob = scope.launch {
                delay(wait)
                beautySendJob = null
                sendBeautyState()
            }
        }
    }

    private fun sendBeautyState() {
        beautySentAt = System.currentTimeMillis()
        session?.sendJson(Proto.CONTROL, beautyReport().put("cmd", "beautyState"))
    }

    override fun onRtt(ms: Int) {
        mainHandler.post { rtt = ms }
    }

    override fun onCongestion() {
        congestionAt = System.currentTimeMillis()
        encoder?.requestKeyframe()
    }

    override fun onClosed(error: String?) {
        mainHandler.post {
            if (userStopped || conn.value.status != ConnStatus.Live) return@post
            val target = lastTarget
            teardownStream()
            session = null
            if (target == null) {
                conn.value = ConnState(error = error)
                updateCamera()
                maybeStartAuto()
                return@post
            }
            val prev = conn.value
            conn.value = prev.copy(status = ConnStatus.Reconnecting)
            connectJob = scope.launch {
                for (i in 0 until 4) {
                    delay(1200L + i * 800L)
                    if (userStopped) return@launch
                    if (attempt(target.copy(pairKey = null), reconnect = true)) return@launch
                    conn.value = prev.copy(status = ConnStatus.Reconnecting)
                }
                if (!userStopped) {
                    conn.value = ConnState(error = "Lost the connection to ${prev.pcName}. ${error ?: ""}".trim())
                    updateCamera()
                    // Keep waiting for the computer (e.g. it's restarting) instead of giving up.
                    maybeStartAuto()
                }
            }
        }
    }

    // ------------------------------------------------------------ periodic work

    private suspend fun tickLoop() {
        while (scope.isActive) {
            delay(1000)
            runCatching { tick() }.onFailure { Log.w(TAG, "tick: $it") }
        }
    }

    private fun tick() {
        tickCount++
        var h = readDevice(health.value)
        health.value = h
        updateWakeLock()
        // Background grain follows the sensor's ISO; only re-render params on a real change.
        camera.live.value.iso?.let { iso ->
            val prev = isoForGrain
            if (prev == null || abs(iso - prev) > prev * 0.15f) {
                isoForGrain = iso
                refresh()
            }
        }
        val st = settings.value

        // Overheating protection.
        val hot = st.overheatProtect && ((h.tempC ?: 0f) >= 43f || thermalStatus() >= 3)
        val cool = (h.tempC ?: 0f) < 40.5f && thermalStatus() < 2
        val throttled = if (h.throttled) !(cool || !st.overheatProtect) else hot
        if (throttled != h.throttled) {
            h = h.copy(throttled = throttled)
            health.value = h
            updateCamera()
            if (throttled) toast.tryEmit("Phone is hot. Lowering frame rate to cool down.")
            currentBitrate = minOf(currentBitrate, bitrateCap()).coerceAtLeast(600_000)
            encoder?.updateBitrate(currentBitrate)
            refresh()
        }

        val s = session
        val enc = encoder
        if (isLive && s != null) {
            s.sendPing()
            val framesOut = enc?.framesOut ?: 0
            val sentFps = (framesOut - lastFramesOut).toInt().coerceAtLeast(0)
            lastFramesOut = framesOut
            val bytes = s.bytesSent.get()
            val bps = (bytes - lastBytes) * 8
            lastBytes = bytes

            // Adaptive bitrate: step down on congestion, creep back up when clean.
            val now = System.currentTimeMillis()
            val cap = bitrateCap()
            if (st.adaptive) {
                val congested = now - congestionAt < 1100 || s.backlog
                s.backlog = false
                if (congested) {
                    currentBitrate = (currentBitrate * 0.7f).toInt().coerceAtLeast(600_000)
                    lastGoodAt = now
                } else if (now - lastGoodAt > 5000 && currentBitrate < cap) {
                    currentBitrate = (currentBitrate * 1.12f).toInt().coerceAtMost(cap)
                    lastGoodAt = now - 3000
                }
            } else {
                currentBitrate = cap
            }
            currentBitrate = currentBitrate.coerceAtMost(cap)
            enc?.updateBitrate(currentBitrate)

            val ps = pcStats
            val encodeMs = enc?.encodeMs?.takeIf { it > 0f }?.roundToInt()
            val pipelineMs = ps?.takeIf { it.has("pipelineMs") }?.optInt("pipelineMs")
            val r = rtt
            val delayMs = if (encodeMs != null && r != null && pipelineMs != null) encodeMs + r / 2 + pipelineMs else null
            val inUse = ps?.optJSONArray("inUseBy")?.let { a: JSONArray -> (0 until a.length()).map { a.optString(it) } } ?: emptyList()
            h = h.copy(
                rttMs = r, encodeMs = encodeMs, pipelineMs = pipelineMs, delayMs = delayMs,
                sentFps = sentFps, recvFps = ps?.takeIf { it.has("recvFps") }?.optInt("recvFps"),
                targetFps = if (h.throttled) minOf(15, st.fps) else st.fps,
                bitrateBps = bps, targetBitrateBps = currentBitrate,
                droppedFrames = ps?.takeIf { it.has("droppedFrames") }?.optLong("droppedFrames"),
                totalFrames = ps?.takeIf { it.has("totalFrames") }?.optLong("totalFrames"),
                localDrops = s.framesDropped.get(),
                inUseBy = inUse,
                vcamInstalled = ps?.takeIf { it.has("vcamInstalled") }?.optBoolean("vcamInstalled"),
                delayHistory = if (delayMs != null) (h.delayHistory + delayMs).takeLast(300) else h.delayHistory,
            )

            val t = toggles.value
            val state = JSONObject()
                .put("cameraControlVersion", 1).put("cameraEnabled", cameraEnabled.value)
                .put("muted", t.muted || !st.micEnabled).put("paused", t.paused)
                .put("sentFps", sentFps).put("bitrateBps", bps)
            val videoOptions = videoOptionsReport()
            videoOptions.keys().forEach { key -> state.put(key, videoOptions.get(key)) }
            val beauty = beautyReport()
            beauty.keys().forEach { key -> state.put(key, beauty.get(key)) }
            h.battery?.let { state.put("battery", it) }
            state.put("charging", h.charging)
            h.tempC?.let { state.put("tempC", it.toDouble()) }
            h.rssi?.let { state.put("wifiRssi", it) }
            h.band?.let { state.put("wifiBand", it) }
            r?.let { state.put("rttMs", it) }
            encodeMs?.let { state.put("encodeMs", it) }
            s.sendJson(Proto.STATE, state)

            // Screen-off-while-streaming: dim after 10 s without a touch.
            if (cameraEnabled.value && st.screenOff && !t.dimmed && now - lastTouch > 10_000) setToggles { copy(dimmed = true) }
        }
        health.value = h

        // Keep focus/exposure on the face.
        if (tickCount % 2 == 0 && cameraRunning && faceUsers > 0) {
            pipeline.face.value?.let { f ->
                pipeline.outToBuffer(f.centerX(), f.centerY())?.let { camera.focusAt(it, quiet = true) }
            }
        }
    }

    private fun thermalStatus(): Int =
        if (Build.VERSION.SDK_INT >= 29) runCatching {
            (app.getSystemService(Context.POWER_SERVICE) as PowerManager).currentThermalStatus
        }.getOrDefault(0) else 0

    private fun readDevice(h: Health): Health {
        var out = h
        runCatching {
            val i: Intent? = app.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            if (i != null) {
                val level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
                val scale = i.getIntExtra(BatteryManager.EXTRA_SCALE, 100)
                val status = i.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
                val temp = i.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, Int.MIN_VALUE)
                out = out.copy(
                    battery = if (level >= 0 && scale > 0) level * 100 / scale else null,
                    charging = status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL,
                    tempC = if (temp != Int.MIN_VALUE) temp / 10f else null,
                )
            }
        }
        runCatching {
            val wm = app.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            @Suppress("DEPRECATION")
            val info = wm.connectionInfo
            val connected = info != null && info.networkId != -1 && info.rssi > -127
            out = if (connected) {
                val f = info.frequency
                out.copy(rssi = info.rssi, band = when {
                    f in 2400..2500 -> "2.4 GHz"
                    f in 4900..5900 -> "5 GHz"
                    f in 5925..7125 -> "6 GHz"
                    else -> null
                })
            } else out.copy(rssi = null, band = null)
        }
        return out
    }

    // ------------------------------------------------------------ snapshot

    fun takeSnapshot() {
        if (!cameraRunning) return
        pipeline.snapshot { bmp ->
            if (bmp == null) {
                toast.tryEmit("Snapshot failed")
                return@snapshot
            }
            scope.launch(Dispatchers.IO) {
                val ok = runCatching { saveBitmap(bmp) }.getOrDefault(false)
                bmp.recycle()
                toast.tryEmit(if (ok) "Saved to Pictures/CamStudio" else "Couldn't save the snapshot")
            }
        }
    }

    private fun saveBitmap(bmp: Bitmap): Boolean {
        val name = "CamStudio_${System.currentTimeMillis()}.jpg"
        val values = ContentValues().apply {
            put(MediaStore.Images.Media.DISPLAY_NAME, name)
            put(MediaStore.Images.Media.MIME_TYPE, "image/jpeg")
            if (Build.VERSION.SDK_INT >= 29) {
                put(MediaStore.Images.Media.RELATIVE_PATH, Environment.DIRECTORY_PICTURES + "/CamStudio")
                put(MediaStore.Images.Media.IS_PENDING, 1)
            }
        }
        val resolver = app.contentResolver
        val uri = resolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values) ?: return false
        resolver.openOutputStream(uri)?.use { bmp.compress(Bitmap.CompressFormat.JPEG, 93, it) } ?: return false
        if (Build.VERSION.SDK_INT >= 29) {
            values.clear()
            values.put(MediaStore.Images.Media.IS_PENDING, 0)
            resolver.update(uri, values, null, null)
        }
        return true
    }

    /** Plain-text report for "Copy diagnostics". */
    fun diagnostics(): String {
        val h = health.value
        val c = conn.value
        val s = settings.value
        val caps = camera.caps.value
        val (w, hgt) = outSize()
        fun v(x: Any?) = x?.toString() ?: "—"
        return buildString {
            appendLine("CamStudio Pro diagnostics")
            appendLine("Phone: ${Build.MANUFACTURER} ${Build.MODEL} (Android ${Build.VERSION.RELEASE})")
            appendLine("Status: ${c.status} ${c.pcName} ${c.pcOs} via ${c.transport}")
            appendLine("Output: ${w}x$hgt @ ${s.fps} fps, H.264, target ${h.targetBitrateBps / 1000} kbps, adaptive=${s.adaptive}")
            appendLine("Camera buffer: ${caps.bufferW}x${caps.bufferH}, front=${caps.front}, camera fps=${"%.1f".format(pipeline.cameraFps)}")
            appendLine("Delay: ${v(h.delayMs)} ms (encode ${v(h.encodeMs)} + rtt ${v(h.rttMs)}/2 + pc ${v(h.pipelineMs)})")
            appendLine("Frame rate: sent ${h.sentFps}, received ${v(h.recvFps)}, target ${h.targetFps}")
            appendLine("Bitrate: ${h.bitrateBps / 1000} kbps")
            appendLine("Dropped: pc ${v(h.droppedFrames)} / ${v(h.totalFrames)}, phone queue ${h.localDrops}")
            appendLine("Battery: ${v(h.battery)}% charging=${h.charging}, temp ${v(h.tempC)} °C, throttled=${h.throttled}")
            appendLine("Wi-Fi: ${v(h.rssi)} dBm ${v(h.band)}")
            append("Quality score: ${v(h.score)}")
        }
    }
}
