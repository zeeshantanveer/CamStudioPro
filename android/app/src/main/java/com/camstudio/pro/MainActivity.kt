package com.camstudio.pro

import android.content.Intent
import android.graphics.Color
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.camstudio.pro.net.parsePairUri
import com.camstudio.pro.ui.AppRoot
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private val engine get() = (application as App).engine

    override fun onCreate(savedInstanceState: Bundle?) {
        // Follows the system theme until the UI applies the user's choice (see AppRoot SystemBars).
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.auto(Color.TRANSPARENT, Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.auto(Color.TRANSPARENT, Color.TRANSPARENT),
        )
        super.onCreate(savedInstanceState)
        setContent { AppRoot(engine) }

        // Keep the screen awake while streaming; drop brightness when the user dims it.
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                combine(engine.conn, engine.toggles, engine.cameraEnabled) { c, t, enabled ->
                    (c.status == ConnStatus.Live && enabled) to t.dimmed }
                    .collect { (live, dimmed) ->
                        if (live) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                        else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
                        window.attributes = window.attributes.apply {
                            screenBrightness = if (dimmed) 0.01f else WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
                        }
                    }
            }
        }

        if (savedInstanceState == null) handleIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    /** camstudio://pair?... links (e.g. QR scanned with the system camera app). */
    private fun handleIntent(intent: Intent?) {
        val data = intent?.data?.toString() ?: return
        val target = parsePairUri(data) ?: return
        if (engine.conn.value.status == ConnStatus.Idle) engine.connect(target)
    }

    override fun onStart() {
        super.onStart()
        engine.onActivityStart()
    }

    override fun onStop() {
        super.onStop()
        engine.onActivityStop()
    }
}
