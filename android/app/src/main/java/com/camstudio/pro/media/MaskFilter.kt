package com.camstudio.pro.media

import java.nio.ByteBuffer
import kotlin.math.abs

/** Average colour of the real room behind the person, and how much of the frame the person covers. */
data class RoomStats(val r: Float, val g: Float, val b: Float, val coverage: Float, val valid: Boolean) {
    companion object {
        val NONE = RoomStats(0f, 0f, 0f, 1f, false)
    }
}

/** CPU helpers for the person mask. Pure functions, so they are unit tested. */
object MaskFilter {
    /**
     * Motion-adaptive temporal smoothing, in place. Small frame-to-frame differences are
     * segmentation noise (edge shimmer) and are damped; large ones are real movement and are
     * taken immediately, so a moving person is never followed by a lagging edge.
     */
    fun stabilize(mask: ByteArray, prev: ByteArray) {
        for (i in mask.indices) {
            val v = mask[i].toInt() and 0xff
            val p = prev[i].toInt() and 0xff
            val d = abs(v - p)
            if (d >= 48) continue
            // 35 % of the change for tiny flicker, rising to 100 % at a difference of 48.
            val k = 0.35f + 0.65f * d / 48f
            mask[i] = (p + (v - p) * k).toInt().coerceIn(0, 255).toByte()
        }
    }

    /**
     * Mean colour (0..1) of pixels the mask marks as clearly background, and the fraction
     * of the frame covered by the person. [pixels] is RGBA, same size and orientation as [mask].
     */
    fun roomStats(pixels: ByteBuffer, mask: ByteArray, w: Int, h: Int, maskW: Int = w, maskH: Int = h): RoomStats {
        var sr = 0L
        var sg = 0L
        var sb = 0L
        var bgCount = 0
        var personCount = 0
        var total = 0
        var y = 0
        while (y < h) {
            var x = 0
            while (x < w) {
                val i = y * w + x
                val mi = (y * maskH / h).coerceAtMost(maskH - 1) * maskW + (x * maskW / w).coerceAtMost(maskW - 1)
                val m = mask[mi].toInt() and 0xff
                total++
                if (m > 128) personCount++
                if (m < 64) {
                    val o = i * 4
                    sr += pixels.get(o).toInt() and 0xff
                    sg += pixels.get(o + 1).toInt() and 0xff
                    sb += pixels.get(o + 2).toInt() and 0xff
                    bgCount++
                }
                x += 2
            }
            y += 2
        }
        val coverage = if (total == 0) 1f else personCount.toFloat() / total
        // Too little visible room to trust the colour estimate.
        if (bgCount == 0 || bgCount * 20 < total) return RoomStats(0f, 0f, 0f, coverage, false)
        val n = bgCount * 255f
        return RoomStats(sr / n, sg / n, sb / n, coverage, true)
    }

    /**
     * Gain that makes a virtual background look lit like the real room: brightness and white
     * balance move toward the room's, by [strength] (0..1). Clamped so a dark or strongly
     * coloured real wall can't push the background to an extreme.
     */
    fun matchGain(room: FloatArray, bg: FloatArray, strength: Float): FloatArray {
        fun luma(c: FloatArray) = (0.2126f * c[0] + 0.7152f * c[1] + 0.0722f * c[2]).coerceAtLeast(0.02f)
        val lr = luma(room)
        val lb = luma(bg)
        val lumGain = (lr / lb).coerceIn(0.55f, 1.8f)
        val s = strength.coerceIn(0f, 1f)
        return FloatArray(3) { i ->
            val wb = ((room[i].coerceAtLeast(0.01f) / lr) / (bg[i].coerceAtLeast(0.01f) / lb)).coerceIn(0.75f, 1.33f)
            (1f + (lumGain - 1f) * s * 0.8f) * (1f + (wb - 1f) * s * 0.7f)
        }
    }

    /** Grain amplitude (0..1 colour units) for the current sensor ISO; [amount] 0..1, 0.5 = natural. */
    fun grainSigma(iso: Int?, amount: Float): Float {
        val base = 0.004f + ((iso ?: 200).coerceIn(50, 6400) / 3200f) * 0.028f
        return (base * amount * 2f).coerceIn(0f, 0.06f)
    }
}
