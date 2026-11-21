package com.camstudio.pro.media

import android.graphics.Bitmap
import android.graphics.RectF
import android.util.Log
import com.google.android.gms.tasks.Tasks
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.face.FaceDetection
import com.google.mlkit.vision.face.FaceDetector
import com.google.mlkit.vision.face.FaceDetectorOptions
import com.google.mlkit.vision.face.FaceContour
import com.google.mlkit.vision.segmentation.Segmentation
import com.google.mlkit.vision.segmentation.Segmenter
import com.google.mlkit.vision.segmentation.selfie.SelfieSegmenterOptions
import java.nio.ByteBuffer
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Runs person segmentation, face detection and the luma histogram on a small RGBA
 * readback of the processed frame. One job at a time; the GL thread skips readbacks
 * while [busy] is set.
 */
class Analyzer(private val cb: Callback) {
    interface Callback {
        /**
         * Confidence mask, row 0 = top of the image, one byte per pixel (temporally stabilised).
         * [stats] describes the real room behind the person in the same frame.
         */
        fun onMask(mask: ByteArray, w: Int, h: Int, stats: RoomStats, frameTag: Long)
        /** Largest face in normalized image coordinates (origin top-left), or null. */
        fun onFace(face: RectF?)
        fun onBeauty(landmarks: BeautyLandmarks?, generation: Long)
        fun onHistogram(bins: FloatArray)
    }

    val busy = AtomicBoolean(false)
    private val exec = Executors.newSingleThreadExecutor { r -> Thread(r, "csp-analyzer") }
    private var segmenter: Segmenter? = null
    private var faces: FaceDetector? = null
    private var basicFaces: FaceDetector? = null
    private var bitmap: Bitmap? = null
    private var maskBytes: ByteArray? = null
    private var faceRotation = 0
    private var lastGeneration = -1L

    /** [frameTag] identifies the rendered frame the pixels came from; it is passed back with the mask. */
    fun submit(pixels: ByteBuffer, w: Int, h: Int, wantSeg: Boolean, wantFace: Boolean, wantHist: Boolean, generation: Long, wantBeauty: Boolean, frameTag: Long = 0L) {
        exec.execute {
            try {
                run(pixels, w, h, wantSeg, wantFace, wantHist, generation, wantBeauty, frameTag)
            } catch (e: Throwable) {
                Log.w("Analyzer", "analysis failed: $e")
                cb.onBeauty(null, generation)
            } finally {
                busy.set(false)
            }
        }
    }

    private fun run(pixels: ByteBuffer, w: Int, h: Int, wantSeg: Boolean, wantFace: Boolean, wantHist: Boolean, generation: Long, wantBeauty: Boolean, frameTag: Long) {
        val capturedAt = System.nanoTime()
        if (wantHist) {
            val bins = FloatArray(64)
            pixels.position(0)
            val n = w * h
            var i = 0
            while (i < n) {
                val o = i * 4
                val r = pixels.get(o).toInt() and 0xff
                val g = pixels.get(o + 1).toInt() and 0xff
                val b = pixels.get(o + 2).toInt() and 0xff
                val y = (r * 54 + g * 183 + b * 19) shr 8
                bins[y shr 2] += 1f
                i += 3
            }
            val max = bins.maxOrNull() ?: 0f
            if (max > 0f) for (k in bins.indices) bins[k] /= max
            cb.onHistogram(bins)
        }
        if (!wantSeg && !wantFace) return

        var bmp = bitmap
        if (bmp == null || bmp.width != w || bmp.height != h) {
            bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
            bitmap = bmp
        }
        pixels.position(0)
        bmp.copyPixelsFromBuffer(pixels)
        val image = InputImage.fromBitmap(bmp, 0)

        if (wantFace) {
            if (lastGeneration != generation) { faceRotation = 0; lastGeneration = generation }
            val det = if (!wantBeauty) basicFaces ?: FaceDetection.getClient(
                FaceDetectorOptions.Builder().setPerformanceMode(FaceDetectorOptions.PERFORMANCE_MODE_FAST)
                    .setMinFaceSize(.12f).build()
            ).also { basicFaces = it } else faces ?: FaceDetection.getClient(
                FaceDetectorOptions.Builder()
                    .setPerformanceMode(FaceDetectorOptions.PERFORMANCE_MODE_FAST)
                    .setContourMode(FaceDetectorOptions.CONTOUR_MODE_ALL)
                    .setLandmarkMode(FaceDetectorOptions.LANDMARK_MODE_ALL)
                    .setMinFaceSize(0.12f)
                    .build()
            ).also { faces = it }
            val rotation = faceRotation
            val faceImage = InputImage.fromBitmap(bmp, rotation)
            val list = Tasks.await(det.process(faceImage), 2, TimeUnit.SECONDS)
            val best = list.maxByOrNull { it.boundingBox.width() * it.boundingBox.height() }
            if (best == null) faceRotation = (rotation + 90) % 360
            val rw = if (rotation % 180 == 0) w else h
            val rh = if (rotation % 180 == 0) h else w
            fun points(type: Int) = best?.getContour(type)?.points?.map {
                AnalysisPolicy.toScene(it.x / rw, it.y / rh, rotation)
            } ?: emptyList()
            cb.onBeauty(best?.let {
                BeautyLandmarks(points(FaceContour.FACE), points(FaceContour.LEFT_EYE), points(FaceContour.RIGHT_EYE),
                    points(FaceContour.NOSE_BRIDGE), points(FaceContour.NOSE_BOTTOM),
                    points(FaceContour.UPPER_LIP_TOP) + points(FaceContour.LOWER_LIP_BOTTOM),
                    it.headEulerAngleY, it.headEulerAngleX, capturedAt)
            }, generation)
            cb.onFace(best?.boundingBox?.let {
                val corners = listOf(AnalysisPolicy.toScene(it.left.toFloat()/rw, it.top.toFloat()/rh, rotation),
                    AnalysisPolicy.toScene(it.right.toFloat()/rw, it.top.toFloat()/rh, rotation),
                    AnalysisPolicy.toScene(it.left.toFloat()/rw, it.bottom.toFloat()/rh, rotation),
                    AnalysisPolicy.toScene(it.right.toFloat()/rw, it.bottom.toFloat()/rh, rotation))
                RectF(corners.minOf { p -> p.x }.coerceIn(0f,1f), (1f-corners.maxOf { p -> p.y }).coerceIn(0f,1f),
                    corners.maxOf { p -> p.x }.coerceIn(0f,1f), (1f-corners.minOf { p -> p.y }).coerceIn(0f,1f))
            })
        }

        if (wantSeg) {
            val seg = segmenter ?: Segmentation.getClient(
                SelfieSegmenterOptions.Builder().setDetectorMode(SelfieSegmenterOptions.STREAM_MODE).enableRawSizeMask().build()
            ).also { segmenter = it }
            val mask = Tasks.await(seg.process(image), 2, TimeUnit.SECONDS)
            val mw = mask.width
            val mh = mask.height
            val fb = mask.buffer.asFloatBuffer()
            var out = maskBytes
            if (out == null || out.size != mw * mh) {
                out = ByteArray(mw * mh)
                maskBytes = out
            }
            for (k in 0 until mw * mh) out[k] = (fb.get(k) * 255f).toInt().coerceIn(0, 255).toByte()
            val stats = MaskFilter.roomStats(pixels, out, w, h, mw, mh)
            cb.onMask(out.copyOf(), mw, mh, stats, frameTag)
        }
    }

    fun release() {
        exec.execute {
            runCatching { segmenter?.close() }
            runCatching { faces?.close() }
            runCatching { basicFaces?.close() }
        }
        exec.shutdown()
    }
}
