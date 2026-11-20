package dev.localcontinuity.app

import android.accessibilityservice.AccessibilityService
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.util.Log
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityWindowInfo
import dev.localcontinuity.app.features.ClipboardFeature
import dev.localcontinuity.app.features.FeatureRegistry

/**
 * Automatic phone → PC text ("Auto-send copied text").
 *
 * Android 10+ won't let a background app read the clipboard, and an accessibility service can't either.
 * This service only works out *that* a copy just happened, then opens the invisible [ClipSendActivity]
 * for a moment. The activity has focus, so it's allowed to read the clip, and it sends it like the
 * Quick Settings tile does.
 *
 * Copy signals:
 *  1. System UI's clipboard preview window (Android 13+) appears.
 *  2. System UI announces/updates something mentioning the clipboard ("Copied").
 *  3. A "Copy" / "Cut" button is clicked (text-selection toolbar, app copy buttons).
 *
 * Debug: `adb shell setprop log.tag.CopyDetector DEBUG` logs event types and System UI window info.
 */
class CopyDetectorService : AccessibilityService() {
    private val main = Handler(Looper.getMainLooper())
    private var overlayShown = false
    private var lastTrigger = 0L

    override fun onAccessibilityEvent(event: AccessibilityEvent) {
        val pkg = event.packageName?.toString()
        val debug = Log.isLoggable(TAG, Log.DEBUG)
        if (debug && (pkg == SYSTEM_UI || event.eventType == AccessibilityEvent.TYPE_VIEW_CLICKED ||
                event.eventType == AccessibilityEvent.TYPE_ANNOUNCEMENT)) {
            Log.d(TAG, "event ${AccessibilityEvent.eventTypeToString(event.eventType)} pkg=$pkg cls=${event.className} " +
                "srcId=${event.source?.viewIdResourceName} isCopyWord=${isCopyWord(event)}")
        }

        when (event.eventType) {
            AccessibilityEvent.TYPE_VIEW_CLICKED ->
                // The app writes the clip after the click handler runs; give it a moment.
                if (isCopyWord(event)) trigger("copy button", delayMs = 350)

            AccessibilityEvent.TYPE_ANNOUNCEMENT ->
                if (pkg == SYSTEM_UI && mentionsClipboard(event)) trigger("system announcement")

            else -> if (pkg == SYSTEM_UI || event.eventType == AccessibilityEvent.TYPE_WINDOWS_CHANGED) {
                if (pkg == SYSTEM_UI && event.eventType == AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED &&
                    mentionsClipboard(event)) trigger("system window")
                val shown = clipboardOverlayVisible(debug)
                if (shown && !overlayShown) trigger("clipboard preview")
                overlayShown = shown
            }
        }
    }

    private fun isCopyWord(e: AccessibilityEvent): Boolean {
        val words = e.text.map { it.toString() } + listOfNotNull(e.contentDescription?.toString(), e.source?.text?.toString(),
            e.source?.contentDescription?.toString())
        return words.any { it.trim().lowercase() in COPY_WORDS } ||
            e.source?.viewIdResourceName?.let { it.endsWith("id/copy") || it.endsWith(":id/cut") } == true
    }

    private fun mentionsClipboard(e: AccessibilityEvent): Boolean {
        val text = (e.text.joinToString(" ") + " " + (e.contentDescription ?: "") + " " + (e.className ?: "")).lowercase()
        return "clipboard" in text || "copied" in text
    }

    private fun clipboardOverlayVisible(debug: Boolean): Boolean = try {
        windows.any { w ->
            if (debug && w.type == AccessibilityWindowInfo.TYPE_SYSTEM)
                Log.d(TAG, "win title=${w.title} root=${w.root?.packageName}/${w.root?.viewIdResourceName}")
            isClipboardOverlay(w)
        }
    } catch (e: Exception) {
        Log.w(TAG, "window scan failed", e); false
    }

    private fun isClipboardOverlay(w: AccessibilityWindowInfo): Boolean {
        if (w.title?.toString()?.contains("clipboard", ignoreCase = true) == true) return true
        val root = w.root ?: return false
        try {
            if (root.packageName != SYSTEM_UI) return false
            if (root.viewIdResourceName?.contains("clipboard") == true) return true
            return OVERLAY_IDS.any { id -> root.findAccessibilityNodeInfosByViewId("$SYSTEM_UI:id/$id").isNotEmpty() }
        } finally {
            @Suppress("DEPRECATION") root.recycle()
        }
    }

    private fun trigger(why: String, delayMs: Long = 0) {
        val now = SystemClock.elapsedRealtime()
        if (now - lastTrigger < 1500) return   // several signals usually fire for one copy
        lastTrigger = now
        if (!SyncService.isRunning || !SyncState.connected) { Log.i(TAG, "copy seen ($why) but not connected"); return }
        if (!isOn(this)) return   // in-app switch off: behave like before (share / tile / notification)
        if (!FeatureRegistry.isEnabled(this, ClipboardFeature.id)) return
        if (!ClipboardFeature.sendEnabled(this)) return   // Phone → PC switched off
        Log.i(TAG, "copy seen ($why), reading clip")
        // Allowed from the background: accessibility services are bound by the system, which exempts
        // them from background-activity-start limits.
        main.postDelayed({
            startActivity(
                Intent(this, ClipSendActivity::class.java)
                    .putExtra(ClipSendActivity.EXTRA_AUTO, true)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION)
            )
        }, delayMs)
    }

    override fun onServiceConnected() { super.onServiceConnected(); running = true }
    override fun onDestroy() { running = false; super.onDestroy() }
    override fun onInterrupt() {}

    companion object {
        private const val TAG = "CopyDetector"
        private const val SYSTEM_UI = "com.android.systemui"
        /** View ids in System UI's clipboard preview (Android 13+ ClipboardOverlay). */
        private val OVERLAY_IDS = listOf("clipboard_preview", "share_chip", "clipboard_ui", "actions_container", "minimized_preview")
        private val COPY_WORDS = setOf("copy", "cut", "copy text", "copy link", "copy link address", "copy message")

        private const val PREF_ON = "auto_send"

        /**
         * True while Android has the service bound. After an app update Android can keep the service in its
         * "enabled" list without rebinding it; the user then has to switch it off and on once.
         */
        @Volatile var running = false
            private set

        /** The in-app on/off switch. The accessibility service must also be enabled for it to work. */
        fun isOn(ctx: Context): Boolean = FeatureRegistry.prefs(ctx).getBoolean(PREF_ON, true)
        fun setOn(ctx: Context, on: Boolean) = FeatureRegistry.prefs(ctx).edit().putBoolean(PREF_ON, on).apply()

        /** True when the accessibility service is switched on in Android's settings. */
        fun isEnabled(ctx: Context): Boolean {
            val me = ComponentName(ctx, CopyDetectorService::class.java)
            val list = Settings.Secure.getString(ctx.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: return false
            return list.split(':').any { ComponentName.unflattenFromString(it) == me }
        }
    }
}
