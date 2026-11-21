package com.camstudio.pro.media

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.audiofx.NoiseSuppressor
import android.util.Log
import kotlin.math.log10
import kotlin.math.sqrt

/** 48 kHz mono PCM16 capture in 20 ms frames, with a level meter. */
class AudioCapture(
    private val noiseSuppression: Boolean,
    private val onLevel: (Float) -> Unit,
    private val onFrame: (ptsUs: Long, pcm: ByteArray, len: Int) -> Unit,
) {
    companion object {
        const val SAMPLE_RATE = 48000
        const val FRAME_BYTES = SAMPLE_RATE / 50 * 2
    }

    @Volatile private var running = false
    @Volatile var muted = false
    private var thread: Thread? = null

    @SuppressLint("MissingPermission")
    fun start(): Boolean {
        if (running) return true
        val min = AudioRecord.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
        if (min <= 0) return false
        val source = if (noiseSuppression) MediaRecorder.AudioSource.VOICE_COMMUNICATION else MediaRecorder.AudioSource.MIC
        val rec = try {
            AudioRecord(source, SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, maxOf(min, FRAME_BYTES * 4))
        } catch (e: Exception) {
            Log.w("AudioCapture", "create failed: $e")
            return false
        }
        if (rec.state != AudioRecord.STATE_INITIALIZED) {
            rec.release()
            return false
        }
        var ns: NoiseSuppressor? = null
        if (noiseSuppression && NoiseSuppressor.isAvailable()) {
            ns = runCatching { NoiseSuppressor.create(rec.audioSessionId)?.also { it.enabled = true } }.getOrNull()
        }
        running = true
        thread = Thread({
            val buf = ByteArray(FRAME_BYTES)
            try {
                rec.startRecording()
                var frames = 0L
                val t0 = System.nanoTime() / 1000
                while (running) {
                    var got = 0
                    while (got < buf.size && running) {
                        val n = rec.read(buf, got, buf.size - got)
                        if (n <= 0) break
                        got += n
                    }
                    if (got < buf.size) {
                        if (running) Thread.sleep(5)
                        continue
                    }
                    var sum = 0.0
                    var i = 0
                    while (i < got) {
                        val s = (buf[i].toInt() and 0xff) or (buf[i + 1].toInt() shl 8)
                        sum += s.toDouble() * s
                        i += 2
                    }
                    val rms = sqrt(sum / (got / 2)) / 32768.0
                    // Map -60 dBFS..0 dBFS to 0..1
                    val db = if (rms > 1e-6) 20 * log10(rms) else -90.0
                    onLevel(if (muted) 0f else ((db + 60) / 60).coerceIn(0.0, 1.0).toFloat())
                    if (!muted) onFrame(t0 + frames * 20_000, buf, got)
                    frames++
                }
            } catch (e: Exception) {
                Log.w("AudioCapture", "loop: $e")
            } finally {
                runCatching { rec.stop() }
                runCatching { ns?.release() }
                rec.release()
                onLevel(0f)
            }
        }, "csp-audio").also { it.start() }
        return true
    }

    fun stop() {
        running = false
        thread = null
    }
}
