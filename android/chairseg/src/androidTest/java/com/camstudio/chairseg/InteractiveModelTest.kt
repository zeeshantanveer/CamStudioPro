package com.camstudio.chairseg

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.os.Build
import android.os.SystemClock
import android.util.Log
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import java.io.File
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Assume
import org.junit.Test
import org.junit.runner.RunWith

/** Native SDK/API/delegate smoke tests on a non-personal existing generated room asset.
 * A plant is selected here; these tests do not establish real-chair boundary quality. */
@RunWith(AndroidJUnit4::class)
class InteractiveModelTest {
    @Test fun cpuSelectsAnObjectAndProducesReviewableMask() { evaluate(false) }
    @Test fun gpuDelegateIsMeasuredWhenAvailable() { evaluate(true) }

    private fun evaluate(gpu: Boolean) {
        val instrumentation=InstrumentationRegistry.getInstrumentation()
        val context=instrumentation.targetContext
        val image=context.assets.open("daylight-office.png").use { BitmapFactory.decodeStream(it) }
        val bitmap=Bitmap.createScaledBitmap(image,1280,720,true)
        val start=SystemClock.elapsedRealtimeNanos()
        val selector=try { ChairSelector(context,gpu) } catch(e: Exception) {
            if(gpu) { Log.w("ChairModelTest","GPU unavailable",e); Assume.assumeNoException(e) }
            throw e
        }
        val initMs=(SystemClock.elapsedRealtimeNanos()-start)/1e6
        selector.use {
            val times=JSONArray()
            var firstSetup=0.0
            var selection: ChairSelector.Selection?=null
            repeat(5) { i ->
                selection=selector.select(bitmap,listOf(ChairSelector.Prompt(.86f,.7f,true)))
                if(i==0) firstSetup=selection!!.setImageMs
                times.put(selection!!.segmentMs)
            }
            val mask=selection!!.mask
            val positive=mask.sample(.86f,.7f)
            val wall=mask.sample(.5f,.3f)
            val fraction=mask.values.count { it>.5f }.toDouble()/mask.values.size
            val name=if(gpu) "gpu" else "cpu"
            val report=JSONObject().put("device",Build.MODEL).put("delegate",name).put("initMs",initMs)
                .put("firstSetImageMs",firstSetup).put("segmentMs",times).put("positiveConfidence",positive)
                .put("distantWallConfidence",wall).put("selectedFraction",fraction)
                .put("fixture","Existing AI-generated daylight office; selected plant, not user's chair")
            val dir=File(context.getExternalFilesDir(null),"benchmark").apply { mkdirs() }
            File(dir,"$name.json").writeText(report.toString(2))
            val pixels=IntArray(mask.values.size) { i -> val v=(mask.values[i]*255).toInt(); Color.rgb(v,v,v) }
            File(dir,"$name-mask.png").outputStream().use { out -> Bitmap.createBitmap(pixels,mask.width,mask.height,Bitmap.Config.ARGB_8888).compress(Bitmap.CompressFormat.PNG,100,out) }
            Log.i("ChairModelTest",report.toString())
            assertTrue("Positive object point must be included: $positive",positive>.5f)
            assertTrue("Distant wall must stay background: $wall",wall<.5f)
            assertTrue("Selection must not cover whole image: $fraction",fraction in .001..0.6)
        }
    }
}
