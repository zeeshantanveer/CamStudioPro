package com.camstudio.pro

import com.camstudio.pro.data.BeautyState
import com.camstudio.pro.data.BEAUTY_CONTROLS
import com.camstudio.pro.data.FxState
import com.camstudio.pro.media.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import kotlin.math.*

class BeautyTest {
    @Test fun rotationsMapBackToOriginalScene() {
        assertEquals(BeautyPoint(.2f, .7f), AnalysisPolicy.toScene(.2f, .3f, 0))
        assertEquals(BeautyPoint(.3f, .2f), AnalysisPolicy.toScene(.2f, .3f, 90))
        assertEquals(BeautyPoint(.8f, .3f), AnalysisPolicy.toScene(.2f, .3f, 180))
        assertEquals(BeautyPoint(.7f, .8f), AnalysisPolicy.toScene(.2f, .3f, 270))
        assertTrue(AnalysisPolicy.intervalNs(true) > AnalysisPolicy.intervalNs(false))
        assertEquals(1f, AnalysisPolicy.freshness(400f), .001f)
        assertEquals(0f, AnalysisPolicy.freshness(800f), .001f)
    }
    @Test fun landscapeFaceAxesKeepTheChinBelowTheMouth() {
        for (rotation in listOf(0f, (Math.PI/2).toFloat(), (-Math.PI/2).toFloat(), Math.PI.toFloat())) {
            val geometry = face(roll = rotation)
            val delta = geometry.warps(BeautyState(chinLength = 100)).single()
            assertTrue(delta.dx * -geometry.sin + delta.dy * geometry.cos < 0)
        }
    }
    private fun face(aspect: Float = 1f, roll: Float = 0f, mirrored: Boolean = false, yaw: Float = 0f): BeautyGeometry {
        val c = cos(roll)
        val s = sin(roll)
        fun p(x: Float, y: Float): BeautyPoint {
            val xx = if (mirrored) -x else x
            return BeautyPoint(.5f + (c * xx - s * y) / aspect, .5f + s * xx + c * y)
        }
        val outline = (0..35).map { i -> val a = i * 2 * Math.PI / 36; p((cos(a) * .23).toFloat(), (sin(a) * .30).toFloat()) }
        return BeautyGeometry.from(BeautyLandmarks(outline, listOf(p(-.09f, .08f)), listOf(p(.09f, .08f)),
            listOf(p(0f, .08f), p(0f, 0f)), listOf(p(-.04f, -.02f), p(0f, -.025f), p(.04f, -.02f)),
            listOf(p(-.08f, -.12f), p(.08f, -.12f), p(0f, -.10f), p(0f, -.14f)), yaw, 0f), aspect)!!
    }

    @Test fun settingsAreOffByDefaultAndPersistWithOtherEffects() {
        assertFalse(BeautyState().enabled)
        val original = FxState(brightness = 15, beauty = BeautyState(enabled = true, noseAlar = 33, noseLength = -20))
        assertEquals(original, FxState.fromJson(JSONObject(original.toJson().toString())))
        assertEquals(BeautyState(), FxState.fromJson(JSONObject("{}")).beauty)
    }

    @Test fun remotePatchesMergeClampAndIgnoreBadTypes() {
        val first = BeautyState(enabled = true, noseWidth = 24, smile = 10)
        val patched = first.patch(JSONObject("""{"noseAlar":999,"smile":-5,"chinLength":-200,"noseWidth":"bad","enabled":null,"unknown":50}"""))
        assertEquals(100, patched.noseAlar)
        assertEquals(0, patched.smile)
        assertEquals(-100, patched.chinLength)
        assertEquals(24, patched.noseWidth)
        assertTrue(patched.enabled)
        assertFalse(patched.toJson().has("unknown"))
        // A second editor changes a separate field without reverting the first editor's value.
        assertEquals(100, patched.patch(JSONObject().put("smile", 20)).noseAlar)
    }

    @Test fun everyShapeControlProducesBoundedLocalWarps() {
        val face = face()
        assertTrue(face.warps(BeautyState()).isEmpty())
        // Skin and contour controls shade colour; every other control reshapes.
        for (control in BEAUTY_CONTROLS.filter { !it.key.startsWith("skin") && it.key != "noseContour" }) {
            val settings = BeautyState(enabled = true).patch(JSONObject().put(control.key, 100))
            val warps = face.warps(settings)
            assertTrue("${control.key} has no effect", warps.isNotEmpty())
            assertTrue(warps.size <= BeautyGeometry.MAX_WARPS)
            for (w in warps) {
                assertTrue(w.rx > 0 && w.ry > 0)
                assertTrue(abs(w.dx) + abs(w.dy) < .05f)
            }
        }
        val all = BeautyState(enabled = true).patch(JSONObject().apply { BEAUTY_CONTROLS.forEach { put(it.key, 100) } })
        assertEquals(20, face.warps(all).size)
    }

    @Test fun noseBridgeNarrowsBetweenTheEyesTowardTheCentreLine() {
        val f = face()
        val w = f.warps(BeautyState(enabled = true, noseBridge = 100))
        assertEquals(2, w.size)
        val left = w.minBy { it.x }
        val right = w.maxBy { it.x }
        assertTrue("both sides move inward", left.dx > 0 && right.dx < 0)
        for (p in w) assertTrue("on the upper nose", p.y < f.root.y && p.y > f.nose.y)
        // Negative values widen instead.
        assertTrue(f.warps(BeautyState(enabled = true, noseBridge = -100)).minBy { it.x }.dx < 0)
    }

    @Test fun reshapeDirectionsFollowFaceRollAndAspect() {
        for (aspect in listOf(16f/9, 9f/16)) {
            val nose = BeautyState(enabled = true, noseLength = 100)
            val upright = face(aspect).warps(nose).single()
            assertTrue(upright.dy < 0)
            assertEquals(0f, upright.dx, .00001f)
            val rolled = face(aspect, roll = .35f).warps(nose).single()
            assertTrue(rolled.dx > 0)
            assertTrue(rolled.dy < 0)
            val mirrored = face(aspect, mirrored = true).warps(nose).single()
            assertEquals(upright.dy, mirrored.dy, .00001f)
            val slim = face(aspect).warps(BeautyState(faceSlim = 100))
            assertTrue(slim[0].dx > 0 && slim[1].dx < 0)
        }
    }

    @Test fun extremePosesBypassAndMissingLandmarksAreRejected() {
        assertEquals(0f, face(yaw = 45f).confidence, .00001f)
        assertEquals(1f, face(yaw = 0f).confidence, .00001f)
        assertNull(BeautyGeometry.from(BeautyLandmarks(emptyList(), emptyList(), emptyList(), emptyList(), emptyList(), emptyList(), 0f, 0f), 1f))
    }
}
