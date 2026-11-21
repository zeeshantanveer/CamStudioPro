package com.camstudio.pro.media

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.LinearGradient
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.RadialGradient
import android.graphics.Shader
import android.media.ExifInterface
import android.media.MediaMetadataRetriever
import android.net.Uri
import java.io.File
import kotlin.random.Random

/** Built-in virtual backgrounds, drawn in code so no image assets are needed. */
object Backdrops {
    private const val W = 1280
    private const val H = 720

    /** Photographic assets with the original procedural art as a decode fallback. */
    fun photographicOffice(ctx: Context): Bitmap = asset(ctx, "daylight-office.png") ?: office()
    fun photographicLibrary(ctx: Context): Bitmap = asset(ctx, "reading-room.png") ?: library()
    private fun asset(ctx: Context, name: String): Bitmap? = runCatching {
        ctx.assets.open("backgrounds/$name").use { BitmapFactory.decodeStream(it) }
    }.getOrNull()

    fun office(): Bitmap {
        val b = Bitmap.createBitmap(W, H, Bitmap.Config.ARGB_8888)
        val c = Canvas(b)
        val p = Paint(Paint.ANTI_ALIAS_FLAG)
        p.shader = LinearGradient(0f, 0f, 0f, H * 0.62f, 0xFFE6EAEF.toInt(), 0xFFD3D9E0.toInt(), Shader.TileMode.CLAMP)
        c.drawRect(0f, 0f, W.toFloat(), H * 0.62f, p)
        p.shader = LinearGradient(0f, H * 0.62f, 0f, H.toFloat(), 0xFF9A8973.toInt(), 0xFF7C6C58.toInt(), Shader.TileMode.CLAMP)
        c.drawRect(0f, H * 0.62f, W.toFloat(), H.toFloat(), p)
        p.shader = null
        // Window with soft daylight
        p.color = 0xFFF7FAFD.toInt()
        c.drawRoundRect(820f, 70f, 1180f, 380f, 6f, 6f, p)
        p.shader = LinearGradient(820f, 70f, 1180f, 380f, 0xFFBFD8EE.toInt(), 0xFFEAF3FA.toInt(), Shader.TileMode.CLAMP)
        c.drawRect(834f, 84f, 1166f, 366f, p)
        p.shader = null
        p.color = 0xFFF7FAFD.toInt()
        c.drawRect(994f, 84f, 1006f, 366f, p)
        c.drawRect(834f, 218f, 1166f, 230f, p)
        // Shelf and a few objects
        p.color = 0xFFB9A58C.toInt()
        c.drawRect(90f, 250f, 520f, 264f, p)
        val cols = intArrayOf(0xFF5E7C8F.toInt(), 0xFF8F6F5E.toInt(), 0xFF6F8F6A.toInt(), 0xFFC9B79A.toInt())
        var x = 120f
        for (i in 0 until 7) {
            p.color = cols[i % cols.size]
            val h = 60f + (i * 37 % 50)
            c.drawRect(x, 250f - h, x + 26f, 250f, p)
            x += 32f
        }
        // Plant
        p.color = 0xFF8A6A50.toInt()
        c.drawRoundRect(420f, 200f, 480f, 250f, 8f, 8f, p)
        p.color = 0xFF5F8A62.toInt()
        c.drawOval(400f, 120f, 460f, 210f, p)
        c.drawOval(440f, 110f, 500f, 205f, p)
        // Floor shadow
        p.shader = LinearGradient(0f, H * 0.62f, 0f, H * 0.68f, 0x33000000, 0x00000000, Shader.TileMode.CLAMP)
        c.drawRect(0f, H * 0.62f, W.toFloat(), H * 0.68f, p)
        return b
    }

    fun library(): Bitmap {
        val b = Bitmap.createBitmap(W, H, Bitmap.Config.ARGB_8888)
        val c = Canvas(b)
        val p = Paint(Paint.ANTI_ALIAS_FLAG)
        c.drawColor(0xFF2A2420.toInt())
        val rnd = Random(7)
        val cols = intArrayOf(
            0xFF6B4F3A.toInt(), 0xFF3E5A6B.toInt(), 0xFF7A6A4A.toInt(), 0xFF4E3B4A.toInt(),
            0xFF5A6B4E.toInt(), 0xFF8A5A44.toInt(), 0xFF3E4A6B.toInt(), 0xFF9A8460.toInt(),
        )
        val shelfH = H / 4f
        for (row in 0 until 4) {
            val base = (row + 1) * shelfH - 14f
            var x = 12f
            while (x < W - 12f) {
                val w = 16f + rnd.nextInt(22)
                val h = shelfH * (0.55f + rnd.nextFloat() * 0.3f)
                p.color = cols[rnd.nextInt(cols.size)]
                c.drawRect(x, base - h, x + w, base, p)
                p.color = 0x22FFFFFF
                c.drawRect(x + 3f, base - h + 10f, x + w - 3f, base - h + 14f, p)
                x += w + 2f
            }
            p.color = 0xFF1A1613.toInt()
            c.drawRect(0f, base, W.toFloat(), base + 14f, p)
        }
        p.shader = RadialGradient(W * 0.5f, H * 0.3f, W * 0.7f, 0x33FFCF8A, 0x66000000, Shader.TileMode.CLAMP)
        c.drawRect(0f, 0f, W.toFloat(), H.toFloat(), p)
        return b
    }

    fun fromUri(ctx: Context, uri: Uri): Bitmap? = runCatching {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        ctx.contentResolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) }
        var sample = 1
        while (bounds.outWidth / sample > 2048 || bounds.outHeight / sample > 2048) sample *= 2
        val opts = BitmapFactory.Options().apply { inSampleSize = sample }
        val bmp = ctx.contentResolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, opts) }
        // Phone photos are usually stored sideways with an EXIF rotation tag.
        val degrees = ctx.contentResolver.openInputStream(uri)?.use { exifDegrees(ExifInterface(it)) } ?: 0
        bmp?.let { rotate(it, degrees) }
    }.getOrNull()

    fun fromFile(file: File): Bitmap? =
        if (file.exists()) runCatching { BitmapFactory.decodeFile(file.absolutePath) }.getOrNull() else null

    class VideoInfo(val aspect: Float, val mean: FloatArray?)

    /** Display aspect (rotation applied) and the average colour of an early frame. */
    fun videoInfo(ctx: Context, uri: Uri): VideoInfo? = runCatching {
        val r = MediaMetadataRetriever()
        try {
            r.setDataSource(ctx, uri)
            val w = r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_WIDTH)?.toFloatOrNull() ?: return null
            val h = r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_HEIGHT)?.toFloatOrNull() ?: return null
            val rot = r.extractMetadata(MediaMetadataRetriever.METADATA_KEY_VIDEO_ROTATION)?.toIntOrNull() ?: 0
            if (w <= 0f || h <= 0f) return null
            val aspect = if (rot % 180 != 0) h / w else w / h
            val mean = r.getFrameAtTime(500_000)?.let { frame ->
                val small = Bitmap.createScaledBitmap(frame, 16, 16, true)
                val px = IntArray(256)
                small.getPixels(px, 0, 16, 0, 0, 16, 16)
                var sr = 0L; var sg = 0L; var sb = 0L
                for (c in px) { sr += (c shr 16) and 0xff; sg += (c shr 8) and 0xff; sb += c and 0xff }
                floatArrayOf(sr / 65280f, sg / 65280f, sb / 65280f)
            }
            VideoInfo(aspect, mean)
        } finally {
            r.release()
        }
    }.getOrNull()

    private fun exifDegrees(exif: ExifInterface): Int = when (exif.getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL)) {
        ExifInterface.ORIENTATION_ROTATE_90 -> 90
        ExifInterface.ORIENTATION_ROTATE_180 -> 180
        ExifInterface.ORIENTATION_ROTATE_270 -> 270
        else -> 0
    }

    private fun rotate(bmp: Bitmap, degrees: Int): Bitmap {
        if (degrees == 0) return bmp
        val m = Matrix().apply { postRotate(degrees.toFloat()) }
        return Bitmap.createBitmap(bmp, 0, 0, bmp.width, bmp.height, m, true).also { if (it !== bmp) bmp.recycle() }
    }
}
