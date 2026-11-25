package com.ctrllink.android

import android.content.ClipboardManager
import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/** Transparent: Share target, and the clipboard read for the tile and notification (Android 10+ needs window focus). */
class SendActivity : ComponentActivity() {
    private var started = false
    private val share get() = intent.action == Intent.ACTION_SEND || intent.action == Intent.ACTION_SEND_MULTIPLE

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ContextCompat.startForegroundService(this, Intent(this, ClipService::class.java))
        if (share) go { Send.share(this, intent) }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus && !share) {
            val clip = getSystemService(ClipboardManager::class.java).primaryClip
            go { Send.clipboard(this, clip) }
        }
    }

    private fun go(send: suspend () -> String) {
        if (started) return
        started = true
        lifecycleScope.launch {
            // Just started? Give the service a moment to come up and reconnect.
            if (Hub.store.peers.value.isNotEmpty()) withTimeoutOrNull(4000) { Hub.online.first { it.isNotEmpty() } }
            val msg = send()
            // Background mode (a copy in another app) stays silent unless something was actually sent.
            if (!intent.getBooleanExtra(EXTRA_BACKGROUND, false) || msg.startsWith("Sent to")) {
                Toast.makeText(applicationContext, msg, Toast.LENGTH_SHORT).show()
            }
            finish()
            @Suppress("DEPRECATION") overridePendingTransition(0, 0)
        }
    }

    companion object {
        const val ACT_CLIPBOARD = "com.ctrllink.android.SEND_CLIPBOARD"
        const val EXTRA_BACKGROUND = "background"
    }
}
