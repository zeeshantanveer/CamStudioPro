package com.camstudio.pro.data

import org.json.JSONObject

data class AppSettings(
    val resolution: Int = 1080,          // 720, 1080, 2160 (short side)
    val fps: Int = 30,
    val bitrateKbps: Int = 8000,         // cap when adaptive, fixed otherwise
    val adaptive: Boolean = true,
    val orientation: String = "landscape", // landscape | portrait | auto
    val micEnabled: Boolean = true,
    val noiseSuppression: Boolean = true,
    val lowLatency: Boolean = true,
    val screenOff: Boolean = false,
    val overheatProtect: Boolean = true,
    val preset: String = "Video meetings",
    /** Reconnect to the last computer automatically, and keep waiting for it in the background. */
    val autoConnect: Boolean = true,
    /** App colours: "system" follows the phone, or "light" / "dark". */
    val theme: String = "system",
    /** The camera used last (back or front), so the app reopens on it. */
    val rearCamera: Boolean = false,
) {
    fun toJson(): JSONObject = JSONObject()
        .put("resolution", resolution).put("fps", fps).put("bitrateKbps", bitrateKbps)
        .put("adaptive", adaptive).put("orientation", orientation).put("micEnabled", micEnabled)
        .put("noiseSuppression", noiseSuppression).put("lowLatency", lowLatency)
        .put("screenOff", screenOff).put("overheatProtect", overheatProtect).put("preset", preset)
        .put("autoConnect", autoConnect).put("theme", theme).put("rearCamera", rearCamera)

    companion object {
        fun fromJson(j: JSONObject): AppSettings {
            val d = AppSettings()
            return AppSettings(
                j.optInt("resolution", d.resolution), j.optInt("fps", d.fps),
                j.optInt("bitrateKbps", d.bitrateKbps), j.optBoolean("adaptive", d.adaptive),
                j.optString("orientation", d.orientation), j.optBoolean("micEnabled", d.micEnabled),
                j.optBoolean("noiseSuppression", d.noiseSuppression), j.optBoolean("lowLatency", d.lowLatency),
                j.optBoolean("screenOff", d.screenOff), j.optBoolean("overheatProtect", d.overheatProtect),
                j.optString("preset", d.preset), j.optBoolean("autoConnect", d.autoConnect),
                j.optString("theme", d.theme).takeIf { it in THEMES } ?: d.theme,
                j.optBoolean("rearCamera", d.rearCamera),
            )
        }
    }
}

val THEMES = listOf("system", "light", "dark")

object Bg {
    const val NONE = 0
    const val BLUR = 1
    const val OFFICE = 2
    const val LIBRARY = 3
    const val UPLOAD = 4
    const val VIDEO = 5   // looping video chosen by the user
    const val ROOM = 6    // "Capture my room": a clean photo of the user's own empty room
}

val LOOK_NAMES = listOf("Natural", "Warm", "Cool", "Mono", "Vivid", "Film")

/** Sliders are 0..100 (strengths) or -100..100 (adjustments). */
data class FxState(
    val bg: Int = Bg.NONE,
    val blurStrength: Int = 70,
    val edge: Int = 85,
    val look: Int = 0,
    val smooth: Int = 0,
    val brightness: Int = 0,
    val contrast: Int = 0,
    val saturation: Int = 0,
    val warmth: Int = 0,
    val sharpness: Int = 0,
    val autoFrame: Boolean = false,
    val lowLight: Boolean = false,
    val uploadUri: String? = null,
    val beauty: BeautyState = BeautyState(),
    val videoUri: String? = null,
    /** Changes every time the room is (re)captured, so the new photo is loaded. 0 = none yet. */
    val roomVersion: Long = 0L,
    // Realism of a replaced background, 0..100.
    val depth: Int = 30,
    val matchLight: Int = 60,
    val grain: Int = 50,
    val lightWrap: Int = 20,
) {
    val activeCount: Int
        get() = listOf(
            bg != Bg.NONE, look != 0, smooth > 0,
            brightness != 0 || contrast != 0 || saturation != 0 || warmth != 0 || sharpness != 0,
            autoFrame, lowLight, beauty.enabled && beauty.values().values.any { it != 0 },
        ).count { it }

    fun toJson(): JSONObject = JSONObject()
        .put("bg", bg).put("blurStrength", blurStrength).put("edge", edge).put("look", look)
        .put("smooth", smooth).put("brightness", brightness).put("contrast", contrast)
        .put("saturation", saturation).put("warmth", warmth).put("sharpness", sharpness)
        .put("autoFrame", autoFrame).put("lowLight", lowLight).put("uploadUri", uploadUri ?: "")
        .put("beauty", beauty.toJson())
        .put("videoUri", videoUri ?: "").put("roomVersion", roomVersion)
        .put("depth", depth).put("matchLight", matchLight).put("grain", grain).put("lightWrap", lightWrap)

    companion object {
        fun fromJson(j: JSONObject): FxState {
            val d = FxState()
            return FxState(
                j.optInt("bg", d.bg), j.optInt("blurStrength", d.blurStrength), j.optInt("edge", d.edge),
                j.optInt("look", d.look), j.optInt("smooth", d.smooth), j.optInt("brightness", d.brightness),
                j.optInt("contrast", d.contrast), j.optInt("saturation", d.saturation), j.optInt("warmth", d.warmth),
                j.optInt("sharpness", d.sharpness), j.optBoolean("autoFrame", d.autoFrame),
                j.optBoolean("lowLight", d.lowLight), j.optString("uploadUri").ifEmpty { null },
                j.optJSONObject("beauty")?.let { BeautyState.fromJson(it) } ?: BeautyState(),
                j.optString("videoUri").ifEmpty { null }, j.optLong("roomVersion", 0L),
                j.optInt("depth", d.depth), j.optInt("matchLight", d.matchLight),
                j.optInt("grain", d.grain), j.optInt("lightWrap", d.lightWrap),
            )
        }
    }
}

data class ProState(
    val isoManual: Boolean = false,
    val iso: Int = 400,
    val shutterManual: Boolean = false,
    val shutterNs: Long = 16_666_667L,
    val wbManual: Boolean = false,
    val kelvin: Int = 5200,
    val focusManual: Boolean = false,
    val focus: Float = 0f,               // 0 = infinity .. 1 = closest
    val evIndex: Int = 0,
    val locked: Boolean = false,
    val peaking: Boolean = false,
    val zebra: Boolean = false,
    val stabilization: Boolean = false,
    val antiFlicker: Int = 50,
) {
    val anyManual: Boolean get() = isoManual || shutterManual || wbManual || focusManual || evIndex != 0

    fun toJson(): JSONObject = JSONObject()
        .put("isoManual", isoManual).put("iso", iso).put("shutterManual", shutterManual)
        .put("shutterNs", shutterNs).put("wbManual", wbManual).put("kelvin", kelvin)
        .put("focusManual", focusManual).put("focus", focus.toDouble()).put("evIndex", evIndex)
        .put("peaking", peaking).put("zebra", zebra).put("stabilization", stabilization)
        .put("antiFlicker", antiFlicker)

    companion object {
        fun fromJson(j: JSONObject): ProState {
            val d = ProState()
            return ProState(
                j.optBoolean("isoManual"), j.optInt("iso", d.iso), j.optBoolean("shutterManual"),
                j.optLong("shutterNs", d.shutterNs), j.optBoolean("wbManual"), j.optInt("kelvin", d.kelvin),
                j.optBoolean("focusManual"), j.optDouble("focus", 0.0).toFloat(), j.optInt("evIndex", 0),
                false, j.optBoolean("peaking"), j.optBoolean("zebra"), j.optBoolean("stabilization"),
                j.optInt("antiFlicker", d.antiFlicker),
            )
        }
    }
}

data class SavedPc(
    val id: String,
    val name: String,
    val os: String,
    val host: String,
    val port: Int,
    val lastUsed: Long,
    /** How the last connection was made: "wifi" or "usb" (adb reverse, host 127.0.0.1). */
    val transport: String = "wifi",
    /** Last Wi-Fi address, kept when a later connection used USB. */
    val wifiHost: String = if (transport == "usb") "" else host,
) {
    fun toJson(): JSONObject = JSONObject().put("id", id).put("name", name).put("os", os)
        .put("host", host).put("port", port).put("lastUsed", lastUsed)
        .put("transport", transport).put("wifiHost", wifiHost)

    companion object {
        fun fromJson(j: JSONObject) = SavedPc(
            j.optString("id"), j.optString("name"), j.optString("os"), j.optString("host"),
            j.optInt("port", 47801), j.optLong("lastUsed"), j.optString("transport", "wifi"),
            j.optString("wifiHost").ifEmpty { if (j.optString("transport") == "usb") "" else j.optString("host") },
        )
    }
}

data class Preset(
    val name: String,
    val builtIn: Boolean,
    val resolution: Int,
    val fps: Int,
    val bitrateKbps: Int,
    val adaptive: Boolean,
    val lowLatency: Boolean,
    val pro: ProState? = null,
) {
    val summary: String
        get() = "${resLabel(resolution)}$fps · H.264 · " + if (lowLatency) "low latency" else "high quality"

    fun toJson(): JSONObject = JSONObject().put("name", name).put("resolution", resolution).put("fps", fps)
        .put("bitrateKbps", bitrateKbps).put("adaptive", adaptive).put("lowLatency", lowLatency)
        .apply { pro?.let { put("pro", it.toJson()) } }

    companion object {
        fun fromJson(j: JSONObject) = Preset(
            j.optString("name"), false, j.optInt("resolution", 1080), j.optInt("fps", 30),
            j.optInt("bitrateKbps", 8000), j.optBoolean("adaptive", true), j.optBoolean("lowLatency", true),
            j.optJSONObject("pro")?.let { ProState.fromJson(it) },
        )

        val BUILT_IN = listOf(
            Preset("Video meetings", true, 1080, 30, 8000, true, true),
            Preset("Streaming", true, 1080, 60, 12000, true, false),
            Preset("Low bandwidth", true, 720, 30, 2500, true, true),
        )
    }
}

fun resLabel(res: Int): String = when (res) {
    2160 -> "4K"
    else -> "${res}p"
}

fun resLongName(res: Int): String = when (res) {
    720 -> "720p HD"
    1080 -> "1080p Full HD"
    2160 -> "4K Ultra HD"
    else -> "${res}p"
}
