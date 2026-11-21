package com.camstudio.pro.net

import android.util.Log
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException

data class DiscoveredPc(val id: String, val name: String, val os: String, val host: String, val port: Int, val seenAt: Long)

/** Broadcasts csp-discover every 2 s and collects csp-announce answers. */
class Discovery(private val onChange: (List<DiscoveredPc>) -> Unit) {
    @Volatile private var running = false
    // Each start() gets a new generation so a thread left over from a quick stop/start exits.
    @Volatile private var generation = 0
    @Volatile private var socket: DatagramSocket? = null
    private val found = LinkedHashMap<String, DiscoveredPc>()

    fun start() {
        if (running) return
        running = true
        val gen = ++generation
        Thread({ run(gen) }, "csp-discovery").start()
    }

    fun stop() {
        running = false
        runCatching { socket?.close() }
        socket = null
    }

    private fun broadcastTargets(): List<InetAddress> {
        val out = ArrayList<InetAddress>()
        runCatching { out.add(InetAddress.getByName("255.255.255.255")) }
        runCatching {
            for (ni in NetworkInterface.getNetworkInterfaces()) {
                if (!ni.isUp || ni.isLoopback) continue
                for (ia in ni.interfaceAddresses) ia.broadcast?.let { out.add(it) }
            }
        }
        return out.distinct()
    }

    private fun run(gen: Int) {
        val payload = JSONObject().put("t", "csp-discover").put("v", Proto.VERSION).toString().toByteArray()
        val buf = ByteArray(2048)
        var lastSend = 0L
        var s: DatagramSocket? = null
        try {
            s = DatagramSocket()
            s.broadcast = true
            s.soTimeout = 500
            socket = s
            while (running && gen == generation) {
                val now = System.currentTimeMillis()
                if (now - lastSend >= 2000) {
                    lastSend = now
                    for (addr in broadcastTargets()) {
                        runCatching { s.send(DatagramPacket(payload, payload.size, addr, Proto.UDP_PORT)) }
                    }
                }
                try {
                    val p = DatagramPacket(buf, buf.size)
                    s.receive(p)
                    val j = JSONObject(String(p.data, 0, p.length, Charsets.UTF_8))
                    val host = p.address.hostAddress
                    if (j.optString("t") == "csp-announce" && host != null) {
                        val id = j.optString("id").ifEmpty { host }
                        val pc = DiscoveredPc(id, j.optString("name", host), j.optString("os"), host,
                            j.optInt("port", Proto.TCP_PORT), System.currentTimeMillis())
                        synchronized(found) { found[id] = pc }
                    }
                } catch (_: SocketTimeoutException) {
                } catch (e: Exception) {
                    if (running) Log.d("Discovery", "recv: $e")
                }
                val cutoff = System.currentTimeMillis() - 7000
                val list = synchronized(found) {
                    found.values.removeAll { it.seenAt < cutoff }
                    found.values.toList()
                }
                onChange(list)
            }
        } catch (e: Exception) {
            if (running) Log.w("Discovery", "stopped: $e")
        } finally {
            runCatching { s?.close() }
        }
    }
}
