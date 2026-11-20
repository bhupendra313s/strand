package dev.localcontinuity.app

import android.app.Activity
import android.content.ClipData
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.widget.Toast
import androidx.core.content.IntentCompat
import dev.localcontinuity.app.features.ClipboardFeature

/**
 * Share target "QuickDrop to PC" for one or many files. It hands the URIs to [SyncService] with
 * FLAG_GRANT_READ_URI_PERMISSION: a share grant lasts only as long as the receiving component, and the
 * PC may fetch minutes later (or resume after a drop), so the long-running service has to own the grant.
 */
class QuickDropShareActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val uris: List<Uri> = when (intent?.action) {
            Intent.ACTION_SEND -> listOfNotNull(IntentCompat.getParcelableExtra(intent, Intent.EXTRA_STREAM, Uri::class.java))
            Intent.ACTION_SEND_MULTIPLE -> IntentCompat.getParcelableArrayListExtra(intent, Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
            else -> emptyList()
        }
        val message = when {
            !SyncService.isRunning -> "Strand isn't running"
            uris.isEmpty() -> intent?.getStringExtra(Intent.EXTRA_TEXT)?.let { ClipboardFeature.send(it) } ?: "Nothing to send"
            else -> {
                val clip = ClipData.newRawUri("QuickDrop", uris[0]).apply { uris.drop(1).forEach { addItem(ClipData.Item(it)) } }
                val forward = Intent(this, SyncService::class.java).setAction(SyncService.ACTION_QUICKDROP)
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                forward.clipData = clip
                startService(forward)
                null // the service toasts the result
            }
        }
        if (message != null) Toast.makeText(applicationContext, message, Toast.LENGTH_SHORT).show()
        finish()
        @Suppress("DEPRECATION") overridePendingTransition(0, 0)
    }
}
