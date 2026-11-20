package dev.localcontinuity.app.features

import android.content.Context
import android.net.Uri
import android.util.Log
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Screenshot
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import dev.localcontinuity.app.ActivityLog
import dev.localcontinuity.app.Direction
import dev.localcontinuity.app.Protocol
import dev.localcontinuity.app.ScreenshotWatcher
import dev.localcontinuity.app.ShotEntry
import dev.localcontinuity.app.ui.SettingSwitch

/** New screenshots are offered to the PC as metadata; bytes move only when the PC pastes. */
object ScreenshotFeature : SyncFeature {
    override val id = "screenshot"
    override val title = "Screenshots"
    override val description = "Take a screenshot, then press Ctrl+V on your PC. It stays on the phone until you paste."
    override val icon = Icons.Rounded.Screenshot
    override val hasSettings = true

    private var ctx: FeatureContext? = null
    private var watcher: ScreenshotWatcher? = null

    override fun start(ctx: FeatureContext) {
        this.ctx = ctx
        watcher = ScreenshotWatcher(ctx.app.contentResolver, ctx.main) { uri, mime, size, w, h -> offer(uri, mime, size, w, h) }
            .also { it.start() }
    }

    override fun stop() {
        watcher?.stop(); watcher = null
        ctx = null
    }

    private fun offer(uri: Uri, mime: String, size: Long, w: Int, h: Int) {
        val c = ctx ?: return
        if (!c.hubSupports(id)) return
        val shot = c.fetch.offer(uri, mime, size, w, h)
        val sent = c.send(Protocol.screenshotReady(shot, c.deviceId))
        Log.i("ScreenshotFeature", "screenshot ${shot.id} offered (sent=$sent)")
        if (!sent) return
        ActivityLog.add(id, Direction.OUT, "Screenshot ready on ${c.hubName}", "Press Ctrl+V on the PC to paste it", image = uri, key = shot.id)
        if (notifyOnPaste(c.app)) c.updateNotification("Screenshot ready. Press Ctrl+V on ${c.hubName}")
    }

    /** Called by FetchServer (background thread) after the PC downloaded a screenshot. */
    fun onServed(shot: ShotEntry) {
        val c = ctx ?: return
        c.main.post {
            ActivityLog.updateByKey(shot.id) { it.copy(title = "Pasted on ${c.hubName}", detail = "Sent ${shot.size / 1024} KB when you pressed Ctrl+V") }
            if (notifyOnPaste(c.app)) c.updateNotification("Screenshot pasted on ${c.hubName}")
        }
    }

    private fun notifyOnPaste(ctx: Context) = FeatureRegistry.prefs(ctx).getBoolean("$id.status_updates", true)

    @Composable
    override fun Settings(ctx: Context) {
        var on by remember { mutableStateOf(notifyOnPaste(ctx)) }
        SettingSwitch(
            title = "Show status in notification",
            subtitle = "\"Ready\" and \"Pasted on PC\" updates in the sync notification",
            checked = on,
            onChange = { on = it; FeatureRegistry.prefs(ctx).edit().putBoolean("$id.status_updates", it).apply() },
        )
    }
}
