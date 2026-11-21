package com.camstudio.pro

import com.camstudio.pro.data.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class VideoOptionsTest {
    @Test fun liveBackgroundDoesNotChangeQualityOrRestartSettings() {
        val original = AppSettings(resolution = 2160, fps = 60, bitrateKbps = 12000, micEnabled = false)
        val patch = VideoOptions.fromJson(JSONObject().put("background", Bg.LIBRARY).put("natural", true), original, FxState())
        assertFalse(patch.changeQuality)
        assertEquals("", patch.error(false, false))
        assertSame(original, patch.settings(original))
        assertEquals(Bg.LIBRARY, patch.effects(FxState()).bg)
    }
    @Test fun hdKeepsHigherBitrateAndUnrelatedSettings() {
        val original = AppSettings(bitrateKbps = 12000, micEnabled = false)
        val result = VideoOptions(720, Bg.OFFICE, true).settings(original)
        assertEquals(720, result.resolution)
        assertEquals(30, result.fps)
        assertEquals(12000, result.bitrateKbps)
        assertFalse(result.micEnabled)
        assertEquals(8000, VideoOptions(1080, Bg.NONE, true).settings(AppSettings(bitrateKbps = 2000)).bitrateKbps)
    }
    @Test fun rejectsUnsupportedAndMissingBackgrounds() {
        assertTrue(VideoOptions(999, Bg.NONE, true).error(true,true).isNotEmpty())
        assertTrue(VideoOptions(1080, Bg.UPLOAD, true).error(false,true).isNotEmpty())
        assertTrue(VideoOptions(1080, Bg.ROOM, true).error(true,false).isNotEmpty())
        assertEquals("", VideoOptions(1080, Bg.OFFICE, true).error(false,false))
    }
    @Test fun realismPresetPreservesBeautyAndPhoto() {
        val original = FxState(uploadUri = "content://photo", brightness = 15)
        val result = VideoOptions(1080, Bg.LIBRARY, true).effects(original)
        assertEquals(original.uploadUri, result.uploadUri)
        assertEquals(original.beauty, result.beauty)
        assertEquals(15, result.brightness)
        assertTrue(result.depth > 0 && result.matchLight > 0 && result.lightWrap > 0)
        val off = VideoOptions(1080, Bg.LIBRARY, false).effects(result)
        assertEquals(0, off.depth + off.matchLight + off.grain + off.lightWrap)
    }
}
