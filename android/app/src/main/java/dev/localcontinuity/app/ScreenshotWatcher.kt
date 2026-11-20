package dev.localcontinuity.app

import android.content.ContentResolver
import android.content.ContentUris
import android.database.ContentObserver
import android.net.Uri
import android.os.Handler
import android.provider.MediaStore

/**
 * Detects new screenshots via MediaStore (needs READ_MEDIA_IMAGES with "Allow all").
 * Only metadata is read here. Bytes are read later by FetchServer, if and when the PC pastes.
 */
class ScreenshotWatcher(
    private val resolver: ContentResolver,
    private val handler: Handler,
    private val onScreenshot: (uri: Uri, mime: String, size: Long, width: Int, height: Int) -> Unit,
) : ContentObserver(handler) {

    private val reported = LinkedHashSet<Long>()
    private val startedAtSec = System.currentTimeMillis() / 1000
    private val check = Runnable { scan(retry = true) }

    fun start() = resolver.registerContentObserver(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, true, this)
    fun stop() { resolver.unregisterContentObserver(this); handler.removeCallbacks(check) }

    override fun onChange(selfChange: Boolean, uri: Uri?) {
        // MediaStore fires several times per screenshot (insert, then un-pend). Debounce.
        handler.removeCallbacks(check)
        handler.postDelayed(check, 400)
    }

    private fun scan(retry: Boolean) {
        val since = maxOf(startedAtSec, System.currentTimeMillis() / 1000 - 20)
        val projection = arrayOf(
            MediaStore.Images.Media._ID, MediaStore.Images.Media.DISPLAY_NAME, MediaStore.Images.Media.RELATIVE_PATH,
            MediaStore.Images.Media.MIME_TYPE, MediaStore.Images.Media.SIZE,
            MediaStore.Images.Media.WIDTH, MediaStore.Images.Media.HEIGHT,
        )
        var sawEmpty = false
        runCatching {
            resolver.query(
                MediaStore.Images.Media.EXTERNAL_CONTENT_URI, projection,
                "${MediaStore.Images.Media.DATE_ADDED} >= ?", arrayOf(since.toString()),
                "${MediaStore.Images.Media.DATE_ADDED} DESC",
            )?.use { c ->
                var n = 0
                while (c.moveToNext() && n++ < 5) {
                    val id = c.getLong(0)
                    if (id in reported) continue
                    val name = c.getString(1) ?: ""
                    val path = c.getString(2) ?: ""
                    if (!isScreenshot(name, path)) continue
                    val size = c.getLong(4)
                    if (size <= 0) { sawEmpty = true; continue } // still being written
                    reported += id
                    if (reported.size > 64) reported.remove(reported.first())
                    onScreenshot(
                        ContentUris.withAppendedId(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, id),
                        c.getString(3) ?: "image/png", size, c.getInt(5), c.getInt(6),
                    )
                }
            }
        }
        if (sawEmpty && retry) handler.postDelayed({ scan(retry = false) }, 800)
    }

    private fun isScreenshot(name: String, relativePath: String): Boolean {
        val p = relativePath.lowercase(); val n = name.lowercase()
        return "screenshot" in p || n.startsWith("screenshot")
    }
}
