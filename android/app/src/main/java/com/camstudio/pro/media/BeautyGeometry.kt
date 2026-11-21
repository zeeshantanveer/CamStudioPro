package com.camstudio.pro.media

import com.camstudio.pro.data.BeautyState
import kotlin.math.*

/** Coordinates are normalized in the unwarped scene, with y increasing upward (GL coordinates). */
data class BeautyPoint(val x: Float, val y: Float)
data class BeautyLandmarks(
    val outline: List<BeautyPoint>, val eyeA: List<BeautyPoint>, val eyeB: List<BeautyPoint>,
    val noseBridge: List<BeautyPoint>, val noseBottom: List<BeautyPoint>, val lips: List<BeautyPoint>,
    val yaw: Float, val pitch: Float,
    val capturedAt: Long = System.nanoTime(),
)
data class BeautyWarp(val x: Float, val y: Float, val rx: Float, val ry: Float, val dx: Float, val dy: Float)

/** Pure math shared by the renderer and JVM checks. Distances are measured in frame-height units. */
class BeautyGeometry private constructor(
    val center: BeautyPoint, val width: Float, val height: Float, val cos: Float, val sin: Float,
    val eyeA: BeautyPoint, val eyeB: BeautyPoint, val root: BeautyPoint, val nose: BeautyPoint,
    val mouthA: BeautyPoint, val mouthB: BeautyPoint, val mouth: BeautyPoint, val chin: BeautyPoint,
    val aspect: Float, val confidence: Float,
) {
    fun smooth(previous: BeautyGeometry?): BeautyGeometry {
        if (previous == null || previous.aspect != aspect ||
            hypot((center.x - previous.center.x) * aspect, center.y - previous.center.y) > width * .65f) return this
        fun f(a: Float, b: Float) = a * .5f + b * .5f
        fun p(a: BeautyPoint, b: BeautyPoint) = BeautyPoint(f(a.x, b.x), f(a.y, b.y))
        val cx = f(previous.cos, cos)
        val sx = f(previous.sin, sin)
        val n = hypot(cx, sx).coerceAtLeast(.001f)
        return BeautyGeometry(p(previous.center, center), f(previous.width, width), f(previous.height, height),
            cx / n, sx / n, p(previous.eyeA, eyeA), p(previous.eyeB, eyeB), p(previous.root, root),
            p(previous.nose, nose), p(previous.mouthA, mouthA), p(previous.mouthB, mouthB),
            p(previous.mouth, mouth), p(previous.chin, chin), aspect, confidence)
    }

    fun warps(s: BeautyState): List<BeautyWarp> {
        val out = ArrayList<BeautyWarp>(MAX_WARPS)
        fun move(p: BeautyPoint, rx: Float, ry: Float, x: Float, y: Float) {
            if (abs(x) + abs(y) < .000001f) return
            out += BeautyWarp(p.x, p.y, rx, ry, (cos * x - sin * y) / aspect, sin * x + cos * y)
        }
        fun at(x: Float, y: Float) = BeautyPoint(center.x + (cos * x - sin * y) / aspect, center.y + sin * x + cos * y)
        fun amount(v: Int) = v.coerceIn(-100, 100) / 100f
        for (side in listOf(-1f, 1f)) {
            move(at(side * width * .40f, -height * .04f), width * .42f, height * .40f,
                -side * width * .065f * amount(s.faceSlim), 0f)
            move(at(side * width * .32f, -height * .28f), width * .30f, height * .28f,
                -side * width * .06f * amount(s.jawSlim), 0f)
            val np = BeautyPoint(nose.x + side * cos * width * .09f / aspect, nose.y + side * sin * width * .09f)
            move(np, width * .17f, height * .18f, -side * width * .025f * amount(s.noseWidth), 0f)
            val alar = BeautyPoint(nose.x + side * cos * width * .115f / aspect, nose.y + side * sin * width * .115f)
            move(alar, width * .12f, height * .09f, -side * width * .022f * amount(s.noseAlar), 0f)
            // Bridge: a tall, narrow region on each side of the upper nose, a third of the way from
            // the root (between the eyes) to the base. Kept clear of the inner eye corners.
            val mid = BeautyPoint(root.x + (nose.x - root.x) * .38f, root.y + (nose.y - root.y) * .38f)
            val bridge = BeautyPoint(mid.x + side * cos * width * .065f / aspect, mid.y + side * sin * width * .065f)
            move(bridge, width * .06f, height * .17f, -side * width * .018f * amount(s.noseBridge), 0f)
        }
        move(chin, width * .32f, height * .23f, 0f, -height * .045f * amount(s.chinLength))
        move(nose, width * .20f, height * .19f, 0f, -height * .035f * amount(s.noseLength))
        move(root, width * .14f, height * .15f, 0f, height * .025f * amount(s.noseRadix))
        move(nose, width * .12f, height * .10f, 0f, height * .022f * amount(s.noseTip))
        move(mouthA, width * .18f, height * .13f, -width * .01f * amount(s.smile), height * .025f * amount(s.smile))
        move(mouthB, width * .18f, height * .13f, width * .01f * amount(s.smile), height * .025f * amount(s.smile))
        // Eye and lip expansion use paired control points, not a whole-frame stretch.
        for (eye in listOf(eyeA, eyeB)) {
            val upper = BeautyPoint(eye.x - sin * height * .035f / aspect, eye.y + cos * height * .035f)
            move(upper, width * .18f, height * .10f, 0f, height * .018f * amount(s.eyeSize))
        }
        for (side in listOf(-1f, 1f)) {
            val p = BeautyPoint(mouth.x - sin * side * height * .025f / aspect, mouth.y + cos * side * height * .025f)
            move(p, width * .23f, height * .065f, 0f, side * height * .012f * amount(s.lipFullness))
        }
        return out.take(MAX_WARPS)
    }

    companion object {
        const val MAX_WARPS = 20
        fun from(l: BeautyLandmarks, aspect: Float): BeautyGeometry? {
            if (l.outline.size < 10 || l.eyeA.isEmpty() || l.eyeB.isEmpty() ||
                l.noseBridge.isEmpty() || l.noseBottom.isEmpty() || l.lips.size < 4 || aspect <= 0) return null
            val all = l.outline + l.eyeA + l.eyeB + l.noseBridge + l.noseBottom + l.lips
            if (all.any { !it.x.isFinite() || !it.y.isFinite() || it.x !in -.1f..1.1f || it.y !in -.1f..1.1f }) return null
            fun mean(points: List<BeautyPoint>) = BeautyPoint(points.map { it.x }.average().toFloat(), points.map { it.y }.average().toFloat())
            val eyes = listOf(mean(l.eyeA), mean(l.eyeB)).sortedBy { it.x }
            val dx = (eyes[1].x - eyes[0].x) * aspect
            val dy = eyes[1].y - eyes[0].y
            val distance = hypot(dx, dy)
            if (distance < .02f) return null
            var c = dx / distance
            var s = dy / distance
            val mouthCenter = mean(l.lips)
            val eyeCenter = mean(eyes)
            // Resolve the eye-axis ambiguity using the mouth, including +/-90 and 180 degree scenes.
            if (-(mouthCenter.x-eyeCenter.x)*aspect*s + (mouthCenter.y-eyeCenter.y)*c > 0) {
                c = -c; s = -s
            }
            val center = mean(l.outline)
            fun local(p: BeautyPoint) = BeautyPoint((p.x - center.x) * aspect * c + (p.y - center.y) * s,
                -(p.x - center.x) * aspect * s + (p.y - center.y) * c)
            val outline = l.outline.map(::local)
            val width = outline.maxOf { it.x } - outline.minOf { it.x }
            val height = outline.maxOf { it.y } - outline.minOf { it.y }
            if (width < .06f || height < .08f) return null
            val mouthA = l.lips.minBy { local(it).x }
            val mouthB = l.lips.maxBy { local(it).x }
            val root = l.noseBridge.maxBy { local(it).y }
            val nose = mean(l.noseBottom)
            val confidence = (1 - ((max(abs(l.yaw), abs(l.pitch)) - 20) / 20)).coerceIn(0f, 1f)
            return BeautyGeometry(center, width, height, c, s, eyes[0], eyes[1], root, nose,
                mouthA, mouthB, mean(l.lips), l.outline.minBy { local(it).y }, aspect, confidence)
        }
    }
}
