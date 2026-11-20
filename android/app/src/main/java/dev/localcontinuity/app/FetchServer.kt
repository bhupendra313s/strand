package dev.localcontinuity.app

import android.content.ContentResolver
import android.net.Uri
import android.util.Base64
import android.util.Log
import java.io.BufferedOutputStream
import java.io.InputStream
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.Executors
import javax.net.ssl.SSLServerSocket

/** A screenshot offered to the hub. The bytes stay in MediaStore until the hub fetches them. */
data class ShotEntry(
    val id: String, val uri: Uri, val mime: String, val size: Long, val width: Int, val height: Int,
    val token: String, val createdAt: Long,
)

/**
 * Data plane: a minimal HTTPS/1.1 server that serves `GET /shot/<id>` with `Authorization: Bearer <token>`.
 * Its TLS key is in AndroidKeyStore. The hub pins the cert via the fingerprint sent in `hello`.
 */
class FetchServer(private val resolver: ContentResolver, private val onServed: (ShotEntry) -> Unit = {}) {
    private val shots = LinkedHashMap<String, ShotEntry>()
    private val pool = Executors.newFixedThreadPool(4)

    /** A file QuickDrop offers. [size] is what was announced in file_offer. */
    data class DropSource(val uri: Uri, val size: Long)

    /** QuickDrop data plane: (offer id, file index, bearer token) → file, or null to refuse. Set by the feature. */
    @Volatile var dropResolver: ((String, Int, String) -> DropSource?)? = null

    /** Screen share: (session id, bearer token) → allowed to stream? Set by the feature. */
    @Volatile var screenAuthorizer: ((String, String) -> Boolean)? = null
    private var server: ServerSocket? = null
    private val rng = SecureRandom()

    val port: Int get() = server?.localPort ?: 0

    fun start() {
        val factory = DeviceIdentity.serverSslContext().serverSocketFactory
        val socket = try { factory.createServerSocket(PREFERRED_PORT) } catch (_: Exception) { factory.createServerSocket(0) }
        (socket as SSLServerSocket).needClientAuth = false
        server = socket
        Thread({ acceptLoop(socket) }, "fetch-accept").apply { isDaemon = true }.start()
        Log.i(TAG, "fetch server on :${socket.localPort}")
    }

    fun stop() {
        runCatching { server?.close() }
        server = null
        pool.shutdownNow()
    }

    @Synchronized
    fun offer(uri: Uri, mime: String, size: Long, width: Int, height: Int): ShotEntry {
        val token = ByteArray(32).also(rng::nextBytes).let { Base64.encodeToString(it, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING) }
        val e = ShotEntry(Protocol.newId(), uri, mime, size, width, height, token, Protocol.now())
        shots[e.id] = e
        while (shots.size > MAX_OFFERS) shots.remove(shots.keys.first())
        return e
    }

    @Synchronized
    private fun lookup(id: String, token: String): ShotEntry? {
        val e = shots[id] ?: return null
        if (Protocol.now() - e.createdAt > TOKEN_TTL_MS) { shots.remove(id); return null }
        return if (MessageDigest.isEqual(e.token.toByteArray(), token.toByteArray())) e else null
    }

    private fun acceptLoop(socket: ServerSocket) {
        while (!socket.isClosed) {
            val client = try { socket.accept() } catch (_: SocketException) { break } catch (e: Exception) { Log.w(TAG, "accept: $e"); continue }
            pool.execute { handle(client) }
        }
    }

    private fun handle(client: Socket): Unit = client.use { s ->
        try {
            s.soTimeout = 10_000
            val input = s.getInputStream()
            val requestLine = readLine(input) ?: return
            var auth: String? = null
            var range: String? = null
            while (true) {
                val h = readLine(input) ?: return
                if (h.isEmpty()) break
                val i = h.indexOf(':')
                if (i <= 0) continue
                when (h.substring(0, i).trim().lowercase()) {
                    "authorization" -> auth = h.substring(i + 1).trim()
                    "range" -> range = h.substring(i + 1).trim()
                }
            }
            val out = BufferedOutputStream(s.getOutputStream(), 64 * 1024)
            val parts = requestLine.split(' ')
            if (parts.size >= 2 && parts[0] == "GET" && parts[1].startsWith("/drop/")) return serveDrop(s, out, parts[1], auth, range)
            if (parts.size >= 2 && parts[0] == "GET" && parts[1].startsWith("/screen/")) return serveScreen(s, out, parts[1], auth)
            if (parts.size < 2 || parts[0] != "GET" || !parts[1].startsWith("/shot/")) return respond(out, 404, "Not Found")
            val id = Uri.decode(parts[1].removePrefix("/shot/").substringBefore('?'))
            val token = auth?.takeIf { it.startsWith("Bearer ", true) }?.substring(7)?.trim()
                ?: return respond(out, 401, "Unauthorized")
            val shot = lookup(id, token) ?: return respond(out, 401, "Unauthorized")

            val stream = resolver.openInputStream(shot.uri) ?: return respond(out, 410, "Gone")
            stream.use {
                val bytes = it.readBytes() // screenshots are a few MB; exact Content-Length keeps the client simple
                out.write(("HTTP/1.1 200 OK\r\nContent-Type: ${shot.mime}\r\nContent-Length: ${bytes.size}\r\n" +
                        "Cache-Control: no-store\r\nConnection: close\r\n\r\n").toByteArray())
                out.write(bytes)
                out.flush()
            }
            Log.i(TAG, "served shot ${shot.id} (${shot.size} B)")
            onServed(shot)
        } catch (e: Exception) {
            Log.w(TAG, "request failed: $e")
        }
    }

    /** `GET /drop/<id>/<index>`: streams a QuickDrop file, honouring `Range: bytes=N-` so the PC can resume. */
    private fun serveDrop(s: Socket, out: BufferedOutputStream, path: String, auth: String?, range: String?) {
        val segs = path.removePrefix("/drop/").substringBefore('?').split('/')
        val id = Uri.decode(segs.getOrNull(0) ?: "")
        val index = segs.getOrNull(1)?.toIntOrNull() ?: return respond(out, 404, "Not Found")
        val token = auth?.takeIf { it.startsWith("Bearer ", true) }?.substring(7)?.trim() ?: return respond(out, 401, "Unauthorized")
        val src = dropResolver?.invoke(id, index, token) ?: return respond(out, 401, "Unauthorized")

        val size = src.size
        val start = range?.let { Regex("""bytes=(\d+)-""").find(it)?.groupValues?.get(1)?.toLongOrNull() } ?: 0L
        if (start > 0 && start >= size) {
            out.write("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */$size\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".toByteArray())
            return out.flush()
        }
        val afd = try { resolver.openAssetFileDescriptor(src.uri, "r") } catch (e: Exception) { null } ?: return respond(out, 410, "Gone")
        afd.use {
            val input = it.createInputStream()
            // Seek cheaply when the provider hands us a real file; otherwise skip through the stream.
            if (start > 0) runCatching { input.channel.position(it.startOffset + start) }.getOrElse { var left = start; while (left > 0) { val k = input.skip(left); if (k <= 0) break; left -= k } }
            val head = if (start > 0) "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes $start-${size - 1}/$size\r\n" else "HTTP/1.1 200 OK\r\n"
            out.write(("${head}Content-Type: application/octet-stream\r\nContent-Length: ${size - start}\r\nAccept-Ranges: bytes\r\n" +
                    "Cache-Control: no-store\r\nConnection: close\r\n\r\n").toByteArray())
            s.soTimeout = 30_000
            val buf = ByteArray(64 * 1024)
            var left = size - start
            while (left > 0) {
                val n = input.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
                if (n <= 0) break
                out.write(buf, 0, n); left -= n
            }
            out.flush()
            Log.i(TAG, "served drop $id/$index from $start (${size - start - left} B)")
        }
    }

    /**
     * `GET /screen/<session>`: an endless stream of frames, each a 4-byte big-endian length followed by a JPEG.
     * A zero length is a keep-alive (no new frame for 2 s). Ends when sharing stops or the PC disconnects.
     */
    private fun serveScreen(s: Socket, out: BufferedOutputStream, path: String, auth: String?) {
        val id = Uri.decode(path.removePrefix("/screen/").substringBefore('?'))
        val token = auth?.takeIf { it.startsWith("Bearer ", true) }?.substring(7)?.trim() ?: return respond(out, 401, "Unauthorized")
        if (screenAuthorizer?.invoke(id, token) != true) return respond(out, 401, "Unauthorized")
        out.write(("HTTP/1.1 200 OK\r\nContent-Type: application/vnd.continuity.frames\r\nCache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n").toByteArray())
        out.flush()
        s.tcpNoDelay = true
        s.soTimeout = 0
        val frames = ScreenCaptureService.Frames
        frames.viewers.incrementAndGet()
        Log.i(TAG, "screen viewer connected to ${id.take(8)}")
        var seq = -1L
        var sent = 0
        try {
            while (dev.localcontinuity.app.features.ScreenShareFeature.isLive(id)) {
                val next = frames.next(seq, 2_000)
                val bytes = next?.second
                if (next != null) seq = next.first
                val len = bytes?.size ?: 0
                out.write(byteArrayOf((len ushr 24).toByte(), (len ushr 16).toByte(), (len ushr 8).toByte(), len.toByte()))
                if (bytes != null) { out.write(bytes); sent++ }
                out.flush()
            }
        } catch (e: Exception) {
            Log.i(TAG, "screen viewer left: $e")
        } finally {
            frames.viewers.decrementAndGet()
            Log.i(TAG, "screen stream ${id.take(8)} ended after $sent frames")
        }
    }

    private fun respond(out: BufferedOutputStream, code: Int, msg: String) {
        out.write("HTTP/1.1 $code $msg\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".toByteArray())
        out.flush()
    }

    /** Reads one CRLF-terminated ASCII line (max 8 KiB). */
    private fun readLine(input: InputStream): String? {
        val sb = StringBuilder()
        while (sb.length < 8192) {
            val c = input.read()
            if (c == -1) return if (sb.isEmpty()) null else sb.toString()
            if (c == '\n'.code) return sb.toString().trimEnd('\r')
            sb.append(c.toChar())
        }
        return null
    }

    companion object {
        private const val TAG = "FetchServer"
        const val PREFERRED_PORT = 47801
        private const val MAX_OFFERS = 5
        private const val TOKEN_TTL_MS = 10 * 60 * 1000L
    }
}
