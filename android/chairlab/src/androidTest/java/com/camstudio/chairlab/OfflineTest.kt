package com.camstudio.chairlab

import android.Manifest
import android.content.pm.PackageManager
import android.graphics.BitmapFactory
import androidx.test.platform.app.InstrumentationRegistry
import com.google.android.gms.tasks.Tasks
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.segmentation.Segmentation
import com.google.mlkit.vision.segmentation.selfie.SelfieSegmenterOptions
import java.nio.ByteOrder
import java.util.concurrent.TimeUnit
import org.junit.Assert.*
import org.junit.Test

class OfflineTest {
    @Test fun prototypeHasNoInternetPermissionAndBaselineModelWorksOffline() {
        val context=InstrumentationRegistry.getInstrumentation().targetContext
        assertEquals(PackageManager.PERMISSION_DENIED,context.checkSelfPermission(Manifest.permission.INTERNET))
        val bitmap=context.assets.open("daylight-office.png").use { BitmapFactory.decodeStream(it) }
        val model=Segmentation.getClient(SelfieSegmenterOptions.Builder().build())
        try {
            val mask=Tasks.await(model.process(InputImage.fromBitmap(bitmap,0)),20,TimeUnit.SECONDS)
            val values=mask.buffer.order(ByteOrder.nativeOrder()).asFloatBuffer()
            assertTrue(values.remaining()>0)
            while(values.hasRemaining()) { val value=values.get(); assertTrue(value.isFinite() && value in 0f..1f) }
        } finally { model.close() }
    }
}
