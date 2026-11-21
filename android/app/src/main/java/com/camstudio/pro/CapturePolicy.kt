package com.camstudio.pro

/** Link state alone never owns the camera. QR scanning is an explicit local action. */
object CapturePolicy {
    fun wantsCamera(permission: Boolean, enabled: Boolean, activity: Boolean,
                    previewUsers: Int, connected: Boolean, scanningQr: Boolean): Boolean =
        permission && ((activity && scanningQr) ||
            (enabled && ((activity && previewUsers > 0) || connected)))
}
