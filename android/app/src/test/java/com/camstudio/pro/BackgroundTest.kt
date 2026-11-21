package com.camstudio.pro

import com.camstudio.pro.data.Bg
import com.camstudio.pro.data.FxState
import com.camstudio.pro.media.AnalysisPolicy
import com.camstudio.pro.media.MaskFrameSync
import com.camstudio.pro.media.MaskFilter
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.ByteBuffer

class BackgroundTest {
    private fun u(b: Byte) = b.toInt() and 0xff

    @Test fun exactFramePairingNeverUsesAnEstimatedDelay() {
        val retained = longArrayOf(104, 101, 102, 103)
        assertEquals(1, MaskFrameSync.slot(101, retained))
        assertEquals(-1, MaskFrameSync.slot(100, retained))
        assertEquals(-1, MaskFrameSync.slot(-1, LongArray(4) { -1 }))
        retained.fill(-1)
        assertEquals(-1, MaskFrameSync.slot(101, retained))
        assertEquals(66, MaskFrameSync.delayMs(105, 103, 33f))
    }

    @Test fun nativeMaskCanHaveDifferentDimensionsToItsSource() {
        val px = ByteBuffer.allocate(4 * 4 * 4)
        for (i in 0 until 16) { px.put(i*4, (if(i%4<2) 255 else 0).toByte()); px.put(i*4+2,(if(i%4>=2) 255 else 0).toByte()) }
        val result = MaskFilter.roomStats(px, byteArrayOf(0,255.toByte()),4,4,2,1)
        assertTrue(result.valid)
        assertEquals(1f,result.r,0.001f)
        assertEquals(0f,result.b,0.001f)
        assertEquals(0.5f,result.coverage,0.001f)
    }

    @Test fun hotSegmentationKeepsUpWithProtectedRenderRate() {
        assertTrue(AnalysisPolicy.segmentIntervalNs(true) <= 1_000_000_000L / 15)
        assertTrue(AnalysisPolicy.segmentIntervalNs(false) <= 1_000_000_000L / 30)
    }

    @Test fun stabilizeDampsFlickerButKeepsMovement() {
        val prev = byteArrayOf(100, 100, 0)
        val mask = byteArrayOf(110.toByte(), 250.toByte(), 255.toByte())
        MaskFilter.stabilize(mask, prev)
        // 10-level shimmer: only part of the change goes through.
        assertTrue(u(mask[0]) in 101..108)
        // Large changes (a person moving) are taken immediately, no lag.
        assertEquals(250, u(mask[1]))
        assertEquals(255, u(mask[2]))
    }

    @Test fun roomStatsUsesOnlyClearBackground() {
        val w = 4
        val h = 4
        val px = ByteBuffer.allocate(w * h * 4)
        val mask = ByteArray(w * h)
        for (i in 0 until w * h) {
            val person = i % w >= 2
            mask[i] = (if (person) 255 else 0).toByte()
            // Wall is pure red, person pure blue.
            px.put(i * 4, (if (person) 0 else 255).toByte())
            px.put(i * 4 + 2, (if (person) 255 else 0).toByte())
        }
        val s = MaskFilter.roomStats(px, mask, w, h)
        assertTrue(s.valid)
        assertEquals(1f, s.r, 0.001f)
        assertEquals(0f, s.b, 0.001f)
        assertEquals(0.5f, s.coverage, 0.001f)
    }

    @Test fun roomStatsInvalidWhenPersonFillsFrame() {
        val px = ByteBuffer.allocate(16 * 4)
        val mask = ByteArray(16) { 255.toByte() }
        val s = MaskFilter.roomStats(px, mask, 4, 4)
        assertFalse(s.valid)
        assertEquals(1f, s.coverage, 0.001f)
    }

    @Test fun matchGainIsNeutralAtZeroAndBounded() {
        val room = floatArrayOf(0.2f, 0.2f, 0.2f)
        val bg = floatArrayOf(0.8f, 0.6f, 0.4f)
        MaskFilter.matchGain(room, bg, 0f).forEach { assertEquals(1f, it, 0.0001f) }
        val g = MaskFilter.matchGain(room, bg, 1f)
        // A darker room darkens the background, and a warm background is pulled toward neutral.
        assertTrue(g.all { it < 1f && it > 0.3f })
        assertTrue(g[2] > g[0])
    }

    @Test fun grainFollowsIso() {
        assertTrue(MaskFilter.grainSigma(3200, 0.5f) > MaskFilter.grainSigma(100, 0.5f))
        assertEquals(0f, MaskFilter.grainSigma(800, 0f), 0f)
        assertTrue(MaskFilter.grainSigma(6400, 1f) <= 0.06f)
    }

    @Test fun segmentationRunsFasterThanFaceAnalysis() {
        assertTrue(AnalysisPolicy.segmentIntervalNs(false) < AnalysisPolicy.intervalNs(false))
        assertTrue(AnalysisPolicy.segmentIntervalNs(true) >= AnalysisPolicy.segmentIntervalNs(false))
    }

    @Test fun realismSettingsRoundTrip() {
        val f = FxState(bg = Bg.ROOM, videoUri = "content://v", roomVersion = 42L, depth = 10, matchLight = 20, grain = 30, lightWrap = 40)
        assertEquals(f, FxState.fromJson(f.toJson()))
    }
}
