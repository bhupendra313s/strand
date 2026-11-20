package dev.localcontinuity.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import dev.localcontinuity.app.features.FeatureContext
import dev.localcontinuity.app.features.FeatureRegistry
import dev.localcontinuity.app.features.SyncFeature
import org.json.JSONObject

/**
 * Foreground service (type connectedDevice). Owns the infrastructure: hub connection, mDNS discovery,
 * and the lazy-fetch HTTPS server. Everything user-facing lives in features (see [SyncFeature]).
 */
class SyncService : Service(), HubConnection.Listener {

    private lateinit var store: PairingStore
    private lateinit var fetch: FetchServer
    private lateinit var featureCtx: FeatureContext
    private val running = mutableSetOf<String>()
    private var hub: HubConnection? = null
    private var discovery: HubDiscovery? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private val main = Handler(Looper.getMainLooper())
    private val netCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) { main.post { hub?.kick() } }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        store = PairingStore(this)
        startInForeground("Starting…")

        if (!store.isPaired) {
            SyncState.update { SyncStatus(Phase.NOT_PAIRED, "Not paired") }
            stopSelf(); return
        }

        fetch = FetchServer(contentResolver) { shot -> dev.localcontinuity.app.features.ScreenshotFeature.onServed(shot) }.apply { start() }
        featureCtx = FeatureContext(
            app = applicationContext, store = store, guard = LoopGuard(store.deviceId), fetch = fetch, main = main,
            sender = { json -> hub?.send(json) == true },
            updateNotification = ::updateNotification,
        )
        FeatureRegistry.load(this)
        FeatureRegistry.all.filter { FeatureRegistry.isEnabled(this, it.id) }.forEach(::startFeature)
        FeatureRegistry.onToggled = { f, on -> main.post { if (on) startFeature(f) else stopFeature(f); reconnectToRenegotiate() } }

        hub = HubConnection(store, { fetch.port }, { FeatureRegistry.enabledIds(this) }, this, ::helloExtras).also { it.start() }
        discovery = store.hubId?.let { id -> HubDiscovery(this, id) { host, port -> hub?.onDiscovered(host, port) } }

        @Suppress("DEPRECATION")
        wifiLock = getSystemService(WifiManager::class.java)
            .createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "continuity:sync").apply { setReferenceCounted(false); acquire() }
        getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(netCallback)
        instance = this
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_QUICKDROP) {
            val clip = intent.clipData
            val uris = if (clip == null) emptyList() else (0 until clip.itemCount).mapNotNull { clip.getItemAt(it).uri }
            if (uris.isNotEmpty()) dev.localcontinuity.app.features.QuickDropFeature.sendAsync(this, uris)
        }
        return START_STICKY
    }

    override fun onDestroy() {
        instance = null
        FeatureRegistry.onToggled = null
        FeatureRegistry.all.forEach(::stopFeature)
        hub?.stop(); hub = null
        discovery?.stop(); discovery = null
        if (::fetch.isInitialized) fetch.stop()
        runCatching { getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(netCallback) }
        wifiLock?.release()
        SyncState.update { if (it.phase == Phase.ERROR || it.phase == Phase.NOT_PAIRED) it else it.copy(phase = Phase.STOPPED, detail = "Sync is off") }
        super.onDestroy()
    }

    // ------------------------------------------------------------ features

    private fun startFeature(f: SyncFeature) {
        if (running.add(f.id)) runCatching { f.start(featureCtx) }.onFailure { running.remove(f.id) }
    }

    private fun stopFeature(f: SyncFeature) {
        if (running.remove(f.id)) runCatching { f.stop() }
    }

    private fun helloExtras(): JSONObject = JSONObject().apply {
        dev.localcontinuity.app.ui.PhonePalette.json(this@SyncService)?.let { put("palette", it) }
        put("clip_directions", dev.localcontinuity.app.features.ClipboardFeature.directionsJson(this@SyncService))
    }

    /** Re-send hello (e.g. clipboard directions changed). */
    fun renegotiate() = main.post { reconnectToRenegotiate() }

    /** The feature list is negotiated in hello, so reconnect after a switch flips. */
    private fun reconnectToRenegotiate() { hub?.let { it.stop(); hub = HubConnection(store, { fetch.port }, { FeatureRegistry.enabledIds(this) }, this, ::helloExtras).also { h -> h.start() } } }

    // ------------------------------------------------------------ HubConnection.Listener

    override fun onState(connected: Boolean, detail: String) {
        SyncState.update {
            it.copy(phase = if (connected) Phase.CONNECTED else Phase.CONNECTING, detail = detail,
                hubName = store.hubName, hubFeatures = if (connected) it.hubFeatures else emptySet())
        }
        updateNotification(detail)
        if (connected) discovery?.stop()
    }

    override fun onWelcome(hubName: String, hubFeatures: Set<String>) {
        SyncState.update { it.copy(hubName = hubName, hubFeatures = hubFeatures) }
        // onState(connected) runs right after this; let it land before re-offering a live screen share.
        main.post { dev.localcontinuity.app.features.ScreenShareFeature.onReconnected() }
    }

    override fun onMessage(type: String, msg: JSONObject) {
        FeatureRegistry.all.filter { it.id in running && type in it.messageTypes }.forEach { it.onMessage(type, msg) }
    }

    override fun onNeedsDiscovery() { discovery?.start() }

    override fun onFatal(code: String, message: String) {
        val detail = when (code) {
            "pairing_closed" -> "That QR code was already used or has expired. Show a new one on the PC and scan it again."
            "unauthorized" -> "The PC doesn't recognise this phone any more. Pair again."
            else -> "Error: $message"
        }
        SyncState.update { it.copy(phase = Phase.ERROR, detail = detail) }
        updateNotification(detail)
        stopSelf()
    }

    // ------------------------------------------------------------ notification

    private fun buildNotification(text: String): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL) == null)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.channel_name), NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val send = PendingIntent.getActivity(
            this, 1, Intent(this, ClipSendActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_sync)
            .setContentTitle(SyncState.status.value.hubName?.let { "Strand · $it" } ?: "Strand")
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setContentIntent(open)
            .addAction(R.drawable.ic_stat_sync, "Send clipboard", send)
            .build()
    }

    private fun startInForeground(text: String) {
        ServiceCompat.startForeground(this, NOTIF_ID, buildNotification(text), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
    }

    private fun updateNotification(text: String) {
        runCatching { getSystemService(NotificationManager::class.java).notify(NOTIF_ID, buildNotification(text)) }
    }

    companion object {
        /** From [QuickDropShareActivity]: ClipData holds the shared URIs, with read grants for this service. */
        const val ACTION_QUICKDROP = "dev.localcontinuity.app.QUICKDROP"
        private const val CHANNEL = "sync"
        private const val NOTIF_ID = 1
        @Volatile var instance: SyncService? = null; private set
        val isRunning: Boolean get() = instance != null

        fun start(ctx: Context) {
            SyncState.update { it.copy(phase = Phase.CONNECTING, detail = "Starting…") }
            ctx.startForegroundService(Intent(ctx, SyncService::class.java))
        }
        fun stop(ctx: Context) = ctx.stopService(Intent(ctx, SyncService::class.java))
    }
}
