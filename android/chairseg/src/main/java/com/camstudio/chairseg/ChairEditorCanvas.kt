package com.camstudio.chairseg

import android.content.Context
import android.graphics.*
import android.view.MotionEvent
import android.view.View
import kotlin.math.*

/** FIT_CENTER frozen source and editable selection. All coordinates stay in source space. */
class ChairEditorCanvas(context: Context): View(context) {
    var source: Bitmap?=null
    var background: Bitmap?=null
    var foreground: Bitmap?=null
    var mode=0 // outline, composite, original
    var onPoint: ((Float,Float,Boolean)->Unit)?=null
    private var overlay: Bitmap?=null
    private val paint=Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG)
    private var downX=0f; private var downY=0f; private var moved=false

    fun mask(mask: ChairMask?) {
        overlay=mask?.let { m -> Bitmap.createBitmap(IntArray(m.values.size) { i ->
            ((m.values[i].coerceIn(0f,1f)*130).toInt() shl 24) or 0x00ff80
        },m.width,m.height,Bitmap.Config.ARGB_8888) }
        invalidate()
    }
    fun brushOverlay(mask: ChairMask,x: Float,y: Float,radius: Float) {
        val bitmap=overlay
        if(bitmap==null || bitmap.width!=mask.width || bitmap.height!=mask.height) { this.mask(mask); return }
        val r=max(1f,radius*min(mask.width,mask.height))
        val left=max(0,(x*mask.width-r).toInt()); val top=max(0,(y*mask.height-r).toInt())
        val right=min(mask.width-1,(x*mask.width+r).toInt()); val bottom=min(mask.height-1,(y*mask.height+r).toInt())
        val w=right-left+1; val h=bottom-top+1
        bitmap.setPixels(IntArray(w*h) { i ->
            ((mask.values[(top+i/w)*mask.width+left+i%w].coerceIn(0f,1f)*130).toInt() shl 24) or 0x00ff80
        },0,w,left,top,w,h); invalidate()
    }
    override fun onDraw(canvas: Canvas) {
        canvas.drawColor(Color.rgb(24,27,32))
        val image=source ?: return
        val scale=min(width.toFloat()/image.width,height.toFloat()/image.height)
        val rect=RectF((width-image.width*scale)/2,(height-image.height*scale)/2,(width+image.width*scale)/2,(height+image.height*scale)/2)
        if(mode==1) {
            background?.let { bg ->
                val aspect=rect.width()/rect.height()
                val sw=min(bg.width.toFloat(),bg.height*aspect); val sh=min(bg.height.toFloat(),bg.width/aspect)
                canvas.drawBitmap(bg,Rect(((bg.width-sw)/2).toInt(),((bg.height-sh)/2).toInt(),((bg.width+sw)/2).toInt(),((bg.height+sh)/2).toInt()),rect,paint)
            }
            foreground?.let { canvas.drawBitmap(it,null,rect,paint) }
        } else {
            canvas.drawBitmap(image,null,rect,paint)
            if(mode==0) overlay?.let { canvas.drawBitmap(it,null,rect,paint) }
        }
    }
    override fun onTouchEvent(event: MotionEvent): Boolean {
        val image=source ?: return false
        val point=ChairMask.point(event.x,event.y,width,height,image.width,image.height)
        when(event.actionMasked) {
            MotionEvent.ACTION_DOWN -> { downX=event.x; downY=event.y; moved=false; point?.let { onPoint?.invoke(it.first,it.second,true) } }
            MotionEvent.ACTION_MOVE -> {
                if(abs(event.x-downX)+abs(event.y-downY)>resources.displayMetrics.density*8) moved=true
                point?.let { onPoint?.invoke(it.first,it.second,true) }
            }
            MotionEvent.ACTION_UP -> { if(!moved) point?.let { onPoint?.invoke(it.first,it.second,false) }; performClick() }
        }
        return true
    }
    override fun performClick(): Boolean { super.performClick(); return true }
}
