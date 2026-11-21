package com.camstudio.pro.media

import android.content.Context
import android.graphics.ImageFormat
import android.graphics.PointF
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraMetadata
import android.hardware.camera2.CaptureRequest
import android.hardware.camera2.CaptureResult
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.TotalCaptureResult
import android.hardware.camera2.params.ColorSpaceTransform
import android.hardware.camera2.params.RggbChannelVector
import android.util.Log
import android.util.Range
import android.util.Size
import android.view.Surface
import androidx.camera.camera2.interop.Camera2CameraControl
import androidx.camera.camera2.interop.Camera2CameraInfo
import androidx.camera.camera2.interop.Camera2Interop
import androidx.camera.camera2.interop.CaptureRequestOptions
import androidx.camera.camera2.interop.ExperimentalCamera2Interop
import androidx.camera.core.Camera
import androidx.camera.core.CameraSelector
import androidx.camera.core.FocusMeteringAction
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.core.SurfaceOrientedMeteringPointFactory
import androidx.camera.core.resolutionselector.AspectRatioStrategy
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.LifecycleRegistry
import com.camstudio.pro.data.ProState
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import kotlinx.coroutines.flow.MutableStateFlow
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import kotlin.math.abs
import kotlin.math.ln
import kotlin.math.pow

/** What the current lens can do. Controls the UI cannot use are disabled, not hidden. */
data class CameraCaps(
    val ready: Boolean = false,
    val front: Boolean = true,
    val hasFront: Boolean = true,
    val hasBack: Boolean = true,
    val manualSensor: Boolean = false,
    val isoMin: Int = 100,
    val isoMax: Int = 3200,
    val shutterMinNs: Long = 100_000L,
    val shutterMaxNs: Long = 100_000_000L,
    val manualWb: Boolean = false,
    val awbModes: List<Int> = emptyList(),
    val minFocusDiopters: Float = 0f,
    val evMin: Int = 0,
    val evMax: Int = 0,
    val evStep: Float = 0f,
    val torch: Boolean = false,
    val zoomMin: Float = 1f,
    val zoomMax: Float = 1f,
    val fpsChoices: List<Int> = listOf(30),
    val stabilization: Boolean = false,
    val antiBanding: List<Int> = emptyList(),
    val bufferW: Int = 0,
    val bufferH: Int = 0,
)

/** Values reported by the sensor for recent frames. Null = not reported by this device. */
data class CameraLive(
    val iso: Int? = null,
    val shutterNs: Long? = null,
    val focusDiopters: Float? = null,
    val zoom: Float = 1f,
    val torchOn: Boolean = false,
)

@androidx.annotation.OptIn(ExperimentalCamera2Interop::class)
class CameraController(private val ctx: Context, private val pipeline: GlPipeline) {
    companion object {
        private const val TAG = "CameraController"
    }

    val caps = MutableStateFlow(CameraCaps())
    val live = MutableStateFlow(CameraLive())
    val error = MutableStateFlow<String?>(null)

    private val main = ContextCompat.getMainExecutor(ctx)
    private val analysisExec = Executors.newSingleThreadExecutor { r -> Thread(r, "csp-qr") }

    private val owner = object : LifecycleOwner {
        val registry = LifecycleRegistry(this)
        override val lifecycle: Lifecycle get() = registry
    }

    private var provider: ProcessCameraProvider? = null
    private var camera: Camera? = null
    private var generation = 0
    private var fps = 30
    private var frameCounter = 0
    @Volatile private var lastTransform: ColorSpaceTransform? = null
    @Volatile private var autoGains: RggbChannelVector? = null
    @Volatile private var wbIsAuto = true
    private var lastPro = ProState()

    init {
        owner.registry.currentState = Lifecycle.State.CREATED
    }

    private val captureCallback = object : CameraCaptureSession.CaptureCallback() {
        override fun onCaptureCompleted(session: CameraCaptureSession, request: CaptureRequest, result: TotalCaptureResult) {
            if (frameCounter++ % 6 != 0) return
            result.get(CaptureResult.COLOR_CORRECTION_TRANSFORM)?.let { lastTransform = it }
            if (wbIsAuto) result.get(CaptureResult.COLOR_CORRECTION_GAINS)?.let { autoGains = it }
            val cur = live.value
            val next = cur.copy(
                iso = result.get(CaptureResult.SENSOR_SENSITIVITY) ?: cur.iso,
                shutterNs = result.get(CaptureResult.SENSOR_EXPOSURE_TIME) ?: cur.shutterNs,
                focusDiopters = result.get(CaptureResult.LENS_FOCUS_DISTANCE) ?: cur.focusDiopters,
            )
            if (next != cur) live.value = next
        }
    }

    /**
     * Binds the camera. [qr] non-null adds a QR analysis stream and reports decoded text.
     * Must be called on the main thread.
     */
    fun start(front: Boolean, longSide: Int, shortSide: Int, fps: Int, qr: ((String) -> Unit)? = null) {
        val gen = ++generation
        this.fps = fps
        val future = ProcessCameraProvider.getInstance(ctx)
        future.addListener({
            if (gen != generation) return@addListener
            try {
                val p = future.get()
                provider = p
                bind(p, front, longSide, shortSide, fps, qr)
            } catch (e: Exception) {
                Log.e(TAG, "bind failed", e)
                error.value = "Camera unavailable: ${e.message ?: e.javaClass.simpleName}"
            }
        }, main)
    }

    fun stop() {
        generation++
        runCatching { provider?.unbindAll() }
        camera = null
        owner.registry.currentState = Lifecycle.State.CREATED
        caps.value = caps.value.copy(ready = false)
        live.value = CameraLive()
    }

    private fun bind(p: ProcessCameraProvider, wantFront: Boolean, longSide: Int, shortSide: Int, fps: Int, qr: ((String) -> Unit)?) {
        p.unbindAll()
        val hasFront = runCatching { p.hasCamera(CameraSelector.DEFAULT_FRONT_CAMERA) }.getOrDefault(false)
        val hasBack = runCatching { p.hasCamera(CameraSelector.DEFAULT_BACK_CAMERA) }.getOrDefault(false)
        if (!hasFront && !hasBack) {
            error.value = "This device has no usable camera"
            return
        }
        val front = if (wantFront) hasFront else !hasBack
        val selector = if (front) CameraSelector.DEFAULT_FRONT_CAMERA else CameraSelector.DEFAULT_BACK_CAMERA

        // Characteristics of the camera we are about to open.
        val info = selector.filter(p.availableCameraInfos).firstOrNull()
        val c2 = info?.let { Camera2CameraInfo.from(it) }
        val fpsRanges = c2?.getCameraCharacteristic(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.toList() ?: emptyList()
        val fpsChoices = listOf(24, 30, 60).filter { f -> fpsRanges.any { it.upper == f } }.ifEmpty { listOf(30) }
        val target = fpsRanges.filter { it.upper == fps }.maxByOrNull { it.lower }

        val builder = Preview.Builder()
            .setTargetRotation(Surface.ROTATION_0)
            .setResolutionSelector(
                ResolutionSelector.Builder()
                    .setAspectRatioStrategy(AspectRatioStrategy.RATIO_16_9_FALLBACK_AUTO_STRATEGY)
                    .setResolutionStrategy(ResolutionStrategy(Size(longSide, shortSide),
                        ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER))
                    .build()
            )
        val ext = Camera2Interop.Extender(builder)
        ext.setSessionCaptureCallback(captureCallback)
        if (target != null) ext.setCaptureRequestOption(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, target)
        val preview = builder.build()

        var bufW = 0
        var bufH = 0
        preview.setSurfaceProvider { request ->
            val res = request.resolution
            bufW = res.width
            bufH = res.height
            val sensorRot = runCatching { request.camera.cameraInfo.getSensorRotationDegrees(Surface.ROTATION_0) }.getOrDefault(90)
            val swap = sensorRot % 180 != 0
            val upW = if (swap) res.height else res.width
            val upH = if (swap) res.width else res.height
            caps.value = caps.value.copy(bufferW = res.width, bufferH = res.height)
            pipeline.createCameraSurface(res.width, res.height, upW, upH) { surface ->
                request.provideSurface(surface, main) { pipeline.releaseCameraSurface(surface) }
            }
        }

        owner.registry.currentState = Lifecycle.State.RESUMED
        val cam = if (qr != null) {
            val analysis = ImageAnalysis.Builder()
                .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                .setResolutionSelector(
                    ResolutionSelector.Builder().setResolutionStrategy(
                        ResolutionStrategy(Size(1280, 720), ResolutionStrategy.FALLBACK_RULE_CLOSEST_LOWER_THEN_HIGHER)
                    ).build()
                )
                .build()
            val reader = MultiFormatReader().apply {
                setHints(mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE), DecodeHintType.TRY_HARDER to true))
            }
            var buf = ByteArray(0)
            analysis.setAnalyzer(analysisExec) { image ->
                try {
                    if (image.format == ImageFormat.YUV_420_888) {
                        val plane = image.planes[0]
                        val stride = plane.rowStride
                        val bb = plane.buffer
                        val needed = bb.remaining()
                        if (buf.size < needed) buf = ByteArray(needed)
                        bb.get(buf, 0, needed)
                        val src = PlanarYUVLuminanceSource(buf, stride, image.height, 0, 0, image.width, image.height, false)
                        val text = try {
                            reader.decodeWithState(BinaryBitmap(HybridBinarizer(src))).text
                        } catch (_: Exception) {
                            null
                        } finally {
                            reader.reset()
                        }
                        if (text != null) main.execute { qr(text) }
                    }
                } catch (e: Exception) {
                    Log.d(TAG, "qr: $e")
                } finally {
                    image.close()
                }
            }
            p.bindToLifecycle(owner, selector, preview, analysis)
        } else {
            p.bindToLifecycle(owner, selector, preview)
        }
        camera = cam
        error.value = null

        val ci = Camera2CameraInfo.from(cam.cameraInfo)
        fun <T> ch(key: CameraCharacteristics.Key<T>): T? = runCatching { ci.getCameraCharacteristic(key) }.getOrNull()
        val capsArr = ch(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES) ?: IntArray(0)
        val manualSensor = capsArr.contains(CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_MANUAL_SENSOR)
        val manualPost = capsArr.contains(CameraMetadata.REQUEST_AVAILABLE_CAPABILITIES_MANUAL_POST_PROCESSING)
        val isoRange = ch(CameraCharacteristics.SENSOR_INFO_SENSITIVITY_RANGE)
        val expRange = ch(CameraCharacteristics.SENSOR_INFO_EXPOSURE_TIME_RANGE)
        val awb = ch(CameraCharacteristics.CONTROL_AWB_AVAILABLE_MODES)?.toList() ?: emptyList()
        val exposure = cam.cameraInfo.exposureState
        val zoom = cam.cameraInfo.zoomState.value
        val stab = ch(CameraCharacteristics.CONTROL_AVAILABLE_VIDEO_STABILIZATION_MODES)
            ?.contains(CameraMetadata.CONTROL_VIDEO_STABILIZATION_MODE_ON) == true
        caps.value = CameraCaps(
            ready = true,
            front = front,
            hasFront = hasFront,
            hasBack = hasBack,
            manualSensor = manualSensor && isoRange != null && expRange != null,
            isoMin = isoRange?.lower ?: 100,
            isoMax = isoRange?.upper ?: 3200,
            shutterMinNs = expRange?.lower ?: 100_000L,
            shutterMaxNs = expRange?.upper ?: 100_000_000L,
            manualWb = manualPost && awb.contains(CameraMetadata.CONTROL_AWB_MODE_OFF),
            awbModes = awb,
            minFocusDiopters = ch(CameraCharacteristics.LENS_INFO_MINIMUM_FOCUS_DISTANCE) ?: 0f,
            evMin = if (exposure.isExposureCompensationSupported) exposure.exposureCompensationRange.lower else 0,
            evMax = if (exposure.isExposureCompensationSupported) exposure.exposureCompensationRange.upper else 0,
            evStep = exposure.exposureCompensationStep.toFloat(),
            torch = cam.cameraInfo.hasFlashUnit(),
            zoomMin = zoom?.minZoomRatio ?: 1f,
            zoomMax = zoom?.maxZoomRatio ?: 1f,
            fpsChoices = fpsChoices,
            stabilization = stab,
            antiBanding = ch(CameraCharacteristics.CONTROL_AE_AVAILABLE_ANTIBANDING_MODES)?.toList() ?: emptyList(),
            bufferW = bufW,
            bufferH = bufH,
        )
        live.value = CameraLive(zoom = zoom?.zoomRatio ?: 1f)
        applyPro(lastPro)
    }

    fun setZoom(ratio: Float) {
        val c = caps.value
        val r = ratio.coerceIn(c.zoomMin, c.zoomMax)
        camera?.cameraControl?.setZoomRatio(r)
        live.value = live.value.copy(zoom = r)
    }

    fun setTorch(on: Boolean) {
        if (!caps.value.torch) return
        camera?.cameraControl?.enableTorch(on)
        live.value = live.value.copy(torchOn = on)
    }

    /** [p] is in normalized camera-buffer coordinates (see GlPipeline.viewToBuffer). */
    fun focusAt(p: PointF, quiet: Boolean = false) {
        val cam = camera ?: return
        if (lastPro.locked || lastPro.focusManual) return
        runCatching {
            val point = SurfaceOrientedMeteringPointFactory(1f, 1f).createPoint(p.x, p.y, if (quiet) 0.25f else 0.15f)
            val action = FocusMeteringAction.Builder(point, FocusMeteringAction.FLAG_AF or FocusMeteringAction.FLAG_AE)
                .setAutoCancelDuration(if (quiet) 3 else 5, TimeUnit.SECONDS)
                .build()
            cam.cameraControl.startFocusAndMetering(action)
            Unit
        }
    }

    /** Pushes every manual control to the camera. Safe to call before the camera is bound. */
    fun applyPro(pro: ProState) {
        lastPro = pro
        val cam = camera ?: return
        val c = caps.value
        val l = live.value
        val b = CaptureRequestOptions.Builder()
        try {
            if ((pro.isoManual || pro.shutterManual) && c.manualSensor) {
                val iso = (if (pro.isoManual) pro.iso else l.iso ?: 400).coerceIn(c.isoMin, c.isoMax)
                val frame = 1_000_000_000L / fps
                val exp = (if (pro.shutterManual) pro.shutterNs else l.shutterNs ?: (frame / 2)).coerceIn(c.shutterMinNs, c.shutterMaxNs)
                b.setCaptureRequestOption(CaptureRequest.CONTROL_AE_MODE, CameraMetadata.CONTROL_AE_MODE_OFF)
                b.setCaptureRequestOption(CaptureRequest.SENSOR_SENSITIVITY, iso)
                b.setCaptureRequestOption(CaptureRequest.SENSOR_EXPOSURE_TIME, exp)
                b.setCaptureRequestOption(CaptureRequest.SENSOR_FRAME_DURATION, maxOf(frame, exp))
            }
            wbIsAuto = !pro.wbManual
            if (pro.wbManual) {
                val t = lastTransform
                if (c.manualWb && t != null) {
                    b.setCaptureRequestOption(CaptureRequest.CONTROL_AWB_MODE, CameraMetadata.CONTROL_AWB_MODE_OFF)
                    b.setCaptureRequestOption(CaptureRequest.COLOR_CORRECTION_MODE, CameraMetadata.COLOR_CORRECTION_MODE_TRANSFORM_MATRIX)
                    b.setCaptureRequestOption(CaptureRequest.COLOR_CORRECTION_TRANSFORM, t)
                    b.setCaptureRequestOption(CaptureRequest.COLOR_CORRECTION_GAINS, gainsForKelvin(pro.kelvin))
                } else {
                    nearestAwbPreset(pro.kelvin, c.awbModes)?.let {
                        b.setCaptureRequestOption(CaptureRequest.CONTROL_AWB_MODE, it)
                    }
                }
            }
            if (pro.focusManual && c.minFocusDiopters > 0f) {
                b.setCaptureRequestOption(CaptureRequest.CONTROL_AF_MODE, CameraMetadata.CONTROL_AF_MODE_OFF)
                b.setCaptureRequestOption(CaptureRequest.LENS_FOCUS_DISTANCE, pro.focus.coerceIn(0f, 1f) * c.minFocusDiopters)
            }
            if (pro.locked) {
                b.setCaptureRequestOption(CaptureRequest.CONTROL_AE_LOCK, true)
                b.setCaptureRequestOption(CaptureRequest.CONTROL_AWB_LOCK, true)
                if (!pro.focusManual) {
                    b.setCaptureRequestOption(CaptureRequest.CONTROL_AF_MODE, CameraMetadata.CONTROL_AF_MODE_AUTO)
                }
            }
            val band = if (pro.antiFlicker == 60) CameraMetadata.CONTROL_AE_ANTIBANDING_MODE_60HZ
            else CameraMetadata.CONTROL_AE_ANTIBANDING_MODE_50HZ
            if (c.antiBanding.contains(band)) b.setCaptureRequestOption(CaptureRequest.CONTROL_AE_ANTIBANDING_MODE, band)
            if (c.stabilization) {
                b.setCaptureRequestOption(CaptureRequest.CONTROL_VIDEO_STABILIZATION_MODE,
                    if (pro.stabilization) CameraMetadata.CONTROL_VIDEO_STABILIZATION_MODE_ON
                    else CameraMetadata.CONTROL_VIDEO_STABILIZATION_MODE_OFF)
            }
            Camera2CameraControl.from(cam.cameraControl).setCaptureRequestOptions(b.build())
            if (c.evMax > c.evMin) {
                cam.cameraControl.setExposureCompensationIndex(pro.evIndex.coerceIn(c.evMin, c.evMax))
            }
        } catch (e: Exception) {
            Log.w(TAG, "applyPro failed: $e")
        }
    }

    /**
     * Approximates white-balance gains for a colour temperature. Sensor response differs per
     * device, so the last auto-WB gains are used as the anchor (treated as ~5000 K daylight).
     */
    private fun gainsForKelvin(k: Int): RggbChannelVector {
        val base = autoGains ?: RggbChannelVector(2.0f, 1f, 1f, 1.7f)
        val ref = illuminant(5000)
        val cur = illuminant(k)
        val r = base.red * (ref[0] / ref[1]) / (cur[0] / cur[1])
        val bl = base.blue * (ref[2] / ref[1]) / (cur[2] / cur[1])
        return RggbChannelVector(r.coerceIn(1f, 6f), base.greenEven, base.greenOdd, bl.coerceIn(1f, 6f))
    }

    /** Black-body colour (Tanner Helland's fit), linear-ish RGB in 0..1. */
    private fun illuminant(kelvin: Int): FloatArray {
        val t = kelvin / 100.0
        val r = if (t <= 66) 255.0 else 329.698727446 * (t - 60).pow(-0.1332047592)
        val g = if (t <= 66) 99.4708025861 * ln(t) - 161.1195681661 else 288.1221695283 * (t - 60).pow(-0.0755148492)
        val b = if (t >= 66) 255.0 else if (t <= 19) 0.0 else 138.5177312231 * ln(t - 10) - 305.0447927307
        return floatArrayOf(
            (r.coerceIn(1.0, 255.0) / 255.0).toFloat(),
            (g.coerceIn(1.0, 255.0) / 255.0).toFloat(),
            (b.coerceIn(1.0, 255.0) / 255.0).toFloat(),
        )
    }

    private fun nearestAwbPreset(k: Int, modes: List<Int>): Int? {
        val presets = listOf(
            2850 to CameraMetadata.CONTROL_AWB_MODE_INCANDESCENT,
            3300 to CameraMetadata.CONTROL_AWB_MODE_WARM_FLUORESCENT,
            4000 to CameraMetadata.CONTROL_AWB_MODE_FLUORESCENT,
            5500 to CameraMetadata.CONTROL_AWB_MODE_DAYLIGHT,
            6500 to CameraMetadata.CONTROL_AWB_MODE_CLOUDY_DAYLIGHT,
            7500 to CameraMetadata.CONTROL_AWB_MODE_SHADE,
        ).filter { modes.contains(it.second) }
        return presets.minByOrNull { abs(it.first - k) }?.second
    }
}
