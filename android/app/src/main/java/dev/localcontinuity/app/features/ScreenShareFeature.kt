package dev.localcontinuity.app.features

import android.app.Activity
import android.content.Context
import android.media.projection.MediaProjectionManager
import android.util.Base64
import android.util.Log
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.CastConnected
import androidx.compose.material.icons.rounded.ScreenShare
import androidx.compose.material.icons.rounded.StopScreenShare
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import dev.localcontinuity.app.ActivityLog
import dev.localcontinuity.app.Direction
import dev.localcontinuity.app.Protocol
import dev.localcontinuity.app.ScreenCaptureService
import dev.localcontinuity.app.SyncState
import dev.localcontinuity.app.ui.SettingSwitch
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONObject
import java.security.MessageDigest
import java.security.SecureRandom

/**
 * Screen share: mirrors this phone's screen to a viewer window on the PC (PROTOCOL.md §7).
 * The user starts it here (Android asks for consent every time); we announce `screen_offer`, and the PC pulls
 * a stream of JPEG frames from the fetch server (`GET /screen/<session>`). Either side can end it with `screen_stop`.
 */
object ScreenShareFeature : SyncFeature {
    override val id = "screen_share"
    override val title = "Screen share"
    override val description = "Show what's on your phone screen in a window on your PC. Start and stop it here; Android shows a sharing indicator the whole time."
    override val icon = Icons.Rounded.ScreenShare
    override val messageTypes = setOf("screen_stop")
    override val hasSettings = true

    private const val TAG = "ScreenShare"
    private val rng = SecureRandom()

    private data class Session(val id: String, val token: String)
    @Volatile private var session: Session? = null
    @Volatile private var ctx: FeatureContext? = null

    private val _sharing = MutableStateFlow(false)
    val sharing: StateFlow<Boolean> = _sharing.asStateFlow()

    override fun start(ctx: FeatureContext) {
        this.ctx = ctx
        ctx.fetch.screenAuthorizer = ::authorize
    }

    override fun stop() {
        ctx?.fetch?.screenAuthorizer = null
        ctx?.let { ScreenCaptureService.stop(it.app) }
        ctx = null
    }

    override fun onMessage(type: String, msg: JSONObject) {
        // The PC closed its viewer: stop capturing (and the system "sharing" indicator) right away.
        if (type == "screen_stop" && msg.optString("session_id") == session?.id) ctx?.let { ScreenCaptureService.stop(it.app) }
    }

    /** FetchServer asks: may this request stream the given session? */
    private fun authorize(sessionId: String, token: String): Boolean {
        val s = session ?: return false
        return s.id == sessionId && MessageDigest.isEqual(s.token.toByteArray(), token.toByteArray())
    }

    fun isLive(sessionId: String) = session?.id == sessionId

    @Volatile private var lastSize = 0 to 0

    /**
     * The hub connection came back (hub restarted, Wi-Fi blip). If we're still sharing, offer again under a
     * fresh session so the PC reopens its viewer; the old session's stream ends because it's no longer live.
     */
    fun onReconnected() {
        if (session == null || !ScreenCaptureService.running) return
        val (w, h) = lastSize
        Log.i(TAG, "hub reconnected while sharing: re-offering")
        onCaptureStarted(w, h)
    }

    /** Called by [ScreenCaptureService] once capture runs. */
    fun onCaptureStarted(width: Int, height: Int) {
        lastSize = width to height
        val c = ctx
        val token = Base64.encodeToString(ByteArray(32).also(rng::nextBytes), Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
        val s = Session(Protocol.newId(), token)
        session = s
        _sharing.value = true
        val sent = c != null && c.hubSupports(id) && c.send(Protocol.screenOffer(s.id, c.deviceId, Protocol.now(), token, width, height))
        Log.i(TAG, "share ${s.id.take(8)} started ${width}x$height (offered=$sent)")
        c?.main?.post {
            if (sent) {
                ActivityLog.add(id, Direction.OUT, "Sharing screen with ${c.hubName}", "Open on the PC in the Strand viewer", key = s.id)
            } else {
                Toast.makeText(c.app, "Couldn't reach ${c.hubName}. Is Screen share on there?", Toast.LENGTH_LONG).show()
                ScreenCaptureService.stop(c.app)
            }
        }
    }

    /** Called by [ScreenCaptureService] when capture ends for any reason. */
    fun onCaptureStopped() {
        val s = session ?: return
        session = null
        _sharing.value = false
        val c = ctx ?: return
        c.send(Protocol.screenStop(s.id))
        c.main.post { ActivityLog.updateByKey(s.id) { it.copy(title = "Stopped sharing screen", detail = "With ${c.hubName}") } }
    }

    fun highQuality(ctx: Context) = FeatureRegistry.prefs(ctx).getBoolean("$id.high_quality", false)

    /** Returns a function that asks Android for capture consent and then starts sharing. Used by Home and the feature card. */
    @Composable
    fun rememberStartSharing(ctx: Context): () -> Unit {
        val consent = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { r ->
            val data = r.data
            if (r.resultCode == Activity.RESULT_OK && data != null) ScreenCaptureService.start(ctx, r.resultCode, data)
        }
        return {
            if (!FeatureRegistry.isEnabled(ctx, id)) Toast.makeText(ctx, "Turn on Screen share in Features first", Toast.LENGTH_SHORT).show()
            else consent.launch(ctx.getSystemService(MediaProjectionManager::class.java).createScreenCaptureIntent())
        }
    }

    @Composable
    override fun Settings(ctx: Context) {
        val live by sharing.collectAsState()
        val status by SyncState.status.collectAsState()
        val startSharing = rememberStartSharing(ctx)
        var hq by remember { mutableStateOf(highQuality(ctx)) }
        Column {
            if (live) {
                Button(onClick = { ScreenCaptureService.stop(ctx) },
                    colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.error)) {
                    Icon(Icons.Rounded.StopScreenShare, null); Text("  Stop sharing")
                }
            } else {
                Button(
                    onClick = startSharing,
                    enabled = status.connected && id in status.hubFeatures,
                ) { Icon(Icons.Rounded.CastConnected, null); Text("  Share screen with ${status.hubName ?: "PC"}") }
            }
            Spacer(Modifier.height(6.dp))
            SettingSwitch(
                title = "High quality",
                subtitle = "Sharper picture; uses more Wi-Fi. Takes effect next time you start sharing.",
                checked = hq,
                onChange = { hq = it; FeatureRegistry.prefs(ctx).edit().putBoolean("$id.high_quality", it).apply() },
            )
            Text("When Android asks, choose \"Entire screen\". Screens that apps protect (banking, DRM video) show as black on the PC.",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
