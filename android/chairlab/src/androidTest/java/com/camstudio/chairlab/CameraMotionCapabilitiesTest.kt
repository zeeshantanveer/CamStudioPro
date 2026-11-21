package com.camstudio.chairlab

import android.content.Context
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.os.Build
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/** Read-only camera capabilities. Does not open a camera or change capture settings. */
@RunWith(AndroidJUnit4::class)
class CameraMotionCapabilitiesTest {
    @Test fun reportMotionCaptureControls() {
        assumeTrue(InstrumentationRegistry.getArguments().getString("motionCameraCaps") == "true")
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        val manager=context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
        val cameras=JSONArray()
        manager.cameraIdList.forEach { id ->
            val c=manager.getCameraCharacteristics(id)
            val capabilities=c.get(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES) ?: intArrayOf()
            cameras.put(JSONObject().put("id",id)
                .put("facing",c.get(CameraCharacteristics.LENS_FACING))
                .put("hardwareLevel",c.get(CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL))
                .put("manualSensor",capabilities.contains(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES_MANUAL_SENSOR))
                .put("exposureRangeNs",c.get(CameraCharacteristics.SENSOR_INFO_EXPOSURE_TIME_RANGE)?.toString())
                .put("isoRange",c.get(CameraCharacteristics.SENSOR_INFO_SENSITIVITY_RANGE)?.toString())
                .put("evRange",c.get(CameraCharacteristics.CONTROL_AE_COMPENSATION_RANGE)?.toString())
                .put("fpsRanges",JSONArray(c.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.map { it.toString() } ?: emptyList<String>())))
        }
        val output=File(context.getExternalFilesDir(null),"motion-review").apply { mkdirs() }
        File(output,"camera-capabilities.json").writeText(JSONObject().put("device",Build.MODEL)
            .put("cameraOpened",false).put("cameras",cameras).toString(2))
    }
}
