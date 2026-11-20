package dev.localcontinuity.app

import android.content.Context
import android.os.Build
import java.util.UUID

/** Pairing state in app-private SharedPreferences (not backed up: allowBackup=false). */
class PairingStore(context: Context) {
    private val p = context.getSharedPreferences("pairing", Context.MODE_PRIVATE)

    val deviceId: String
        get() = p.getString("device_id", null) ?: UUID.randomUUID().toString().replace("-", "").also {
            p.edit().putString("device_id", it).apply()
        }

    val deviceName: String get() = p.getString("device_name", null) ?: "${Build.MANUFACTURER} ${Build.MODEL}".trim()

    val isPaired: Boolean get() = hubFingerprint != null && (deviceToken != null || pairCode != null)
    val hubId: String? get() = p.getString("hub_id", null)
    val hubName: String? get() = p.getString("hub_name", null)
    val hubFingerprint: String? get() = p.getString("hub_fp", null)
    val hubHost: String? get() = p.getString("hub_host", null)
    val hubPort: Int get() = p.getInt("hub_port", 47800)
    val deviceToken: String? get() = p.getString("device_token", null)
    val pairCode: String? get() = p.getString("pair_code", null)

    fun savePairing(info: PairingInfo) {
        // Re-scanning the same hub must not throw away a working token: the QR's code is single-use,
        // so it is probably already spent.
        val sameHub = info.hubId == hubId && info.fingerprint == hubFingerprint && deviceToken != null
        p.edit()
            .putString("hub_id", info.hubId).putString("hub_name", info.name).putString("hub_fp", info.fingerprint)
            .putString("hub_host", info.ip).putInt("hub_port", info.port)
            .apply {
                if (sameHub) remove("pair_code") else putString("pair_code", info.pairCode).remove("device_token")
            }
            .apply()
    }

    fun saveToken(token: String) { p.edit().putString("device_token", token).remove("pair_code").apply() }
    fun saveAddress(host: String, port: Int) { p.edit().putString("hub_host", host).putInt("hub_port", port).apply() }

    fun clear() {
        p.edit().remove("hub_id").remove("hub_name").remove("hub_fp").remove("hub_host").remove("hub_port")
            .remove("device_token").remove("pair_code").apply()
    }
}
