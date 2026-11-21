package com.camstudio.pro.media

import org.junit.Assert.assertEquals
import org.junit.Test

class MaskMotionHistoryTest {
    @Test fun motionUsesOlderFrameNotNewerCameraFrames() {
        assertEquals(3, MaskFrameSync.previousSlot(41, longArrayOf(44,41,42,40)))
    }
    @Test fun oldestRetainedFrameHasNoValidMotionHistory() {
        assertEquals(-1, MaskFrameSync.previousSlot(40, longArrayOf(40,41,42,43)))
    }
    @Test fun geometryResetInvalidatesHistory() {
        assertEquals(-1, MaskFrameSync.previousSlot(8, longArrayOf(-1,-1,-1,-1)))
        assertEquals(-1, MaskFrameSync.previousSlot(-1, longArrayOf(3,4,5,6)))
    }
    @Test fun wrappedRingSelectsNearestEarlierFrame() {
        assertEquals(1, MaskFrameSync.previousSlot(10, longArrayOf(8,9,10,11)))
    }
}
