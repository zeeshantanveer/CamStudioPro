package com.camstudio.pro.media

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.view.Surface
import com.camstudio.pro.net.Proto
import java.nio.ByteBuffer
import java.util.concurrent.ConcurrentHashMap

/** H.264 surface-input encoder. Output is Annex-B, delivered through [sink]. */
class VideoEncoder(
    val width: Int,
    val height: Int,
    val fps: Int,
    bitrate: Int,
    private val lowLatency: Boolean,
    private val sink: (ptsUs: Long, flags: Int, data: ByteBuffer) -> Unit,
) {
    companion object {
        private const val TAG = "VideoEncoder"
        const val MIME = "video/avc"

        fun supports(w: Int, h: Int, fps: Int): Boolean = runCatching {
            MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.any { info ->
                info.isEncoder && info.supportedTypes.any { it.equals(MIME, true) } &&
                    info.getCapabilitiesForType(MIME).videoCapabilities.let {
                        it.isSizeSupported(w, h) && runCatching { it.areSizeAndRateSupported(w, h, fps.toDouble()) }.getOrDefault(false)
                    }
            }
        }.getOrDefault(false)
    }

    private val thread = HandlerThread("csp-encoder").apply { start() }
    private val handler = Handler(thread.looper)
    private var codec: MediaCodec? = null
    var inputSurface: Surface? = null
        private set
    private var config: ByteArray? = null
    private var lastWasConfig = false
    private val submitTimes = ConcurrentHashMap<Long, Long>()
    @Volatile var encodeMs = 0f
        private set
    @Volatile var framesOut = 0L
        private set
    @Volatile private var released = false
    @Volatile var bitrate: Int = bitrate
        private set

    private fun format(full: Boolean): MediaFormat = MediaFormat.createVideoFormat(MIME, width, height).apply {
        setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
        setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
        setInteger(MediaFormat.KEY_FRAME_RATE, fps)
        setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 2)
        if (full) {
            setInteger(MediaFormat.KEY_BITRATE_MODE,
                if (lowLatency) MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR
                else MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_VBR)
            setInteger(MediaFormat.KEY_PRIORITY, 0)
            setInteger("prepend-sps-pps-to-idr-frames", 1)
            if (lowLatency) setInteger(MediaFormat.KEY_LATENCY, 1)
            if (Build.VERSION.SDK_INT >= 29) setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
            if (Build.VERSION.SDK_INT >= 30 && lowLatency) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        }
    }

    /** Creates and starts the codec. Throws if no configuration is accepted. */
    fun start() {
        var err: Exception? = null
        for (full in listOf(true, false)) {
            var c: MediaCodec? = null
            try {
                c = MediaCodec.createEncoderByType(MIME)
                c.setCallback(callback, handler)
                c.configure(format(full), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                inputSurface = c.createInputSurface()
                c.start()
                codec = c
                Log.i(TAG, "started ${width}x$height@$fps ${bitrate / 1000} kbps full=$full (${c.name})")
                return
            } catch (e: Exception) {
                Log.w(TAG, "configure(full=$full) failed: $e")
                err = e
                runCatching { c?.release() }
            }
        }
        thread.quitSafely()
        throw err ?: IllegalStateException("encoder unavailable")
    }

    /** Called from the GL thread right before a frame with this timestamp is swapped in. */
    fun noteFrame(ptsNs: Long) {
        if (submitTimes.size > 120) submitTimes.clear()
        submitTimes[ptsNs / 1000] = System.nanoTime()
    }

    fun requestKeyframe() {
        val c = codec ?: return
        runCatching { c.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) }) }
    }

    fun updateBitrate(bps: Int) {
        if (bps == bitrate) return
        bitrate = bps
        val c = codec ?: return
        runCatching { c.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, bps) }) }
    }

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(codec: MediaCodec, index: Int) {}

        override fun onOutputBufferAvailable(codec: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            if (released) return
            try {
                val buf = codec.getOutputBuffer(index)
                if (buf != null && info.size > 0) {
                    buf.position(info.offset)
                    buf.limit(info.offset + info.size)
                    if (info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0) {
                        val copy = ByteArray(info.size)
                        buf.get(copy)
                        config = copy
                        sink(info.presentationTimeUs, Proto.FLAG_CONFIG, ByteBuffer.wrap(copy))
                        lastWasConfig = true
                    } else {
                        val key = info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0
                        if (key && !lastWasConfig) {
                            config?.let { sink(info.presentationTimeUs, Proto.FLAG_CONFIG, ByteBuffer.wrap(it)) }
                        }
                        lastWasConfig = false
                        submitTimes.remove(info.presentationTimeUs)?.let { t ->
                            val ms = (System.nanoTime() - t) / 1_000_000f
                            encodeMs = if (encodeMs == 0f) ms else encodeMs * 0.9f + ms * 0.1f
                        }
                        framesOut++
                        sink(info.presentationTimeUs, if (key) Proto.FLAG_KEY else 0, buf)
                    }
                }
                codec.releaseOutputBuffer(index, false)
            } catch (e: Exception) {
                if (!released) Log.w(TAG, "output: $e")
            }
        }

        override fun onError(codec: MediaCodec, e: MediaCodec.CodecException) {
            Log.e(TAG, "codec error: $e")
        }

        override fun onOutputFormatChanged(codec: MediaCodec, format: MediaFormat) {}
    }

    /** Stops the codec. The GL pipeline must have dropped the input surface first. */
    fun stop() {
        if (released) return
        released = true
        val c = codec
        codec = null
        runCatching { c?.stop() }
        runCatching { c?.release() }
        runCatching { inputSurface?.release() }
        inputSurface = null
        thread.quitSafely()
    }
}
