package dev.localcontinuity.app.features

import android.content.ClipData
import android.content.ClipboardManager
import android.os.PersistableBundle
import android.util.Log
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.ContentPaste
import dev.localcontinuity.app.ActivityLog
import dev.localcontinuity.app.Direction
import dev.localcontinuity.app.Protocol
import dev.localcontinuity.app.sha256Hex
import dev.localcontinuity.app.ui.SettingSwitch
import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import org.json.JSONObject

/** Universal clipboard for text. PC → phone is automatic; phone → PC needs a tap (Android 10+ rule). */
object ClipboardFeature : SyncFeature {
    const val ORIGIN = "continuity.origin"
    const val TS = "continuity.ts"

    override val id = "clip_text"
    override val title = "Clipboard text"
    override val description = "Text you copy on the PC lands on this phone's clipboard. Send text back with one tap."
    override val icon = Icons.Rounded.ContentPaste
    override val messageTypes = setOf("clip_text")
    override val hasSettings = true

    private var ctx: FeatureContext? = null

    // ---- directions: each can be switched off on its own
    private const val TO_PC = "clip_text.to_pc"
    private const val FROM_PC = "clip_text.from_pc"
    fun sendEnabled(c: Context) = FeatureRegistry.prefs(c).getBoolean(TO_PC, true)
    fun receiveEnabled(c: Context) = FeatureRegistry.prefs(c).getBoolean(FROM_PC, true)

    /** Sent in hello, so the hub stops sending its clipboard at all when PC → phone is off. */
    fun directionsJson(c: Context): JSONObject = JSONObject().put("to_pc", sendEnabled(c)).put("from_pc", receiveEnabled(c))

    private fun setDirection(c: Context, key: String, on: Boolean) {
        FeatureRegistry.prefs(c).edit().putBoolean(key, on).apply()
        dev.localcontinuity.app.SyncService.instance?.renegotiate() // re-send hello with the new directions
    }

    @Composable
    override fun Settings(ctx: Context) {
        var toPc by remember { mutableStateOf(sendEnabled(ctx)) }
        var fromPc by remember { mutableStateOf(receiveEnabled(ctx)) }
        SettingSwitch("Phone → PC", "Send what you copy here to your PC", toPc, { toPc = it; setDirection(ctx, TO_PC, it) })
        SettingSwitch("PC → phone", "Put what you copy on the PC on this phone's clipboard", fromPc, { fromPc = it; setDirection(ctx, FROM_PC, it) })
    }

    override fun start(ctx: FeatureContext) { this.ctx = ctx }
    override fun stop() { ctx = null }

    override fun onMessage(type: String, msg: JSONObject) {
        val c = ctx ?: return
        if (!receiveEnabled(c.app)) return // PC → phone switched off (the hub normally doesn't send then)
        val text = msg.optString("text", "")
        val msgId = msg.optString("id"); val origin = msg.optString("origin_device_id"); val ts = msg.optLong("timestamp")
        if (text.isEmpty() || text.toByteArray().size > Protocol.MAX_TEXT_BYTES) return
        if (!c.guard.acceptIncoming(msgId, origin, ts)) return
        c.guard.markAppliedRemote(sha256Hex(text))

        val clip = ClipData.newPlainText("Strand", text)
        clip.description.extras = PersistableBundle().apply { putString(ORIGIN, origin); putLong(TS, ts) }
        // Writing the clipboard from the background is allowed; reading it is not (Android 10+).
        c.app.getSystemService(ClipboardManager::class.java).setPrimaryClip(clip)
        Log.i("ClipboardFeature", "clip_text ${text.length} chars from ${origin.take(8)} applied to clipboard")
        ActivityLog.add(id, Direction.IN, "Copied from ${c.hubName}", ActivityLog.preview(text))
    }

    /** Sends text the user explicitly shared/copied. Returns a short user-facing result. */
    fun send(text: String): String {
        val c = ctx ?: return "Clipboard sync is off or not running"
        if (!sendEnabled(c.app)) return "Phone → PC is off (Features → Clipboard text)"
        if (text.toByteArray().size > Protocol.MAX_TEXT_BYTES) return "Too large to send"
        if (!c.hubSupports(id)) return "Not connected to PC"
        if (!c.guard.shouldBroadcastLocal(sha256Hex(text))) return "Already on ${c.hubName}"
        val ok = c.send(Protocol.clipText(Protocol.newId(), c.deviceId, Protocol.now(), text))
        if (ok) ActivityLog.add(id, Direction.OUT, "Sent to ${c.hubName}", ActivityLog.preview(text))
        return if (ok) "Sent to ${c.hubName}" else "Send failed"
    }

    /** True if [clip] was written by us from a remote payload (don't bounce it back). */
    fun isFromRemote(clip: ClipData?, selfId: String): Boolean {
        val origin = clip?.description?.extras?.getString(ORIGIN) ?: return false
        return origin != selfId
    }
}
