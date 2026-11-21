package com.camstudio.pro

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class CapturePolicyTest {
    @Test fun connectedLiveScreenStaysInStandbyUntilExplicitlyOpened() {
        assertFalse(CapturePolicy.wantsCamera(true,false,true,3,true,false))
        assertTrue(CapturePolicy.wantsCamera(true,true,true,3,true,false))
    }
    @Test fun cameraOffReleasesCaptureEvenWithBackgroundConnectionAndPreviewOwners() {
        assertFalse(CapturePolicy.wantsCamera(true,false,false,3,true,false))
        assertFalse(CapturePolicy.wantsCamera(true,false,true,3,false,false))
    }
    @Test fun explicitStreamingCanContinueWithScreenOffButLocalPreviewCannot() {
        assertTrue(CapturePolicy.wantsCamera(true,true,false,0,true,false))
        assertFalse(CapturePolicy.wantsCamera(true,true,false,3,false,false))
    }
    @Test fun qrScanRequiresExplicitVisibleScannerAndPermission() {
        assertTrue(CapturePolicy.wantsCamera(true,false,true,0,false,true))
        assertFalse(CapturePolicy.wantsCamera(true,false,false,0,false,true))
        assertFalse(CapturePolicy.wantsCamera(false,true,true,3,true,true))
    }
}
