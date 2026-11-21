package com.camstudio.pro.data

import org.json.JSONObject

data class BeautyControl(val key: String, val label: String, val min: Int = 0, val max: Int = 100)
val BEAUTY_CONTROLS = listOf(
    BeautyControl("faceSlim", "Face slimming"), BeautyControl("jawSlim", "Jaw slimming"),
    BeautyControl("chinLength", "Chin length", -100), BeautyControl("noseWidth", "Nose narrowing", -100),
    BeautyControl("noseBridge", "Nose bridge slimming", -100),
    BeautyControl("noseAlar", "Nose alar narrowing", -100), BeautyControl("noseLength", "Nose length", -100),
    BeautyControl("noseRadix", "Nose root / radix lift", -100), BeautyControl("noseTip", "Nose tip lift", -100),
    BeautyControl("noseContour", "Nose contour shading"),
    BeautyControl("smile", "Smile lift"), BeautyControl("eyeSize", "Eye opening"),
    BeautyControl("lipFullness", "Lip fullness", -100), BeautyControl("skinSmooth", "Skin smoothing"),
    BeautyControl("skinBrightness", "Skin brightness"),
)

/** Wire format v1. Positive width values narrow; positive length/lift values lengthen/lift. */
data class BeautyState(
    val enabled: Boolean = false,
    val faceSlim: Int = 0,
    val jawSlim: Int = 0,
    val chinLength: Int = 0,
    val noseWidth: Int = 0,
    val noseAlar: Int = 0,
    val noseLength: Int = 0,
    val noseRadix: Int = 0,
    val noseTip: Int = 0,
    val smile: Int = 0,
    val eyeSize: Int = 0,
    val lipFullness: Int = 0,
    val skinSmooth: Int = 0,
    val skinBrightness: Int = 0,
    /** Narrows (positive) or widens the upper nose between the eyes. */
    val noseBridge: Int = 0,
    /** Makeup-style contour: slightly darker bridge sides, slightly brighter ridge. */
    val noseContour: Int = 0,
) {
    fun toJson() = JSONObject().put("enabled", enabled).apply {
        values().forEach { (key, value) -> put(key, value) }
    }
    fun values() = linkedMapOf("faceSlim" to faceSlim, "jawSlim" to jawSlim, "chinLength" to chinLength,
        "noseWidth" to noseWidth, "noseAlar" to noseAlar, "noseLength" to noseLength,
        "noseRadix" to noseRadix, "noseTip" to noseTip, "smile" to smile, "eyeSize" to eyeSize,
        "lipFullness" to lipFullness, "skinSmooth" to skinSmooth, "skinBrightness" to skinBrightness,
        "noseBridge" to noseBridge, "noseContour" to noseContour)

    /** Merge only recognized, correctly typed keys. This prevents simultaneous editors losing other fields. */
    fun patch(changes: JSONObject): BeautyState {
        val merged = toJson()
        (values().keys + "enabled").forEach { key ->
            val value = changes.opt(key)
            if (key == "enabled" && value is Boolean) merged.put(key, value)
            if (key != "enabled" && value is Number && value.toDouble().isFinite()) merged.put(key, value.toInt())
        }
        return fromJson(merged)
    }
    companion object {
        fun fromJson(j: JSONObject): BeautyState {
            fun strength(key: String) = j.optInt(key).coerceIn(0, 100)
            fun signed(key: String) = j.optInt(key).coerceIn(-100, 100)
            return BeautyState(j.optBoolean("enabled"), strength("faceSlim"), strength("jawSlim"),
                signed("chinLength"), signed("noseWidth"), signed("noseAlar"), signed("noseLength"),
                signed("noseRadix"), signed("noseTip"), strength("smile"), strength("eyeSize"),
                signed("lipFullness"), strength("skinSmooth"), strength("skinBrightness"),
                signed("noseBridge"), strength("noseContour"))
        }
    }
}
