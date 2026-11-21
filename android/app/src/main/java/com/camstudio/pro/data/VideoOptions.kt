package com.camstudio.pro.data

import org.json.JSONObject

/** Validated desktop video edits; unrelated exposure, beauty and audio settings survive. */
data class VideoOptions(val resolution: Int, val background: Int, val natural: Boolean, val changeQuality: Boolean = true) {
    fun error(hasPhoto: Boolean, hasRoom: Boolean): String = when {
        changeQuality && resolution !in listOf(720, 1080) -> "Choose 720p or 1080p."
        background !in listOf(Bg.NONE, Bg.BLUR, Bg.OFFICE, Bg.LIBRARY, Bg.UPLOAD, Bg.ROOM) -> "Unsupported background."
        background == Bg.UPLOAD && !hasPhoto -> "Choose a photo in the phone app first."
        background == Bg.ROOM && !hasRoom -> "Capture My room in the phone app first."
        else -> ""
    }
    fun settings(current: AppSettings) = if (!changeQuality) current else current.copy(resolution = resolution, fps = 30,
        bitrateKbps = maxOf(current.bitrateKbps, if (resolution == 1080) 8000 else 4000), preset = "")
    fun effects(current: FxState) = current.copy(bg = background, edge = 85,
        depth = if (natural) 35 else 0, matchLight = if (natural) 65 else 0,
        grain = if (natural) 30 else 0, lightWrap = if (natural) 20 else 0)
    companion object {
        fun fromJson(j: JSONObject, settings: AppSettings, fx: FxState) = VideoOptions(
            j.optInt("resolution", settings.resolution), j.optInt("background", fx.bg), j.optBoolean("natural", true), j.has("resolution"))
    }
}
