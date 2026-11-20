package dev.localcontinuity.app.features

import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.util.Base64
import android.util.Log
import android.webkit.MimeTypeMap
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.CreateNewFolder
import androidx.compose.material.icons.rounded.FileUpload
import androidx.compose.material.icons.rounded.SendToMobile
import androidx.compose.material3.Button
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import dev.localcontinuity.app.ActivityLog
import dev.localcontinuity.app.Direction
import dev.localcontinuity.app.FetchServer
import dev.localcontinuity.app.PinnedTrustManager
import dev.localcontinuity.app.Protocol
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import java.io.FileOutputStream
import java.io.IOException
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/**
 * QuickDrop: files and folders between phone and PC over Wi-Fi (PROTOCOL.md §6).
 * Phone → PC: we announce `file_offer`; the PC pulls each file from [FetchServer] (`/drop/<id>/<i>`).
 * PC → phone: the PC announces; we pull from the hub's HTTPS port and save to Download/QuickDrop.
 * Interrupted downloads continue from the bytes already saved (HTTP Range).
 */
object QuickDropFeature : SyncFeature {
    override val id = "file_drop"
    override val title = "QuickDrop"
    override val description = "Send files and folders between this phone and your PC over Wi-Fi, with progress and resume. " +
        "Share → QuickDrop to PC, or use the buttons below. Received files go to Download/QuickDrop."
    override val icon = Icons.Rounded.SendToMobile
    override val messageTypes = setOf("file_offer", "file_status")
    override val hasSettings = true

    private const val TAG = "QuickDrop"
    private const val TTL_MS = 24 * 3600 * 1000L
    private const val STALL_LIMIT_MS = 15 * 60 * 1000L
    private const val SAVE_DIR = "Download/QuickDrop/"

    /** One file we offer: where to read it, its wire path and announced size. */
    data class Item(val uri: Uri, val path: String, val size: Long)
    private data class Outgoing(val token: String, val items: List<Item>, val created: Long, val label: String)

    private val outgoing = ConcurrentHashMap<String, Outgoing>()
    private val receiving = ConcurrentHashMap.newKeySet<String>()
    private val io = Executors.newCachedThreadPool()
    private val rng = SecureRandom()
    @Volatile private var ctx: FeatureContext? = null

    override fun start(ctx: FeatureContext) {
        this.ctx = ctx
        ctx.fetch.dropResolver = ::resolve
    }

    override fun stop() {
        ctx?.fetch?.dropResolver = null
        ctx = null
    }

    // ------------------------------------------------------------------ phone → PC

    private fun resolve(id: String, index: Int, token: String): FetchServer.DropSource? {
        val o = outgoing[id] ?: return null
        if (Protocol.now() - o.created > TTL_MS) { outgoing.remove(id); return null }
        if (!MessageDigest.isEqual(o.token.toByteArray(), token.toByteArray())) return null
        val item = o.items.getOrNull(index) ?: return null
        return FetchServer.DropSource(item.uri, item.size)
    }

    /** Turns content URIs (files, or folder trees from the picker) into offer items. Runs I/O; call off the main thread. */
    fun collect(context: Context, uris: List<Uri>): List<Item> {
        val out = mutableListOf<Item>()
        for (u in uris) {
            if (DocumentsContract.isTreeUri(u)) {
                val rootDoc = DocumentsContract.getTreeDocumentId(u)
                val rootName = queryName(context, DocumentsContract.buildDocumentUriUsingTree(u, rootDoc)) ?: "Folder"
                walkTree(context, u, rootDoc, sanitize(rootName), out)
            } else {
                val name = sanitize(queryName(context, u) ?: u.lastPathSegment ?: "file")
                out += Item(u, name, querySize(context, u))
            }
        }
        return out
    }

    private fun walkTree(context: Context, tree: Uri, docId: String, prefix: String, out: MutableList<Item>) {
        val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, docId)
        val cols = arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME,
            DocumentsContract.Document.COLUMN_MIME_TYPE, DocumentsContract.Document.COLUMN_SIZE)
        context.contentResolver.query(children, cols, null, null, null)?.use { c ->
            while (c.moveToNext()) {
                val childId = c.getString(0); val name = sanitize(c.getString(1) ?: "file")
                if (c.getString(2) == DocumentsContract.Document.MIME_TYPE_DIR) walkTree(context, tree, childId, "$prefix/$name", out)
                else out += Item(DocumentsContract.buildDocumentUriUsingTree(tree, childId), "$prefix/$name", if (c.isNull(3)) -1 else c.getLong(3))
            }
        }
    }

    /** Offers items to the PC. Returns a short user-facing result. */
    fun send(context: Context, items: List<Item>): String {
        val c = ctx ?: return "QuickDrop is off or sync isn't running"
        if (!c.hubSupports(id)) return "QuickDrop is off on ${c.hubName}, or the PC isn't connected"
        val files = items.map { if (it.size >= 0) it else it.copy(size = querySize(context, it.uri)) }.filter { it.size >= 0 }
        if (files.isEmpty()) return "Nothing to send"

        val token = Base64.encodeToString(ByteArray(32).also(rng::nextBytes), Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
        val tops = files.map { it.path.substringBefore('/') }.distinct()
        val label = when {
            files.size == 1 -> files[0].path.substringAfterLast('/')
            tops.size == 1 && files[0].path.contains('/') -> tops[0]
            else -> "${files.size} files"
        }
        val offerId = Protocol.newId()
        outgoing[offerId] = Outgoing(token, files, Protocol.now(), label)
        outgoing.entries.removeIf { Protocol.now() - it.value.created > TTL_MS }

        val total = files.sumOf { it.size }
        val ok = c.send(Protocol.fileOffer(offerId, c.deviceId, Protocol.now(), token, label, files.map { it.path to it.size }))
        if (!ok) { outgoing.remove(offerId); return "Not connected to ${c.hubName}" }
        Log.i(TAG, "offered $offerId: ${files.size} files, $total B")
        c.main.post {
            ActivityLog.add(id, Direction.OUT, "Sending $label to ${c.hubName}",
                "Waiting for ${c.hubName} · ${fmt(total)}" + (if (files.size > 1) " · ${files.size} files" else ""), key = offerId)
        }
        return "Sending $label to ${c.hubName}"
    }

    // ------------------------------------------------------------------ messages

    override fun onMessage(type: String, msg: JSONObject) {
        when (type) {
            "file_status" -> onStatus(msg)
            "file_offer" -> onOffer(msg)
        }
    }

    private fun onStatus(m: JSONObject) {
        val c = ctx ?: return
        val offerId = m.optString("offer_id"); val o = outgoing[offerId] ?: return
        val done = m.optLong("done_bytes"); val total = m.optLong("total_bytes")
        val fd = m.optInt("files_done"); val fc = m.optInt("file_count")
        val pct = if (total > 0) (done * 100 / total).toInt() else 100
        when (m.optString("state")) {
            "progress" -> ActivityLog.updateByKey(offerId) {
                it.copy(detail = "$pct% · ${fmt(done)} of ${fmt(total)}" + (m.optString("message").takeIf { s -> s.isNotEmpty() }?.let { s -> " · $s" } ?: "") +
                        (if (fc > 1) " · $fd of $fc files" else ""))
            }
            "done" -> {
                ActivityLog.updateByKey(offerId) { it.copy(title = "Sent ${o.label} to ${c.hubName}", detail = "Saved in Downloads\\QuickDrop on the PC · ${fmt(total)}") }
                c.updateNotification("Sent ${o.label} to ${c.hubName}")
            }
            "failed" -> ActivityLog.updateByKey(offerId) { it.copy(detail = "Failed on ${c.hubName}: ${m.optString("message")}") }
        }
    }

    // ------------------------------------------------------------------ PC → phone

    private data class InFile(val path: String, val size: Long)

    private fun onOffer(m: JSONObject) {
        val c = ctx ?: return
        val offerId = m.optString("id"); val token = m.optString("token")
        val arr = m.optJSONArray("files") ?: return
        if (offerId.isEmpty() || token.isEmpty() || arr.length() == 0 || !receiving.add(offerId)) return
        val files = (0 until arr.length()).mapNotNull { i ->
            val f = arr.optJSONObject(i) ?: return@mapNotNull null
            val p = sanitizePath(f.optString("path")) ?: return@mapNotNull null
            InFile(p, f.optLong("size", -1)).takeIf { it.size >= 0 }
        }
        if (files.size != arr.length()) { receiving.remove(offerId); return }
        val label = m.optString("label").ifEmpty { if (files.size == 1) files[0].path.substringAfterLast('/') else "${files.size} files" }
        val total = files.sumOf { it.size }
        ActivityLog.add(id, Direction.IN, "Receiving $label from ${c.hubName}", "Starting · ${fmt(total)}", key = offerId)
        io.execute { Download(c, offerId, token, label, files, total).run() }
    }

    /** Pulls every file of one offer, resuming after drops, and reports progress to the UI and the PC. */
    private class Download(
        val c: FeatureContext, val offerId: String, val token: String, val label: String, val files: List<InFile>, val total: Long,
    ) {
        private val http = PinnedTrustManager(c.store.hubFingerprint ?: error("not paired")).let { tm ->
            OkHttpClient.Builder().sslSocketFactory(tm.sslContext().socketFactory, tm).hostnameVerifier { _, _ -> true }
                .connectTimeout(5, TimeUnit.SECONDS).readTimeout(30, TimeUnit.SECONDS).proxy(java.net.Proxy.NO_PROXY)
                .retryOnConnectionFailure(false).build()
        }
        private var done = 0L
        private var filesDone = 0
        private var rate = 0.0
        private var rateBytes = 0L
        private var rateAt = System.nanoTime()
        private var lastReport = 0L

        fun run() {
            var error: String? = null
            try {
                files.forEachIndexed { i, f -> fetch(i, f); filesDone++; report(force = true) }
            } catch (e: Exception) {
                error = e.message ?: e.javaClass.simpleName
                Log.w(TAG, "download $offerId failed", e)
            }
            receiving.remove(offerId)
            c.send(Protocol.fileStatus(offerId, if (error == null) "done" else "failed", if (error == null) total else done, total,
                filesDone, files.size, error))
            c.main.post {
                if (error == null) {
                    ActivityLog.updateByKey(offerId) { it.copy(title = "Received $label from ${c.hubName}", detail = "Saved to Download/QuickDrop · ${fmt(total)}" +
                            (if (files.size > 1) " · ${files.size} files" else "")) }
                    c.updateNotification("Received $label · saved to Download/QuickDrop")
                } else ActivityLog.updateByKey(offerId) { it.copy(detail = "Failed: $error") }
            }
            http.dispatcher.executorService.shutdown(); http.connectionPool.evictAll()
        }

        private fun fetch(index: Int, f: InFile) {
            val target = pendingItem(index, f)
            val before = done
            var lastProgressAt = System.currentTimeMillis()
            var backoff = 1_000L
            while (true) {
                val had = sizeOf(target)
                try { if (fetchOnce(index, f, target, before)) break }
                catch (e: IOException) {
                    if (sizeOf(target) > had) { lastProgressAt = System.currentTimeMillis(); backoff = 1_000 }
                    if (System.currentTimeMillis() - lastProgressAt > STALL_LIMIT_MS) throw IOException("No progress for 15 minutes: ${e.message}")
                    Log.w(TAG, "file $index: ${e.message}; retrying in ${backoff / 1000}s")
                    Thread.sleep(backoff); backoff = (backoff * 2).coerceAtMost(30_000)
                }
            }
            // Publish: clear IS_PENDING so the file shows up in Files / Downloads.
            c.app.contentResolver.update(target, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
            partials.remove("$offerId:$index")
        }

        /** True when the file is complete. */
        private fun fetchOnce(index: Int, f: InFile, target: Uri, before: Long): Boolean {
            var have = sizeOf(target).coerceAtMost(f.size)
            done = before + have
            if (have == f.size) return true
            val host = c.store.hubHost ?: throw IOException("PC address unknown")
            val url = "https://${if (host.contains(':')) "[$host]" else host}:${c.store.hubPort}/drop/${Uri.encode(offerId)}/$index"
            val req = Request.Builder().url(url).header("Authorization", "Bearer $token")
                .apply { if (have > 0) header("Range", "bytes=$have-") }.build()
            http.newCall(req).execute().use { resp ->
                if (resp.code == 401 || resp.code == 404 || resp.code == 410) throw IllegalStateException("The PC no longer offers this file (${resp.code})")
                if (resp.code == 416 && have == f.size) return true
                if (!resp.isSuccessful) throw IOException("PC answered ${resp.code}")
                val append = resp.code == 206 && resp.header("Content-Range")?.startsWith("bytes $have-") == true
                if (!append) { have = 0; done = before }
                c.app.contentResolver.openFileDescriptor(target, "rw")!!.use { pfd ->
                    FileOutputStream(pfd.fileDescriptor).use { out ->
                        out.channel.truncate(have); out.channel.position(have)
                        val input = resp.body!!.byteStream()
                        val buf = ByteArray(64 * 1024)
                        while (true) {
                            val n = input.read(buf); if (n < 0) break
                            out.write(buf, 0, n); have += n; done += n
                            if (have > f.size) throw IOException("PC sent more bytes than announced")
                            report(current = f.path)
                        }
                    }
                }
            }
            if (have != f.size) throw IOException("Connection ended at $have of ${f.size} bytes")
            return true
        }

        /** A MediaStore Downloads item for this file, created once and reused across retries (resume). */
        private fun pendingItem(index: Int, f: InFile): Uri = partials.getOrPut("$offerId:$index") {
            val name = f.path.substringAfterLast('/')
            val dir = f.path.substringBeforeLast('/', "")
            val mime = MimeTypeMap.getSingleton().getMimeTypeFromExtension(name.substringAfterLast('.', "").lowercase()) ?: "application/octet-stream"
            val values = ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, name)
                put(MediaStore.MediaColumns.MIME_TYPE, mime)
                put(MediaStore.MediaColumns.RELATIVE_PATH, SAVE_DIR + if (dir.isEmpty()) "" else "$dir/")
                put(MediaStore.MediaColumns.IS_PENDING, 1)
            }
            c.app.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: throw IOException("Couldn't create $name in Downloads")
        }

        private fun sizeOf(uri: Uri): Long = runCatching { c.app.contentResolver.openFileDescriptor(uri, "r")!!.use { it.statSize } }.getOrDefault(0L)

        private fun report(force: Boolean = false, current: String? = null) {
            val now = System.nanoTime()
            val dt = (now - rateAt) / 1e9
            if (dt >= 0.5) {
                val inst = (done - rateBytes) / dt
                rate = if (rate == 0.0) inst else rate * 0.6 + inst * 0.4
                rateBytes = done; rateAt = now
            }
            if (!force && (now - lastReport) < 700_000_000L) return
            lastReport = now
            val pct = if (total > 0) (done * 100 / total).toInt() else 100
            val speed = "${fmt(rate.toLong())}/s"
            val detail = "$pct% · ${fmt(done)} of ${fmt(total)} · $speed" + (if (files.size > 1) " · $filesDone of ${files.size} files" else "")
            c.send(Protocol.fileStatus(offerId, "progress", done, total, filesDone, files.size, speed))
            c.main.post {
                ActivityLog.updateByKey(offerId) { it.copy(detail = detail) }
                c.updateNotification("Receiving $label · $pct% · $speed")
            }
        }
    }

    /** offer:index → pending MediaStore item, so a retry appends to the same file. */
    private val partials = ConcurrentHashMap<String, Uri>()

    // ------------------------------------------------------------------ helpers

    private fun queryName(context: Context, uri: Uri): String? = runCatching {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { if (it.moveToFirst()) it.getString(0) else null }
    }.getOrNull()

    private fun querySize(context: Context, uri: Uri): Long {
        runCatching {
            context.contentResolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)?.use { c ->
                if (c.moveToFirst() && !c.isNull(0)) return c.getLong(0)
            }
        }
        return runCatching { context.contentResolver.openAssetFileDescriptor(uri, "r")!!.use { it.length } }.getOrDefault(-1L)
    }

    private fun sanitize(name: String) = name.replace(Regex("""[\\/:*?"<>|\u0000-\u001f]"""), "_").trim().trimEnd('.').ifEmpty { "file" }

    /** Peer-supplied path → safe relative path (no roots, no "." / ".."). */
    private fun sanitizePath(raw: String): String? {
        val parts = raw.replace('\\', '/').split('/').map { it.trim() }
            .filter { it.isNotEmpty() && it != "." && it != ".." }.map { sanitize(it) }
        return parts.takeIf { it.isNotEmpty() && it.size <= 32 }?.joinToString("/")
    }

    fun fmt(b: Long): String = when {
        b < 1024 -> "$b B"
        b < 1024 * 1024 -> "%.0f KB".format(b / 1024.0)
        b < 1024L * 1024 * 1024 -> "%.1f MB".format(b / (1024.0 * 1024))
        else -> "%.2f GB".format(b / (1024.0 * 1024 * 1024))
    }

    /** Collects and sends on a background thread, then toasts the result. Used by the pickers and the share target. */
    fun sendAsync(context: Context, uris: List<Uri>) {
        val app = context.applicationContext
        io.execute {
            val msg = runCatching { send(app, collect(app, uris)) }.getOrElse { "Couldn't send: ${it.message}" }
            android.os.Handler(android.os.Looper.getMainLooper()).post { Toast.makeText(app, msg, Toast.LENGTH_SHORT).show() }
        }
    }

    /** Returns a function that opens the file picker and sends the chosen files. Used by Home and the feature card. */
    @Composable
    fun rememberPickFiles(ctx: Context): () -> Unit {
        val launcher = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
            if (uris.isEmpty()) return@rememberLauncherForActivityResult
            uris.forEach { u -> runCatching { ctx.contentResolver.takePersistableUriPermission(u, Intent.FLAG_GRANT_READ_URI_PERMISSION) } }
            sendAsync(ctx, uris)
        }
        return { launcher.launch(arrayOf("*/*")) }
    }

    @Composable
    override fun Settings(ctx: Context) {
        val pickFiles = rememberPickFiles(ctx)
        val pickFolder = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { tree ->
            if (tree == null) return@rememberLauncherForActivityResult
            runCatching { ctx.contentResolver.takePersistableUriPermission(tree, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
            sendAsync(ctx, listOf(tree))
        }
        Column {
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Button(onClick = pickFiles) {
                    Icon(Icons.Rounded.FileUpload, null); Spacer(Modifier.height(0.dp)); Text("  Send files")
                }
                FilledTonalButton(onClick = { pickFolder.launch(null) }) {
                    Icon(Icons.Rounded.CreateNewFolder, null); Text("  Send folder")
                }
            }
            Spacer(Modifier.height(8.dp))
            Text("From any app: Share → QuickDrop to PC. Files from the PC are saved to Download/QuickDrop.",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
