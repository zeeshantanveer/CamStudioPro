package com.camstudio.chairseg

import android.content.Context
import android.graphics.Bitmap
import android.os.SystemClock
import com.google.mediapipe.framework.image.BitmapImageBuilder
import com.google.mediapipe.framework.image.ByteBufferExtractor
import com.google.mediapipe.framework.image.MPImage
import com.google.mediapipe.tasks.components.containers.NormalizedKeypoint
import com.google.mediapipe.tasks.core.BaseOptions
import com.google.mediapipe.tasks.core.Delegate
import com.google.mediapipe.tasks.vision.interactivesegmenter.InteractiveSegmenter
import com.google.mediapipe.tasks.vision.interactivesegmenter.InteractiveSegmenterOptions
import com.google.mediapipe.tasks.vision.interactivesegmenter.Stroke
import java.nio.ByteOrder

/** Create, use and close on one worker thread (also required by the GPU delegate). */
class ChairSelector(context: Context, gpu: Boolean = false): AutoCloseable {
    data class Prompt(val x: Float,val y: Float,val keep: Boolean)
    data class Selection(val mask: ChairMask,val setImageMs: Double,val segmentMs: Double)
    private val worker = Thread.currentThread().id
    private val segmenter = InteractiveSegmenter.createFromOptions(context,
        InteractiveSegmenterOptions.builder().setBaseOptions(BaseOptions.builder()
            .setModelAssetPath("interactive_segmentation.task")
            .setDelegate(if(gpu) Delegate.GPU else Delegate.CPU).build()).build())
    private var image: MPImage? = null
    private var pinned: Bitmap? = null

    fun select(bitmap: Bitmap,prompts: List<Prompt>): Selection {
        check(Thread.currentThread().id == worker)
        require(prompts.any { it.keep }) { "Tap the chair to add a positive selection first." }
        val start = SystemClock.elapsedRealtimeNanos()
        if (bitmap !== pinned) {
            val next = BitmapImageBuilder(bitmap).build()
            segmenter.setImage(next)
            image?.close(); image = next; pinned = bitmap
        }
        val set = SystemClock.elapsedRealtimeNanos()
        val strokes = prompts.map { p -> Stroke.builder()
            .setBrushMode(if(p.keep) Stroke.BrushMode.POSITIVE else Stroke.BrushMode.NEGATIVE)
            .setPoints(listOf(NormalizedKeypoint.create(p.x,p.y))).setCompleted(true).build() }
        val result = segmenter.segment(strokes)
        try {
            val floats = ByteBufferExtractor.extract(result).order(ByteOrder.nativeOrder()).asFloatBuffer()
            val values = FloatArray(result.width*result.height)
            floats.get(values)
            check(values.all { it.isFinite() && it in 0f..1f }) { "Model returned invalid mask confidences." }
            return Selection(ChairMask(result.width,result.height,values),(set-start)/1e6,
                (SystemClock.elapsedRealtimeNanos()-set)/1e6)
        } finally { result.close() }
    }
    override fun close() {
        check(Thread.currentThread().id == worker)
        image?.close(); segmenter.close()
    }
}
