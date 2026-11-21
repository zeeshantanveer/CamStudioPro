package com.camstudio.chairlab

import android.Manifest
import android.content.pm.PackageManager
import android.graphics.*
import android.os.Bundle
import android.os.SystemClock
import android.util.Log
import android.view.MotionEvent
import android.view.View
import android.widget.*
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import com.camstudio.chairseg.ChairMask
import com.camstudio.chairseg.ChairSelector
import com.google.android.gms.tasks.Tasks
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.segmentation.Segmentation
import com.google.mlkit.vision.segmentation.Segmenter
import com.google.mlkit.vision.segmentation.selfie.SelfieSegmenterOptions
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import kotlin.math.min

/** A separate evaluation app. No sockets, no production settings and no automatic camera use. */
class MainActivity: ComponentActivity() {
    private val executor = Executors.newSingleThreadExecutor()
    private lateinit var status: TextView
    private lateinit var preview: PreviewView
    private lateinit var board: MaskBoard
    private var provider: ProcessCameraProvider? = null
    private var lens = CameraSelector.LENS_FACING_FRONT
    private var reference: Bitmap? = null
    private var pose: Bitmap? = null
    private var referencePerson: ChairMask? = null
    private var posePerson: ChairMask? = null
    private var chair: ChairMask? = null
    private var selector: ChairSelector? = null
    private var selectorGpu = false
    private var gpu = false
    private var personModel: Segmenter? = null
    private var busy = false
    private var tool = 0 // positive prompt, negative prompt, preserve brush, erase chair brush
    private var mode = 0 // outline, composite, original
    private var showingPose = false
    private var pendingPoseRender: Boolean? = null
    private var brushRadius = .025f
    private val prompts = mutableListOf<ChairSelector.Prompt>()
    private val measurements = JSONArray()
    private val permission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { allowed ->
        if (allowed) openCamera() else status.text = "Camera permission denied. Import a photo instead."
    }
    private val photoPicker = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) work("Loading photo…") {
            val bitmap = if (android.os.Build.VERSION.SDK_INT >= 28)
                ImageDecoder.decodeBitmap(ImageDecoder.createSource(contentResolver,uri)) { decoder,_,_ -> decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE }
            else contentResolver.openInputStream(uri).use { BitmapFactory.decodeStream(it) }
            requireNotNull(bitmap) { "Cannot decode this photo." }
            val image = sized(bitmap)
            val person = personMask(image)
            ui { setReference(image,person) }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL; setBackgroundColor(Color.rgb(245,246,248))
            setPadding(dp(8),dp(12),dp(8),dp(12))
            setOnApplyWindowInsetsListener { v,i -> v.setPadding(dp(8),i.systemWindowInsetTop+dp(6),dp(8),i.systemWindowInsetBottom+dp(6)); i }
        }
        root.addView(TextView(this).apply { text = "CamStudio Chair Lab · separate prototype"; textSize = 18f; setTextColor(Color.BLACK) })
        status = TextView(this).apply { textSize = 13f; text = "Keep phone and chair fixed. Stop the main app's camera before using Camera here. Freeze the empty chair, then tap its upholstery. Nothing is sent to Zoom." }
        root.addView(status,LinearLayout.LayoutParams(-1,dp(82)))
        val frame = FrameLayout(this)
        preview = PreviewView(this).apply { scaleType = PreviewView.ScaleType.FIT_CENTER; visibility = View.GONE }
        board = MaskBoard(this).apply { onPoint = ::onPoint }
        frame.addView(preview,FrameLayout.LayoutParams(-1,-1)); frame.addView(board,FrameLayout.LayoutParams(-1,-1))
        root.addView(frame,LinearLayout.LayoutParams(-1,0,1f))
        fun row(vararg items: Pair<String,()->Unit>) {
            val line = LinearLayout(this)
            items.forEach { (title,action) -> line.addView(Button(this).apply {
                text = title; textSize = 11f; isAllCaps = false
                setOnClickListener { if (!busy) action() }
            },LinearLayout.LayoutParams(0,dp(45),1f)) }
            root.addView(line)
        }
        row("Camera" to { openCamera() },"Freeze chair" to { freeze(false) },"Freeze pose" to { freeze(true) },"Import" to { photoPicker.launch(arrayOf("image/*")) })
        row("Pick chair" to { chooseTool(0) },"Exclude" to { chooseTool(1) },"Brush +" to { chooseTool(2) },"Brush −" to { chooseTool(3) })
        row("Outline" to { mode=0; show(false) },"Composite" to { mode=1; show(pose!=null) },"Original" to { mode=2; show(pose!=null) },"Clear" to {
            chair=null; prompts.clear(); mode=0; show(false); status.text="Selection cleared. The person mask is unchanged."
        })
        row("CPU / GPU" to { gpu=!gpu; status.text="Next selection uses ${if(gpu) "GPU" else "CPU"}." },"Benchmark" to { select(true) },"Save test set" to { save() },"Front / rear" to {
            lens=if(lens==CameraSelector.LENS_FACING_FRONT) CameraSelector.LENS_FACING_BACK else CameraSelector.LENS_FACING_FRONT
            reference=null; pose=null; chair=null; prompts.clear(); openCamera()
        })
        root.addView(SeekBar(this).apply {
            max=55; progress=20; contentDescription="Correction brush size"
            setOnSeekBarChangeListener(object: SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(s: SeekBar?,p: Int,user: Boolean) { brushRadius=(p+5)/1000f }
                override fun onStartTrackingTouch(s: SeekBar?) {}
                override fun onStopTrackingTouch(s: SeekBar?) {}
            })
        },LinearLayout.LayoutParams(-1,dp(28)))
        setContentView(root)
    }

    private fun openCamera() {
        if (ContextCompat.checkSelfPermission(this,Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            permission.launch(Manifest.permission.CAMERA); return
        }
        val future = ProcessCameraProvider.getInstance(this)
        future.addListener({
            try {
                provider = future.get(); provider!!.unbindAll()
                val p = Preview.Builder().build().also { it.surfaceProvider = preview.surfaceProvider }
                val camera = provider!!.bindToLifecycle(this,CameraSelector.Builder().requireLensFacing(lens).build(),p)
                camera.cameraInfo.cameraState.observe(this) { state -> state.error?.let {
                    status.text="Camera unavailable (${it.code}). Stop the camera connection in CamStudio Pro, then retry Camera."
                } }
                preview.visibility=View.VISIBLE; board.visibility=View.GONE
                status.text=if(reference==null) "Move fully out of view; leave the chair in place, then tap Freeze chair."
                    else "Keep the camera and chair fixed. Sit down, lean or hold a mug, then tap Freeze pose."
            } catch(e: Exception) { error(e) }
        },ContextCompat.getMainExecutor(this))
    }

    private fun freeze(testPose: Boolean) {
        val raw = if(preview.visibility==View.VISIBLE) preview.bitmap else null
        if(raw==null) { status.text="Open Camera first and wait for a preview."; return }
        if(testPose && reference==null) { status.text="Freeze and select the empty chair first."; return }
        val image = sized(raw)
        if(testPose) {
            val ref=reference!!
            if(kotlin.math.abs(image.width.toFloat()/image.height-ref.width.toFloat()/ref.height)>.01f) {
                status.text="Framing changed. Freeze the chair again before testing a pose."; return
            }
        }
        provider?.unbindAll()
        preview.visibility=View.GONE; board.visibility=View.VISIBLE
        work("Computing the baseline person mask…") {
            val person = personMask(image)
            ui {
                if(testPose) { pose=image; posePerson=person; mode=1; show(true); status.text="Pose preview: original person mask + selected chair. Check hair, shoulders and the mug. No video tracking is enabled." }
                else setReference(image,person)
            }
        }
    }

    private fun setReference(image: Bitmap,person: ChairMask) {
        provider?.unbindAll(); reference=image; referencePerson=person; pose=null; posePerson=null
        chair=null; prompts.clear(); mode=0; tool=0; show(false)
        status.text="Tap the chair upholstery to select it. Use Exclude on unwanted room areas, then Brush + / − for corrections."
    }

    private fun personMask(bitmap: Bitmap): ChairMask {
        val model=personModel ?: Segmentation.getClient(SelfieSegmenterOptions.Builder()
            .setDetectorMode(SelfieSegmenterOptions.STREAM_MODE).build()).also { personModel=it }
        val result=Tasks.await(model.process(InputImage.fromBitmap(bitmap,0)),10,TimeUnit.SECONDS)
        val values=FloatArray(result.width*result.height)
        result.buffer.order(java.nio.ByteOrder.nativeOrder()).asFloatBuffer().get(values)
        return ChairMask(result.width,result.height,values)
    }

    private fun chooseTool(next: Int) {
        tool=next; mode=0; show(false)
        status.text=when(next) {
            0 -> "Tap on the chair to add a positive model prompt."
            1 -> "Tap on a wrongly selected room area to exclude it."
            2 -> "Draw over missing chair areas. This changes only the chair selection."
            else -> "Draw over unwanted chair-selection areas. The person mask is never erased."
        }
    }

    private fun onPoint(x: Float,y: Float,drag: Boolean) {
        if(busy || reference==null || mode!=0 || showingPose) return
        if(tool<2) {
            if(drag) return
            prompts.add(ChairSelector.Prompt(x,y,tool==0)); select(false)
        } else {
            val ref=reference!!
            val mask=chair ?: ChairMask(ref.width,ref.height,FloatArray(ref.width*ref.height)).also { chair=it }
            mask.brush(x,y,brushRadius,tool==2)
            board.editOverlay(mask,x,y,brushRadius)
        }
    }

    private fun select(benchmark: Boolean) {
        val image=reference ?: return
        if(prompts.none { it.keep }) { status.text="Pick chair first: tap a point inside the chair."; return }
        val selected=prompts.toList(); val useGpu=gpu
        work(if(benchmark) "Benchmarking five model selections…" else "MediaPipe is selecting the chair…") {
            val init=SystemClock.elapsedRealtimeNanos()
            if(selector==null || selectorGpu!=useGpu) {
                selector?.close(); selector=null
                selector=ChairSelector(this,useGpu); selectorGpu=useGpu
            }
            val initMs=(SystemClock.elapsedRealtimeNanos()-init)/1e6
            val times=mutableListOf<Double>()
            var result: ChairSelector.Selection?=null
            repeat(if(benchmark) 5 else 1) { result=selector!!.select(image,selected); times.add(result!!.segmentMs) }
            val mask=result!!.mask
            val report=JSONObject().put("delegate",if(useGpu) "GPU" else "CPU").put("initMs",initMs)
                .put("setImageMs",result!!.setImageMs).put("segmentMs",JSONArray(times))
                .put("maskWidth",mask.width).put("maskHeight",mask.height).put("selectedFraction",mask.values.count { it>.5f }.toDouble()/mask.values.size)
            measurements.put(report); Log.i("CamChairLab",report.toString())
            ui {
                chair=mask; mode=0; show(false)
                status.text="${if(useGpu) "GPU" else "CPU"}: ${times.map { it.toInt() }} ms; image setup ${result!!.setImageMs.toInt()} ms. Review the outline. AI selection replaces manual corrections; apply corrections last."
            }
        }
    }

    private fun show(testPose: Boolean) {
        val source=(if(testPose) pose else reference) ?: return
        showingPose=testPose; preview.visibility=View.GONE; board.visibility=View.VISIBLE
        board.source=source; board.mode=mode; board.overlay(chair)
        if(mode==1) {
            if(busy) { pendingPoseRender=testPose; return }
            val person=if(testPose) posePerson else referencePerson
            val savedChair=chair
            work("Rendering the combined foreground…") {
                val out=composite(source,person,savedChair)
                ui { board.cutout=out; board.invalidate(); status.text="Composite uses live source pixels in the person OR chair mask. Fixed framing only; check the chair outline before accepting." }
            }
        } else board.invalidate()
    }

    private fun composite(source: Bitmap,person: ChairMask?,chair: ChairMask?): Bitmap {
        val w=source.width; val h=source.height; val px=IntArray(w*h)
        source.getPixels(px,0,w,0,0,w,h)
        for(y in 0 until h) for(x in 0 until w) {
            val nx=(x+.5f)/w; val ny=(y+.5f)/h
            val a=ChairMask.union(person?.sample(nx,ny)?:0f,chair?.sample(nx,ny)?:0f)
            px[y*w+x]=(px[y*w+x] and 0xffffff) or ((a*255).toInt() shl 24)
        }
        return Bitmap.createBitmap(px,w,h,Bitmap.Config.ARGB_8888)
    }

    private fun save() {
        val ref=reference ?: return
        val mask=chair ?: run { status.text="Select the chair before saving a review set."; return }
        val test=pose; val body=if(test!=null) posePerson else referencePerson
        work("Saving local review images and timings…") {
            val dir=File(getExternalFilesDir(null),"review").apply { mkdirs() }
            fun png(name: String,b: Bitmap) { File(dir,name).outputStream().use { b.compress(Bitmap.CompressFormat.PNG,100,it) } }
            png("chair-source.png",ref)
            val pixels=IntArray(mask.values.size) { val v=(mask.values[it].coerceIn(0f,1f)*255).toInt(); Color.rgb(v,v,v) }
            png("chair-mask.png",Bitmap.createBitmap(pixels,mask.width,mask.height,Bitmap.Config.ARGB_8888))
            if(test!=null) png("pose-source.png",test)
            png("foreground.png",composite(test?:ref,body,mask))
            val meta=JSONObject().put("fixedPhoneAndChair",true).put("frontPreviewMirrored",lens==CameraSelector.LENS_FACING_FRONT)
                .put("model","MagicTouch v2 int8 version 1").put("measurements",measurements)
                .put("prompts",JSONArray(prompts.map { JSONObject().put("x",it.x).put("y",it.y).put("keep",it.keep) }))
            File(dir,"review.json").writeText(meta.toString(2))
            ui { status.text="Saved locally: $dir. No images were uploaded." }
        }
    }

    private fun work(message: String,body: ()->Unit) {
        if(busy) return
        busy=true; status.text=message
        executor.execute {
            try { body() } catch(e: Exception) { Log.e("CamChairLab",message,e); ui { error(e) } }
            finally { ui {
                busy=false
                val pending=pendingPoseRender; pendingPoseRender=null
                if(pending!=null) show(pending)
            } }
        }
    }
    private fun ui(body: ()->Unit) = runOnUiThread { if(!isDestroyed) body() }
    private fun error(e: Exception) { status.text="${e.javaClass.simpleName}: ${e.message}" }
    private fun sized(bitmap: Bitmap): Bitmap {
        val scale=min(1f,1280f/maxOf(bitmap.width,bitmap.height))
        val out=Bitmap.createScaledBitmap(bitmap,(bitmap.width*scale).toInt(),(bitmap.height*scale).toInt(),true)
        return if(out.config==Bitmap.Config.ARGB_8888) out else out.copy(Bitmap.Config.ARGB_8888,false)
    }
    private fun dp(n: Int)=(resources.displayMetrics.density*n).toInt()
    override fun onDestroy() {
        provider?.unbindAll()
        executor.execute { selector?.close(); personModel?.close() }; executor.shutdown()
        super.onDestroy()
    }
}

private class MaskBoard(context: android.content.Context): View(context) {
    var source: Bitmap?=null
    var cutout: Bitmap?=null
    var mode=0
    var onPoint: ((Float,Float,Boolean)->Unit)?=null
    private var maskBitmap: Bitmap?=null
    private val paint=Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG)
    private val bg=context.assets.open("daylight-office.png").use { BitmapFactory.decodeStream(it) }
    private var moved=false
    private var downX=0f
    private var downY=0f
    fun overlay(mask: ChairMask?) {
        maskBitmap=mask?.let { m -> Bitmap.createBitmap(IntArray(m.values.size) { ((m.values[it].coerceIn(0f,1f)*130).toInt() shl 24) or 0x00ff80 },m.width,m.height,Bitmap.Config.ARGB_8888) }
        invalidate()
    }
    fun editOverlay(mask: ChairMask,x: Float,y: Float,radius: Float) {
        val bitmap=maskBitmap
        if(bitmap==null || bitmap.width!=mask.width || bitmap.height!=mask.height) { overlay(mask); return }
        val r=kotlin.math.max(1f,radius*min(mask.width,mask.height))
        val left=kotlin.math.max(0,(x*mask.width-r).toInt()); val top=kotlin.math.max(0,(y*mask.height-r).toInt())
        val right=kotlin.math.min(mask.width-1,(x*mask.width+r).toInt()); val bottom=kotlin.math.min(mask.height-1,(y*mask.height+r).toInt())
        val w=right-left+1; val h=bottom-top+1
        val patch=IntArray(w*h) { i -> ((mask.values[(top+i/w)*mask.width+left+i%w].coerceIn(0f,1f)*130).toInt() shl 24) or 0x00ff80 }
        bitmap.setPixels(patch,0,w,left,top,w,h); invalidate()
    }
    override fun onDraw(canvas: Canvas) {
        canvas.drawColor(Color.rgb(24,27,32))
        val image=source ?: return
        val scale=min(width.toFloat()/image.width,height.toFloat()/image.height)
        val rect=RectF((width-image.width*scale)/2,(height-image.height*scale)/2,(width+image.width*scale)/2,(height+image.height*scale)/2)
        if(mode==1) {
            val aspect=rect.width()/rect.height()
            val sw=min(bg.width.toFloat(),bg.height*aspect)
            val sh=min(bg.height.toFloat(),bg.width/aspect)
            val bgCrop=Rect(((bg.width-sw)/2).toInt(),((bg.height-sh)/2).toInt(),((bg.width+sw)/2).toInt(),((bg.height+sh)/2).toInt())
            canvas.drawBitmap(bg,bgCrop,rect,paint)
            cutout?.let { canvas.drawBitmap(it,null,rect,paint) }
        } else {
            canvas.drawBitmap(image,null,rect,paint)
            if(mode==0) maskBitmap?.let { canvas.drawBitmap(it,null,rect,paint) }
        }
    }
    override fun onTouchEvent(event: MotionEvent): Boolean {
        val image=source ?: return false
        val p=ChairMask.point(event.x,event.y,width,height,image.width,image.height)
        when(event.actionMasked) {
            MotionEvent.ACTION_DOWN -> { moved=false; downX=event.x; downY=event.y; if(p!=null) onPoint?.invoke(p.first,p.second,true) }
            MotionEvent.ACTION_MOVE -> {
                if(kotlin.math.abs(event.x-downX)+kotlin.math.abs(event.y-downY)>resources.displayMetrics.density*8) moved=true
                if(p!=null) onPoint?.invoke(p.first,p.second,true)
            }
            MotionEvent.ACTION_UP -> { if(p!=null && !moved) onPoint?.invoke(p.first,p.second,false); performClick() }
        }
        return true
    }
    override fun performClick(): Boolean { super.performClick(); return true }
}
