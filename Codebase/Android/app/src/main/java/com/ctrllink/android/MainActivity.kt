package com.ctrllink.android

import android.Manifest
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.ctrllink.android.ui.App
import com.ctrllink.android.ui.ClipTheme
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private val invite = MutableStateFlow<String?>(null)
    private val cm by lazy { getSystemService(ClipboardManager::class.java) }
    private val clipListener = ClipboardManager.OnPrimaryClipChangedListener {
        if (Hub.store.settings.value.autoSend) {
            val clip = cm.primaryClip
            lifecycleScope.launch { Send.clipboard(this@MainActivity, clip) }
        }
    }
    private val askNotifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) {}

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        ContextCompat.startForegroundService(this, Intent(this, ClipService::class.java))
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        handle(intent)
        setContent { ClipTheme { App(invite) } }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    private fun handle(i: Intent?) {
        i?.dataString?.takeIf { it.startsWith("ctrllink://pair") }?.let { invite.value = it }
    }

    /** Catch up on anything copied while we were away; [Send] dedupes by fingerprint. */
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || !Hub.store.settings.value.autoSend) return
        val clip = cm.primaryClip
        lifecycleScope.launch {
            val msg = Send.clipboard(this@MainActivity, clip)
            if (msg.startsWith("Sent to")) Toast.makeText(this@MainActivity, msg, Toast.LENGTH_SHORT).show()
        }
    }

    override fun onResume() {
        super.onResume()
        cm.addPrimaryClipChangedListener(clipListener)
    }

    override fun onPause() {
        cm.removePrimaryClipChangedListener(clipListener)
        super.onPause()
    }
}
