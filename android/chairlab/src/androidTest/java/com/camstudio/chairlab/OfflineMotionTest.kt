package com.camstudio.chairlab

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.Matrix
import android.os.SystemClock
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.android.gms.tasks.Tasks
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.segmentation.Segmentation
import com.google.mlkit.vision.segmentation.selfie.SelfieSegmenterOptions
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.nio.ByteOrder
import java.util.concurrent.TimeUnit

/** Opt-in offline evaluation of local recorded frames. Never opens a camera or network. */
@RunWith(AndroidJUnit4::class)
class OfflineMotionTest {
    @Test fun exportPersonMasksForMotionReview() {
        assumeTrue(InstrumentationRegistry.getArguments().getString("motionReview") == "true")
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val root = File(context.getExternalFilesDir(null), "motion-review")
        val inputs = File(root, "frames").listFiles { f -> f.extension == "jpg" }
            ?.sortedBy { it.nameWithoutExtension.toInt() } ?: error("Missing local review frames")
        require(inputs.isNotEmpty())
        val output = File(root, "person").apply { mkdirs() }
        val model = Segmentation.getClient(SelfieSegmenterOptions.Builder()
            .setDetectorMode(SelfieSegmenterOptions.STREAM_MODE).build())
        val timings = JSONArray()
        try {
            inputs.forEachIndexed { index, file ->
                val source = requireNotNull(BitmapFactory.decodeFile(file.path))
                // Saved Chair Lab images are sideways; infer upright, then map back exactly.
                val upright = Bitmap.createBitmap(source, 0, 0, source.width, source.height,
                    Matrix().apply { postRotate(90f) }, true)
                val start = SystemClock.elapsedRealtimeNanos()
                val mask = Tasks.await(model.process(InputImage.fromBitmap(upright, 0)), 10, TimeUnit.SECONDS)
                val ms = (SystemClock.elapsedRealtimeNanos() - start) / 1e6
                val values = FloatArray(mask.width * mask.height)
                mask.buffer.order(ByteOrder.nativeOrder()).asFloatBuffer().get(values)
                require(values.all { it.isFinite() && it in 0f..1f })
                val pixels = IntArray(values.size) { i ->
                    val v = (values[i] * 255).toInt().coerceIn(0, 255)
                    Color.rgb(v, v, v)
                }
                val uprightMask = Bitmap.createBitmap(pixels, mask.width, mask.height, Bitmap.Config.ARGB_8888)
                val restored = Bitmap.createBitmap(uprightMask, 0, 0, uprightMask.width, uprightMask.height,
                    Matrix().apply { postRotate(-90f) }, true)
                require(restored.width == source.width && restored.height == source.height)
                File(output, "${file.nameWithoutExtension}.png").outputStream().use {
                    check(restored.compress(Bitmap.CompressFormat.PNG, 100, it))
                }
                timings.put(JSONObject().put("frame", index).put("segmentationMs", ms))
                source.recycle(); upright.recycle(); uprightMask.recycle(); restored.recycle()
            }
            File(output, "report.json").writeText(JSONObject()
                .put("model", "ML Kit selfie 16.0.0-beta6 STREAM_MODE")
                .put("scope", "Offline saved-preview images, upright inference; not production GL output")
                .put("frames", timings).toString(2))
        } finally { model.close() }
    }
}
