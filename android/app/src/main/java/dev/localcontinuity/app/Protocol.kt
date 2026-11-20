package dev.localcontinuity.app

import org.json.JSONException
import org.json.JSONObject
import java.security.MessageDigest
import java.util.UUID

/** Wire protocol v1 — mirrors PROTOCOL.md and windows/ContinuityHub/Core/Protocol.cs. */
object Protocol {
    const val MAX_TEXT_BYTES = 1024 * 1024

    fun now(): Long = System.currentTimeMillis()
    fun newId(): String = UUID.randomUUID().toString().replace("-", "")

    fun hello(
        deviceId: String, deviceName: String, fetchPort: Int, fetchFingerprint: String,
        deviceToken: String?, pairCode: String?, features: List<String>, appVersion: String,
        extras: JSONObject? = null,
    ): String = JSONObject().apply {
        // Optional extras (PROTOCOL.md §3): "palette" (Material You colours) and "clip_directions".
        extras?.keys()?.forEach { put(it, extras.get(it)) }
        put("type", "hello")
        put("device_id", deviceId)
        put("device_name", deviceName)
        put("fetch_port", fetchPort)
        put("fetch_fingerprint", fetchFingerprint)
        if (deviceToken != null) put("device_token", deviceToken) else if (pairCode != null) put("pair_code", pairCode)
        put("features", org.json.JSONArray(features))
        put("app_version", appVersion)
    }.toString()

    /** Feature ids a peer announced; peers that send none are v1 and support the v1 set. */
    fun features(o: JSONObject): Set<String> {
        val arr = o.optJSONArray("features") ?: return setOf("clip_text", "screenshot")
        return (0 until arr.length()).map { arr.optString(it) }.filter { it.isNotEmpty() }.toSet()
    }

    fun clipText(id: String, origin: String, ts: Long, text: String): String = JSONObject().apply {
        put("type", "clip_text"); put("id", id); put("origin_device_id", origin); put("timestamp", ts); put("text", text)
    }.toString()

    fun screenshotReady(shot: ShotEntry, origin: String): String = JSONObject().apply {
        put("type", "screenshot_ready"); put("id", shot.id); put("origin_device_id", origin); put("timestamp", shot.createdAt)
        put("mime", shot.mime); put("size", shot.size); put("width", shot.width); put("height", shot.height)
        put("token", shot.token)
    }.toString()

    /** QuickDrop (PROTOCOL.md §6). [files] are (relative '/'-path, size). */
    fun fileOffer(id: String, origin: String, ts: Long, token: String, label: String, files: List<Pair<String, Long>>): String =
        JSONObject().apply {
            put("type", "file_offer"); put("id", id); put("origin_device_id", origin); put("timestamp", ts)
            put("token", token); put("label", label); put("total_size", files.sumOf { it.second })
            put("files", org.json.JSONArray(files.map { JSONObject().put("path", it.first).put("size", it.second) }))
        }.toString()

    /** Receiver → sender progress. No "id" envelope on purpose: repeats must not be dropped by the loop guard. */
    fun fileStatus(offerId: String, state: String, done: Long, total: Long, filesDone: Int, fileCount: Int, message: String?): String =
        JSONObject().apply {
            put("type", "file_status"); put("offer_id", offerId); put("state", state); put("done_bytes", done)
            put("total_bytes", total); put("files_done", filesDone); put("file_count", fileCount)
            if (message != null) put("message", message)
        }.toString()

    /** Screen share (PROTOCOL.md §7): the PC pulls JPEG frames from `GET /screen/<id>`. */
    fun screenOffer(id: String, origin: String, ts: Long, token: String, width: Int, height: Int): String = JSONObject().apply {
        put("type", "screen_offer"); put("id", id); put("origin_device_id", origin); put("timestamp", ts)
        put("token", token); put("width", width); put("height", height)
    }.toString()

    fun screenStop(sessionId: String): String = JSONObject().put("type", "screen_stop").put("session_id", sessionId).toString()

    fun parse(json: String): JSONObject? = try { JSONObject(json) } catch (_: JSONException) { null }
}

/** Contents of the pairing QR shown by the Windows hub. */
data class PairingInfo(
    val ip: String, val port: Int, val fingerprint: String, val hubId: String, val name: String, val pairCode: String,
) {
    companion object {
        fun parse(raw: String): PairingInfo? {
            val o = Protocol.parse(raw.trim()) ?: return null
            val fp = o.optString("fingerprint").lowercase()
            if (o.optInt("v") != 1 || fp.length != 64 || !fp.all { it in '0'..'9' || it in 'a'..'f' }) return null
            val ip = o.optString("ip"); val port = o.optInt("port")
            if (ip.isEmpty() || port !in 1..65535) return null
            return PairingInfo(ip, port, fp, o.optString("hub_id"), o.optString("name", "PC"), o.optString("pair_code"))
        }
    }
}

fun sha256Hex(bytes: ByteArray): String =
    MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }

fun sha256Hex(text: String): String = sha256Hex(text.toByteArray(Charsets.UTF_8))
