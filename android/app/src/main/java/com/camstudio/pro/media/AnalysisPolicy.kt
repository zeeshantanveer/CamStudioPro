package com.camstudio.pro.media

/** ML runs independently of camera FPS; all returned coordinates refer to the original scene. */
object AnalysisPolicy {
    fun intervalNs(hot: Boolean) = if (hot) 250_000_000L else 100_000_000L
    /**
     * Person segmentation for background replacement runs up to ~30 Hz so edges keep up with
     * movement. When the phone is hot it still follows the 15fps render cap (on one worker): a slow mask is what
     * makes edges trail, so face analysis (4 Hz) and the frame rate (15 fps) give way first.
     */
    fun segmentIntervalNs(hot: Boolean) = if (hot) 60_000_000L else 25_000_000L
    fun toScene(x: Float, y: Float, rotation: Int): BeautyPoint = when (rotation) {
        90 -> BeautyPoint(y, x)
        180 -> BeautyPoint(1f - x, y)
        270 -> BeautyPoint(1f - y, 1f - x)
        else -> BeautyPoint(x, 1f - y)
    }
    fun freshness(ageMs: Float) = ((750f - ageMs) / 300f).coerceIn(0f, 1f)
}
