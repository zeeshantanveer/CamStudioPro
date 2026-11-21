package com.camstudio.pro.media

/** A mask is valid only for the exact retained camera frame it was inferred from. */
object MaskFrameSync {
    fun slot(frameTag: Long, retained: LongArray): Int = if (frameTag < 0) -1 else retained.indexOf(frameTag)
    /** Motion must compare the mask's exact source frame with older geometry-matched data. */
    fun previousSlot(frameTag: Long, retained: LongArray): Int =
        if (frameTag < 0) -1 else retained.indices
            .filter { retained[it] >= 0 && retained[it] < frameTag }
            .maxByOrNull { retained[it] } ?: -1
    fun delayMs(current: Long, source: Long, frameMs: Float): Int =
        ((current - source).coerceAtLeast(0) * frameMs).toInt().coerceIn(0, 250)
}
