package com.camstudio.pro

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat

/**
 * Foreground service that keeps the camera, microphone and network link alive while the app
 * is in the background or the screen is off: while streaming, and while auto-connect waits
 * for the computer. The [Engine] owns all the work; this only holds the foreground state and
 * shows the ongoing notification.
 */
class StreamService : Service() {
    companion object {
        private const val TAG = "StreamService"
        private const val CHANNEL = "link"
        private const val NOTIFICATION_ID = 1
        private const val ACTION_STOP = "com.camstudio.pro.STOP"

        /** Returns false if Android refused to start it (e.g. the app isn't in the foreground). */
        fun start(ctx: Context): Boolean = try {
            ContextCompat.startForegroundService(ctx, Intent(ctx, StreamService::class.java))
            true
        } catch (e: Exception) {
            Log.w(TAG, "start refused: $e")
            false
        }

        fun stop(ctx: Context) {
            ctx.stopService(Intent(ctx, StreamService::class.java))
        }

        /** Updates the notification text for the current connection state. */
        fun refresh(ctx: Context, engine: Engine) {
            if (!canNotify(ctx)) return
            runCatching {
                ctx.getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, build(ctx, engine))
            }
        }

        private fun canNotify(ctx: Context) = Build.VERSION.SDK_INT < 33 ||
            ContextCompat.checkSelfPermission(ctx, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

        private fun build(ctx: Context, engine: Engine): Notification {
            val nm = ctx.getSystemService(NotificationManager::class.java)
            if (nm.getNotificationChannel(CHANNEL) == null) {
                nm.createNotificationChannel(
                    NotificationChannel(CHANNEL, "Connection to your computer", NotificationManager.IMPORTANCE_LOW).apply {
                        description = "Shown while CamStudio streams or waits for your computer"
                        setShowBadge(false)
                    }
                )
            }
            val open = PendingIntent.getActivity(
                ctx, 0,
                Intent(ctx, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
            val stop = PendingIntent.getService(
                ctx, 1,
                Intent(ctx, StreamService::class.java).setAction(ACTION_STOP),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
            val (title, text) = engine.serviceText()
            return NotificationCompat.Builder(ctx, CHANNEL)
                .setSmallIcon(R.drawable.ic_launcher_foreground)
                .setContentTitle(title)
                .setContentText(text)
                .setContentIntent(open)
                .addAction(0, "Stop", stop)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setSilent(true)
                .setCategory(NotificationCompat.CATEGORY_SERVICE)
                .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
                .build()
        }
    }

    private val engine get() = (application as App).engine

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // The notification's Stop action uses a plain service intent; the engine then stops us.
        if (intent?.action == ACTION_STOP) {
            engine.stopFromNotification()
            return START_NOT_STICKY
        }
        // Started with startForegroundService(): must enter the foreground right away.
        if (!enterForeground()) stopSelf()
        return START_NOT_STICKY
    }

    private fun enterForeground(): Boolean {
        var types = 0
        if (Build.VERSION.SDK_INT >= 30) {
            if (granted(Manifest.permission.CAMERA)) types = types or ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA
            if (granted(Manifest.permission.RECORD_AUDIO)) types = types or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
            if (types == 0) return false
        }
        return try {
            ServiceCompat.startForeground(this, NOTIFICATION_ID, build(this, engine), types)
            true
        } catch (e: Exception) {
            // e.g. Android 14+ refuses camera/mic services started from the background.
            Log.w(TAG, "startForeground refused: $e")
            false
        }
    }

    private fun granted(p: String) = ContextCompat.checkSelfPermission(this, p) == PackageManager.PERMISSION_GRANTED

    override fun onDestroy() {
        engine.onServiceStopped()
        super.onDestroy()
    }
}
