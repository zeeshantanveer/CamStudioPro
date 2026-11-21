package com.camstudio.pro.net

/** Wire constants from docs/PROTOCOL.md (v1). */
object Proto {
    const val VERSION = 1
    const val UDP_PORT = 47800
    const val TCP_PORT = 47801
    const val MAX_PAYLOAD = 8 * 1024 * 1024

    const val HELLO = 0x01
    const val HELLO_ACK = 0x02
    const val VIDEO_CONFIG = 0x03
    const val VIDEO_FRAME = 0x04
    const val AUDIO_CONFIG = 0x05
    const val AUDIO_FRAME = 0x06
    const val PING = 0x07
    const val PONG = 0x08
    const val STATE = 0x09
    const val STATS = 0x0A
    const val CONTROL = 0x0B

    const val FLAG_KEY = 1
    const val FLAG_CONFIG = 2
}

/** A desktop client we can connect to. */
data class PcTarget(
    val hosts: List<String>,
    val port: Int = Proto.TCP_PORT,
    val name: String = "",
    val os: String = "",
    val id: String = "",
    val pairKey: String? = null,
    val transport: String = "wifi",
)

fun parsePairUri(text: String): PcTarget? {
    val uri = runCatching { android.net.Uri.parse(text.trim()) }.getOrNull() ?: return null
    if (uri.scheme != "camstudio" || uri.host != "pair") return null
    val hosts = uri.getQueryParameter("h")?.split(',')?.map { it.trim() }?.filter { it.isNotEmpty() }
    if (hosts.isNullOrEmpty()) return null
    return PcTarget(
        hosts = hosts,
        port = uri.getQueryParameter("p")?.toIntOrNull() ?: Proto.TCP_PORT,
        name = uri.getQueryParameter("n") ?: hosts.first(),
        id = uri.getQueryParameter("id") ?: "",
        pairKey = uri.getQueryParameter("k"),
    )
}
