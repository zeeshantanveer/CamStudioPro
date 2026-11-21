package com.camstudio.pro.net

import android.util.Log
import org.json.JSONObject
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.nio.ByteBuffer
import java.util.concurrent.atomic.AtomicLong

class HandshakeException(val reason: String) : IOException(reason)

/** One TCP session with the desktop client. Blocking open(), then reader/writer threads. */
class Session(private val listener: Listener) {
    interface Listener {
        fun onStats(json: JSONObject)
        fun onControl(json: JSONObject)
        fun onRtt(ms: Int)
        /** The send queue backed up: frames were dropped, an IDR is needed, bitrate should fall. */
        fun onCongestion()
        fun onClosed(error: String?)
    }

    private class Msg(val bytes: ByteArray, val video: Boolean)

    @Volatile private var socket: Socket? = null
    private var out: BufferedOutputStream? = null
    private var input: DataInputStream? = null
    private val queue = ArrayDeque<Msg>()
    private val lock = Object()
    private val writeLock = Any()
    @Volatile private var closed = false
    @Volatile private var closeReported = false
    private var videoQueued = 0
    private var dropUntilKey = false
    @Volatile var maxVideoQueue = 12

    @Volatile var connectedHost: String = ""
        private set

    /** Aborts a pending open() from another thread. */
    fun cancel() {
        closed = true
        closeReported = true
        runCatching { socket?.close() }
    }

    val bytesSent = AtomicLong()
    val framesDropped = AtomicLong()

    /** True if the queue held more than a few frames since it was last reset (soft congestion). */
    @Volatile var backlog = false

    /**
     * Connects to the first reachable host, sends HELLO and waits for HELLO_ACK
     * (up to 60 s, because the PC may ask its user). Returns the ACK JSON.
     */
    fun open(target: PcTarget, hello: JSONObject, onWaiting: () -> Unit): JSONObject {
        var last: Exception? = null
        var s: Socket? = null
        val timeout = if (target.hosts.size > 1) 2000 else 4000
        for (h in target.hosts) {
            if (closed) throw IOException("cancelled")
            try {
                val c = Socket()
                c.tcpNoDelay = true
                c.connect(InetSocketAddress(h, target.port), timeout)
                s = c
                connectedHost = h
                break
            } catch (e: Exception) {
                last = e
            }
        }
        if (s == null) throw IOException(last?.message ?: "unreachable")
        socket = s
        if (closed) {
            runCatching { s.close() }
            throw IOException("cancelled")
        }
        out = BufferedOutputStream(s.getOutputStream(), 64 * 1024)
        input = DataInputStream(s.getInputStream().buffered(64 * 1024))
        writeNow(frame(Proto.HELLO, hello.toString().toByteArray()))

        s.soTimeout = 1500
        var waited = 0
        var notified = false
        while (true) {
            try {
                val (type, payload) = readMsg()
                if (type != Proto.HELLO_ACK) continue
                val ack = JSONObject(String(payload, Charsets.UTF_8))
                if (!ack.optBoolean("ok")) throw HandshakeException(ack.optString("reason", "denied"))
                return ack
            } catch (e: SocketTimeoutException) {
                waited += 1500
                if (!notified) {
                    notified = true
                    onWaiting()
                }
                if (waited >= 60_000) throw HandshakeException("timeout")
            }
        }
    }

    fun start() {
        socket?.soTimeout = 5000
        Thread({ readLoop() }, "csp-read").start()
        Thread({ writeLoop() }, "csp-write").start()
    }

    private fun frame(type: Int, payload: ByteArray): ByteArray {
        val b = ByteArray(5 + payload.size)
        b[0] = type.toByte()
        putInt(b, 1, payload.size)
        System.arraycopy(payload, 0, b, 5, payload.size)
        return b
    }

    private fun putInt(b: ByteArray, o: Int, v: Int) {
        b[o] = (v ushr 24).toByte()
        b[o + 1] = (v ushr 16).toByte()
        b[o + 2] = (v ushr 8).toByte()
        b[o + 3] = v.toByte()
    }

    private fun putLong(b: ByteArray, o: Int, v: Long) {
        putInt(b, o, (v ushr 32).toInt())
        putInt(b, o + 4, v.toInt())
    }

    private fun writeNow(bytes: ByteArray) {
        synchronized(writeLock) {
            val o = out ?: throw IOException("closed")
            o.write(bytes)
            o.flush()
        }
        bytesSent.addAndGet(bytes.size.toLong())
    }

    private fun readMsg(): Pair<Int, ByteArray> {
        val i = input ?: throw IOException("closed")
        val type = i.readUnsignedByte()
        val len = i.readInt()
        if (len < 0 || len > Proto.MAX_PAYLOAD) throw IOException("bad length $len")
        val p = ByteArray(len)
        i.readFully(p)
        return type to p
    }

    private fun enqueue(m: Msg) {
        synchronized(lock) {
            if (closed) return
            queue.addLast(m)
            if (m.video) videoQueued++
            lock.notifyAll()
        }
    }

    fun sendJson(type: Int, json: JSONObject) = enqueue(Msg(frame(type, json.toString().toByteArray()), false))

    fun sendControl(cmd: String) = sendJson(Proto.CONTROL, JSONObject().put("cmd", cmd))

    fun dropCaptureQueue() {
        synchronized(lock) {
            queue.removeAll { (it.bytes[0].toInt() and 255) in listOf(
                Proto.VIDEO_FRAME, Proto.VIDEO_CONFIG, Proto.AUDIO_FRAME, Proto.AUDIO_CONFIG) }
            videoQueued = queue.count { it.video }
        }
    }

    fun sendPing() {
        val b = ByteArray(13)
        b[0] = Proto.PING.toByte()
        putInt(b, 1, 8)
        putLong(b, 5, System.nanoTime())
        enqueue(Msg(b, false))
    }

    fun sendAudio(ptsUs: Long, pcm: ByteArray, len: Int) {
        synchronized(lock) { if (queue.size > 200) return }
        val b = ByteArray(5 + 8 + len)
        b[0] = Proto.AUDIO_FRAME.toByte()
        putInt(b, 1, 8 + len)
        putLong(b, 5, ptsUs)
        System.arraycopy(pcm, 0, b, 13, len)
        enqueue(Msg(b, false))
    }

    /** Queues one access unit. Drops to the next IDR instead of letting latency grow. */
    fun sendVideo(ptsUs: Long, flags: Int, data: ByteBuffer) {
        val key = flags and Proto.FLAG_KEY != 0
        val config = flags and Proto.FLAG_CONFIG != 0
        var congested = false
        synchronized(lock) {
            if (closed) return
            if (videoQueued > 4) backlog = true
            if (videoQueued > maxVideoQueue) {
                val it = queue.iterator()
                while (it.hasNext()) {
                    if (it.next().video) {
                        it.remove()
                        framesDropped.incrementAndGet()
                    }
                }
                videoQueued = 0
                dropUntilKey = true
                congested = true
            }
            if (key) dropUntilKey = false
            if (dropUntilKey && !config) {
                framesDropped.incrementAndGet()
            } else {
                val len = data.remaining()
                val b = ByteArray(5 + 9 + len)
                b[0] = Proto.VIDEO_FRAME.toByte()
                putInt(b, 1, 9 + len)
                putLong(b, 5, ptsUs)
                b[13] = flags.toByte()
                data.get(b, 14, len)
                queue.addLast(Msg(b, !config))
                if (!config) videoQueued++
                lock.notifyAll()
            }
        }
        if (congested) listener.onCongestion()
    }

    private fun writeLoop() {
        try {
            while (!closed) {
                var m: Msg? = null
                synchronized(lock) {
                    while (queue.isEmpty() && !closed) lock.wait(500)
                    if (!queue.isEmpty()) {
                        m = queue.removeFirst()
                        if (m!!.video) videoQueued--
                    }
                }
                val msg = m ?: continue
                writeNow(msg.bytes)
            }
        } catch (e: Exception) {
            fail(e.message ?: "write failed")
        }
    }

    private fun readLoop() {
        try {
            while (!closed) {
                val (type, p) = readMsg()
                when (type) {
                    Proto.PONG -> if (p.size >= 8) {
                        val nonce = ByteBuffer.wrap(p).long
                        listener.onRtt(((System.nanoTime() - nonce) / 1_000_000).toInt().coerceAtLeast(0))
                    }
                    Proto.STATS -> runCatching { listener.onStats(JSONObject(String(p, Charsets.UTF_8))) }
                    Proto.CONTROL -> {
                        val json = runCatching { JSONObject(String(p, Charsets.UTF_8)) }.getOrDefault(JSONObject())
                        val cmd = json.optString("cmd")
                        if (cmd == "bye") {
                            fail("The computer ended the session")
                            return
                        }
                        listener.onControl(json)
                    }
                    else -> {}
                }
            }
        } catch (e: SocketTimeoutException) {
            fail("Connection lost")
        } catch (e: Exception) {
            fail(e.message ?: "Connection lost")
        }
    }

    private fun fail(msg: String) {
        if (closed) return
        Log.i("Session", "closed: $msg")
        closeInternal()
        if (!closeReported) {
            closeReported = true
            listener.onClosed(msg)
        }
    }

    private fun closeInternal() {
        closed = true
        synchronized(lock) {
            queue.clear()
            videoQueued = 0
            lock.notifyAll()
        }
        runCatching { socket?.close() }
    }

    /** Local, intentional close. Sends bye first if the socket is still up. */
    fun close(sendBye: Boolean = true) {
        if (closed) return
        closeReported = true
        closed = true
        val bye = frame(Proto.CONTROL, JSONObject().put("cmd", "bye").toString().toByteArray())
        Thread {
            if (sendBye) runCatching { writeNow(bye) }
            closeInternal()
        }.start()
    }
}
