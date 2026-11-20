package dev.localcontinuity.app

import android.os.Handler
import android.os.Looper
import android.util.Log
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.concurrent.TimeUnit

/**
 * Control plane: pinned WSS to the hub, with automatic reconnect and backoff.
 * Callbacks run on the main thread.
 */
class HubConnection(
    private val store: PairingStore,
    private val fetchPort: () -> Int,
    private val localFeatures: () -> List<String>,
    private val listener: Listener,
    /** Extra hello fields: Material You palette, clipboard directions. */
    private val extras: () -> org.json.JSONObject? = { null },
) {
    interface Listener {
        fun onState(connected: Boolean, detail: String)
        fun onWelcome(hubName: String, hubFeatures: Set<String>)
        /** Any message other than welcome/error, routed by type to features. */
        fun onMessage(type: String, msg: org.json.JSONObject)
        fun onFatal(code: String, message: String)
        fun onNeedsDiscovery()
    }

    private val main = Handler(Looper.getMainLooper())
    private val client: OkHttpClient
    private var ws: WebSocket? = null
    private var welcomed = false
    private var running = false
    private var backoffMs = 1_000L
    private val reconnect = Runnable { connect() }

    init {
        val tm = PinnedTrustManager(store.hubFingerprint ?: error("not paired"))
        client = OkHttpClient.Builder()
            .sslSocketFactory(tm.sslContext().socketFactory, tm)
            .hostnameVerifier { _, _ -> true } // identity is proven by the pinned cert, not by a hostname
            .connectTimeout(5, TimeUnit.SECONDS)
            .pingInterval(20, TimeUnit.SECONDS)
            .retryOnConnectionFailure(false)
            .proxy(java.net.Proxy.NO_PROXY)
            .build()
    }

    val isConnected: Boolean get() = welcomed

    fun start() { running = true; connect() }

    fun stop() {
        running = false
        main.removeCallbacks(reconnect)
        ws?.close(1000, "bye"); ws = null; welcomed = false
        client.dispatcher.executorService.shutdown()
    }

    /** Called when mDNS finds the hub (possibly at a new address after DHCP renewal). */
    fun onDiscovered(host: String, port: Int) {
        val changed = host != store.hubHost || port != store.hubPort
        store.saveAddress(host, port)
        if (changed) Log.i(TAG, "hub found at new address $host:$port")
        kick()
    }

    /**
     * Retry now instead of waiting out the backoff. Never cancels an attempt already in flight:
     * that attempt may have consumed the one-time pair code.
     */
    fun kick() { if (running && ws == null) { main.removeCallbacks(reconnect); backoffMs = 1_000; connect() } }

    fun send(json: String): Boolean = welcomed && (ws?.send(json) ?: false)

    private fun connect() {
        if (!running) return
        ws?.cancel()
        val host = store.hubHost ?: return
        val url = "wss://${if (host.contains(':')) "[$host]" else host}:${store.hubPort}/ws"
        listener.onState(false, "Connecting to ${store.hubName ?: host}…")
        ws = client.newWebSocket(Request.Builder().url(url).build(), Callbacks())
    }

    private fun scheduleReconnect(reason: String) {
        welcomed = false
        ws = null
        if (!running) return
        listener.onState(false, "Disconnected ($reason). Retrying in ${backoffMs / 1000}s")
        listener.onNeedsDiscovery()
        main.removeCallbacks(reconnect)
        main.postDelayed(reconnect, backoffMs)
        backoffMs = (backoffMs * 2).coerceAtMost(30_000)
    }

    private inner class Callbacks : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            webSocket.send(
                Protocol.hello(
                    store.deviceId, store.deviceName, fetchPort(), DeviceIdentity.fingerprint(),
                    store.deviceToken, store.pairCode, localFeatures(), BuildConfig.VERSION_NAME, extras(),
                )
            )
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            val o = Protocol.parse(text) ?: return
            main.post {
                // A token is valid even if it arrived on a superseded socket; losing it would burn the pair code.
                if (o.optString("type") == "welcome") o.optString("device_token").takeIf { it.isNotEmpty() }?.let { store.saveToken(it) }
                if (webSocket !== ws) return@post
                when (o.optString("type")) {
                    "welcome" -> {
                        welcomed = true; backoffMs = 1_000
                        listener.onWelcome(o.optString("hub_name", "PC"), Protocol.features(o))
                        listener.onState(true, "Connected to ${o.optString("hub_name", "PC")}")
                    }
                    "error" -> {
                        val code = o.optString("code")
                        running = false
                        listener.onFatal(code, o.optString("message"))
                    }
                    else -> if (welcomed) listener.onMessage(o.optString("type"), o)
                }
            }
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            main.post { if (webSocket === ws) scheduleReconnect(reason.ifEmpty { "closed" }) }
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            Log.w(TAG, "ws failure: $t")
            main.post { if (webSocket === ws) scheduleReconnect(t.javaClass.simpleName) }
        }
    }

    companion object { private const val TAG = "HubConnection" }
}
