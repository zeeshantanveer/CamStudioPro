package com.camstudio.chairseg

import kotlin.math.max
import kotlin.math.min

/** The chair contributes foreground only. It never reduces the existing person confidence. */
class ChairMask(val width: Int, val height: Int, val values: FloatArray) {
    init { require(width > 0 && height > 0 && values.size == width * height) }

    fun sample(x: Float, y: Float): Float = values[(y.coerceIn(0f, .999999f)*height).toInt()*width +
        (x.coerceIn(0f, .999999f)*width).toInt()].coerceIn(0f,1f)

    fun brush(x: Float, y: Float, radius: Float, keep: Boolean) {
        val cx = x * width; val cy = y * height
        val r = max(1f, radius * min(width,height))
        for (yy in max(0,(cy-r).toInt())..min(height-1,(cy+r).toInt()))
            for (xx in max(0,(cx-r).toInt())..min(width-1,(cx+r).toInt()))
                if ((xx-cx)*(xx-cx)+(yy-cy)*(yy-cy) <= r*r) values[yy*width+xx] = if (keep) 1f else 0f
    }

    companion object {
        fun union(person: Float, chair: Float) = max(person.coerceIn(0f,1f),chair.coerceIn(0f,1f))
        /** Normalize a tap in a FIT_CENTER view; letterbox taps have no image coordinates. */
        fun point(x: Float,y: Float,vw: Int,vh: Int,iw: Int,ih: Int): Pair<Float,Float>? {
            if (minOf(vw,vh,iw,ih) <= 0) return null
            val s = min(vw.toFloat()/iw,vh.toFloat()/ih)
            val nx = (x-(vw-iw*s)/2)/(iw*s); val ny = (y-(vh-ih*s)/2)/(ih*s)
            return if (nx in 0f..1f && ny in 0f..1f) nx to ny else null
        }
    }
}
