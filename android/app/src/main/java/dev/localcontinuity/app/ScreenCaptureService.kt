package dev.localcontinuity.app

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.SystemClock
import android.util.Log
import android.view.WindowManager
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.IntentCompat
import dev.localcontinuity.app.features.ScreenShareFeature
import java.io.ByteArrayOutputStream
import kotlin.math.max
import kotlin.math.roundToInt

/**
 * Captures the screen with MediaProjection (after the user's consent) and keeps the latest frame as a JPEG in
 * [Frames]. Frames are only encoded while the PC viewer is connected. Runs as its own foreground service
 * (type mediaProjection); Android shows its own "sharing" indicator for as long as this runs.
 */
class ScreenCaptureService : Service() {
    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var reader: ImageReader? = null
    private lateinit var worker: HandlerThread
    private lateinit var handler: Handler
    private var width = 0
    private var height = 0
    private var lastFrameAt = 0L

    private val displayListener = object : DisplayManager.DisplayListener {
        override fun onDisplayChanged(displayId: Int) { if (displayId == android.view.Display.DEFAULT_DISPLAY) handler.post { resizeIfRotated() } }
        override fun onDisplayAdded(displayId: Int) {}
        override fun onDisplayRemoved(displayId: Int) {}
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        worker = HandlerThread("screen-capture").apply { start() }
        handler = Handler(worker.looper)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) { stopSelf(); return START_NOT_STICKY }
        val code = intent?.getIntExtra(EXTRA_CODE, Activity.RESULT_CANCELED) ?: Activity.RESULT_CANCELED
        val data = intent?.let { IntentCompat.getParcelableExtra(it, EXTRA_DATA, Intent::class.java) }
        if (code != Activity.RESULT_OK || data == null || projection != null) return START_NOT_STICKY

        // Android 14+: the foreground service must be running before the projection is created.
        ServiceCompat.startForeground(this, NOTIF_ID, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        val mp = getSystemService(MediaProjectionManager::class.java).getMediaProjection(code, data) ?: run { stopSelf(); return START_NOT_STICKY }
        projection = mp
        mp.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() { Log.i(TAG, "projection stopped by the system or the user"); stopSelf() }
        }, handler)

        handler.post { startCapture(mp) }
        getSystemService(DisplayManager::class.java).registerDisplayListener(displayListener, handler)
        running = true
        ScreenShareFeature.onCaptureStarted(width = targetSize().first, height = targetSize().second)
        return START_NOT_STICKY
    }

    /** Stream size: the screen scaled so its longer side is at most [maxSide()] (keeps Wi-Fi use and latency down). */
    private fun targetSize(): Pair<Int, Int> {
        val b = getSystemService(WindowManager::class.java).maximumWindowMetrics.bounds
        val scale = minOf(1f, maxSide().toFloat() / max(b.width(), b.height()))
        // Even dimensions keep encoders and ImageReader happy.
        return ((b.width() * scale).roundToInt() and 1.inv()) to ((b.height() * scale).roundToInt() and 1.inv())
    }

    private fun maxSide() = if (ScreenShareFeature.highQuality(this)) 1920 else 1280

    private fun startCapture(mp: MediaProjection) {
        val (w, h) = targetSize()
        width = w; height = h
        val r = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, 2)
        r.setOnImageAvailableListener({ onImage(it) }, handler)
        reader = r
        display = mp.createVirtualDisplay("Strand screen share", w, h, resources.displayMetrics.densityDpi,
            DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, r.surface, null, handler)
        Log.i(TAG, "capturing at ${w}x$h")
    }

    /** Portrait ↔ landscape: resize the virtual display and swap in a reader of the new size. */
    private fun resizeIfRotated() {
        val (w, h) = targetSize()
        if (w == width && h == height) return
        val old = reader
        width = w; height = h
        val r = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, 2)
        r.setOnImageAvailableListener({ onImage(it) }, handler)
        reader = r
        display?.resize(w, h, resources.displayMetrics.densityDpi)
        display?.surface = r.surface
        old?.close()
        Log.i(TAG, "rotated: now ${w}x$h")
    }

    private val jpeg = ByteArrayOutputStream(256 * 1024)
    private var scratch: Bitmap? = null

    private fun onImage(r: ImageReader) {
        val image = runCatching { r.acquireLatestImage() }.getOrNull() ?: return
        image.use { img ->
            // Encode only while someone is watching, and at most ~[FPS] times per second.
            val now = SystemClock.elapsedRealtime()
            if (Frames.viewers.get() == 0 || now - lastFrameAt < 1000 / FPS) return
            lastFrameAt = now
            val plane = img.planes[0]
            val rowPixels = plane.rowStride / plane.pixelStride
            val bmp = scratch?.takeIf { it.width == rowPixels && it.height == img.height }
                ?: Bitmap.createBitmap(rowPixels, img.height, Bitmap.Config.ARGB_8888).also { scratch?.recycle(); scratch = it }
            bmp.copyPixelsFromBuffer(plane.buffer)
            val frame = if (rowPixels == img.width) bmp else Bitmap.createBitmap(bmp, 0, 0, img.width, img.height)
            jpeg.reset()
            frame.compress(Bitmap.CompressFormat.JPEG, if (ScreenShareFeature.highQuality(this)) 80 else 65, jpeg)
            if (frame !== bmp) frame.recycle()
            Frames.publish(jpeg.toByteArray())
        }
    }

    override fun onDestroy() {
        running = false
        runCatching { getSystemService(DisplayManager::class.java).unregisterDisplayListener(displayListener) }
        display?.release(); display = null
        reader?.close(); reader = null
        projection?.stop(); projection = null
        scratch?.recycle(); scratch = null
        worker.quitSafely()
        Frames.clear()
        ScreenShareFeature.onCaptureStopped()
        super.onDestroy()
    }

    private fun notification(): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL) == null)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Screen sharing", NotificationManager.IMPORTANCE_LOW))
        val stop = PendingIntent.getService(this, 2, Intent(this, ScreenCaptureService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE)
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        return NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_sync)
            .setContentTitle("Sharing your screen with ${SyncState.status.value.hubName ?: "your PC"}")
            .setContentText("Everything on screen is visible on the PC. Tap Stop to end.")
            .setOngoing(true).setContentIntent(open)
            .addAction(R.drawable.ic_stat_sync, "Stop sharing", stop)
            .build()
    }

    /** Latest encoded frame, shared with the streaming thread in [FetchServer]. */
    object Frames {
        val viewers = java.util.concurrent.atomic.AtomicInteger()
        private val lock = Object()
        private var latest: ByteArray? = null
        private var seq = 0L

        fun publish(bytes: ByteArray) = synchronized(lock) { latest = bytes; seq++; lock.notifyAll() }
        fun clear() = synchronized(lock) { latest = null; seq++; lock.notifyAll() }

        /** Waits up to [timeoutMs] for a frame newer than [afterSeq]. Returns (seq, bytes) or null on timeout. */
        fun next(afterSeq: Long, timeoutMs: Long): Pair<Long, ByteArray?>? = synchronized(lock) {
            val deadline = SystemClock.elapsedRealtime() + timeoutMs
            while (seq == afterSeq) {
                val left = deadline - SystemClock.elapsedRealtime()
                if (left <= 0) return null
                lock.wait(left)
            }
            seq to latest
        }
    }

    companion object {
        private const val TAG = "ScreenCapture"
        private const val CHANNEL = "screen_share"
        private const val NOTIF_ID = 2
        private const val FPS = 24
        const val EXTRA_CODE = "code"
        const val EXTRA_DATA = "data"
        const val ACTION_STOP = "dev.localcontinuity.app.STOP_SCREEN_SHARE"
        @Volatile var running = false; private set

        fun start(ctx: Context, resultCode: Int, data: Intent) =
            ctx.startForegroundService(Intent(ctx, ScreenCaptureService::class.java).putExtra(EXTRA_CODE, resultCode).putExtra(EXTRA_DATA, data))

        fun stop(ctx: Context) { if (running) ctx.startService(Intent(ctx, ScreenCaptureService::class.java).setAction(ACTION_STOP)) }
    }
}
