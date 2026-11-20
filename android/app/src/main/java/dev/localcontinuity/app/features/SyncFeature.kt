package dev.localcontinuity.app.features

import android.content.Context
import android.os.Handler
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.vector.ImageVector
import dev.localcontinuity.app.FetchServer
import dev.localcontinuity.app.LoopGuard
import dev.localcontinuity.app.PairingStore
import dev.localcontinuity.app.SyncState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONObject

/**
 * A pluggable sync capability. To add one (files, notifications, media controls, …):
 *  1. implement this interface,
 *  2. add it to [FeatureRegistry.all],
 *  3. add the matching IFeature on Windows (same [id]).
 * Pairing, encryption, discovery, loop prevention, and the UI shell need no changes.
 */
interface SyncFeature {
    /** Stable id; announced in hello so the PC knows what this phone supports. */
    val id: String
    val title: String
    val description: String
    val icon: ImageVector
    /** Message types routed to [onMessage]. */
    val messageTypes: Set<String> get() = emptySet()
    val enabledByDefault: Boolean get() = true

    /** Called (main thread) when sync starts with this feature enabled, or when it's switched on. */
    fun start(ctx: FeatureContext) {}
    fun stop() {}
    fun onMessage(type: String, msg: JSONObject) {}

    /** Optional extra settings shown under the feature's card. */
    @Composable
    fun Settings(ctx: Context) {}
    val hasSettings: Boolean get() = false
}

/** Everything a running feature may use. */
class FeatureContext(
    val app: Context,
    val store: PairingStore,
    val guard: LoopGuard,
    val fetch: FetchServer,
    val main: Handler,
    private val sender: (String) -> Boolean,
    val updateNotification: (String) -> Unit,
) {
    val deviceId: String get() = store.deviceId
    val hubName: String get() = SyncState.status.value.hubName ?: store.hubName ?: "PC"
    fun send(json: String): Boolean = sender(json)
    fun hubSupports(featureId: String) = featureId in SyncState.status.value.hubFeatures
}

/** Registry + persisted on/off state. */
object FeatureRegistry {
    val all: List<SyncFeature> = listOf(ClipboardFeature, ScreenshotFeature, QuickDropFeature, ScreenShareFeature)

    private val _enabled = MutableStateFlow<Map<String, Boolean>>(emptyMap())
    val enabled: StateFlow<Map<String, Boolean>> = _enabled.asStateFlow()

    /** Called by the running service when a switch flips. */
    var onToggled: ((SyncFeature, Boolean) -> Unit)? = null

    fun load(ctx: Context) {
        val p = prefs(ctx)
        _enabled.value = all.associate { it.id to p.getBoolean(it.id, it.enabledByDefault) }
    }

    fun isEnabled(ctx: Context, id: String): Boolean {
        if (_enabled.value.isEmpty()) load(ctx)
        return _enabled.value[id] ?: false
    }

    fun setEnabled(ctx: Context, feature: SyncFeature, on: Boolean) {
        prefs(ctx).edit().putBoolean(feature.id, on).apply()
        _enabled.value = _enabled.value + (feature.id to on)
        onToggled?.invoke(feature, on)
    }

    fun enabledIds(ctx: Context) = all.filter { isEnabled(ctx, it.id) }.map { it.id }

    fun prefs(ctx: Context) = ctx.getSharedPreferences("features", Context.MODE_PRIVATE)
}
