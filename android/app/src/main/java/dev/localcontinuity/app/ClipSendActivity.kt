package dev.localcontinuity.app

import android.app.Activity
import android.content.ClipboardManager
import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import dev.localcontinuity.app.features.ClipboardFeature

/**
 * Invisible activity that sends phone text to the PC.
 * - "Send to PC" in the share sheet (ACTION_SEND) or text-selection menu (PROCESS_TEXT): uses that text.
 * - Notification action / Quick Settings tile: reads the clipboard. Android 10+ only allows that while
 *   an activity of ours has window focus, which is why this activity exists.
 */
class ClipSendActivity : Activity() {
    companion object {
        /** Set by [CopyDetectorService]: quiet unless the text was actually sent. */
        const val EXTRA_AUTO = "dev.localcontinuity.app.AUTO"
        /** ClipDescription.EXTRA_IS_SENSITIVE (API 33) as a literal, so it compiles against minSdk 29. */
        private const val EXTRA_IS_SENSITIVE = "android.content.extra.IS_SENSITIVE"
    }

    private var handled = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val shared = when (intent?.action) {
            Intent.ACTION_SEND -> intent.getStringExtra(Intent.EXTRA_TEXT)
            Intent.ACTION_PROCESS_TEXT -> intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString()
            else -> null
        }
        if (shared != null) { handled = true; finishWith(send(shared)) }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || handled) return
        handled = true
        val auto = intent?.getBooleanExtra(EXTRA_AUTO, false) == true
        val clip = getSystemService(ClipboardManager::class.java).primaryClip
        val text = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(this)?.toString()
        val sensitive = clip?.description?.extras?.getBoolean(EXTRA_IS_SENSITIVE, false) == true
        when {
            text.isNullOrEmpty() -> finishWith(if (auto) null else "Clipboard has no text")
            ClipboardFeature.isFromRemote(clip, PairingStore(this).deviceId) ->
                finishWith(if (auto) null else "That text came from your PC")
            // Password managers mark copies as sensitive; never send those automatically.
            auto && sensitive -> finishWith(null)
            else -> {
                val result = send(text)
                // Auto mode: only speak up when something actually went to the PC.
                finishWith(if (auto && !result.startsWith("Sent")) null else result)
            }
        }
    }

    private fun send(text: String): String =
        if (!SyncService.isRunning) "Strand isn't running" else ClipboardFeature.send(text)

    private fun finishWith(message: String?) {
        if (message != null) Toast.makeText(applicationContext, message, Toast.LENGTH_SHORT).show()
        finish()
        @Suppress("DEPRECATION") overridePendingTransition(0, 0)
    }
}
