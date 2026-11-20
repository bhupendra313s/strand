package dev.localcontinuity.app

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log
import java.net.Inet4Address

/**
 * Browses _continuity._tcp via the OS mDNS stack and reports the address of *our* paired hub
 * (matched by TXT id). The pinned fingerprint still guards the connection, so spoofed answers are harmless.
 */
class HubDiscovery(context: Context, private val hubId: String, private val onFound: (String, Int) -> Unit) {
    private val nsd = context.getSystemService(NsdManager::class.java)
    private val main = Handler(Looper.getMainLooper())
    private var listener: NsdManager.DiscoveryListener? = null
    private var resolving = false
    private val queue = ArrayDeque<NsdServiceInfo>()

    fun start() {
        if (listener != null) return
        val l = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {}
            override fun onDiscoveryStopped(serviceType: String) {}
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) { Log.w(TAG, "discovery failed $errorCode"); main.post { listener = null } }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
            override fun onServiceLost(serviceInfo: NsdServiceInfo) {}
            override fun onServiceFound(serviceInfo: NsdServiceInfo) { main.post { queue.addLast(serviceInfo); resolveNext() } }
        }
        listener = l
        nsd.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, l)
    }

    fun stop() {
        listener?.let { runCatching { nsd.stopServiceDiscovery(it) } }
        listener = null
        queue.clear()
    }

    @Suppress("DEPRECATION") // resolveService is the only option below API 34 and still works above it
    private fun resolveNext() {
        if (resolving || listener == null) return
        val next = queue.removeFirstOrNull() ?: return
        resolving = true
        nsd.resolveService(next, object : NsdManager.ResolveListener {
            override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                main.post { resolving = false; resolveNext() }
            }
            override fun onServiceResolved(info: NsdServiceInfo) {
                main.post {
                    resolving = false
                    val id = info.attributes["id"]?.toString(Charsets.UTF_8)
                    val host = pickHost(info)
                    if (id == hubId && host != null) onFound(host, info.port)
                    resolveNext()
                }
            }
        })
    }

    @Suppress("DEPRECATION")
    private fun pickHost(info: NsdServiceInfo): String? {
        val addrs = if (Build.VERSION.SDK_INT >= 34) info.hostAddresses else listOfNotNull(info.host)
        return (addrs.firstOrNull { it is Inet4Address } ?: addrs.firstOrNull())?.hostAddress
    }

    companion object {
        const val SERVICE_TYPE = "_continuity._tcp."
        private const val TAG = "HubDiscovery"
    }
}
