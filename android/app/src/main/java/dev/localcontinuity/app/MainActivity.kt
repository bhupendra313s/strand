package dev.localcontinuity.app

import android.Manifest
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import dev.localcontinuity.app.features.ClipboardFeature
import dev.localcontinuity.app.ui.AppActions
import dev.localcontinuity.app.ui.ContinuityApp
import dev.localcontinuity.app.ui.ContinuityTheme
import dev.localcontinuity.app.ui.Perms
import dev.localcontinuity.app.ui.UiInfo

class MainActivity : ComponentActivity() {
    private lateinit var store: PairingStore
    /** Bumped whenever something outside Compose state changes (pairing, permissions, service). */
    private var refresh by mutableIntStateOf(0)
    private var pendingPairing by mutableStateOf<PairingInfo?>(null)

    private val scan = registerForActivityResult(ScanContract()) { r -> r.contents?.let(::onPairingText) }
    private val askPerms = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { refresh++ }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        store = PairingStore(this)
        if (savedInstanceState == null) {
            requestRuntimePermissions()
            if (store.isPaired && !SyncService.isRunning) SyncService.start(this)
            else if (!store.isPaired) SyncState.update { SyncStatus(Phase.NOT_PAIRED, "Not paired") }
        }

        val actions = AppActions(
            scan = ::startScan,
            pastePairing = ::pastePairing,
            startSync = { SyncService.start(this); bump() },
            stopSync = { SyncService.stop(this); bump() },
            sendClipboard = ::sendClipboard,
            forget = ::forget,
            requestPermissions = ::requestRuntimePermissions,
            openBatterySettings = {
                // The direct "Allow" dialog; if this phone doesn't offer it, the battery-optimisation list instead.
                runCatching {
                    @Suppress("BatteryLife")
                    startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
                }.onFailure { startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)) }
            },
            openAppSettings = {
                if (!hasPhotos() && Build.VERSION.SDK_INT >= 33) requestRuntimePermissions()
                else startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
            },
            confirmPairing = { info ->
                pendingPairing = null
                SyncService.stop(this)
                store.savePairing(info)
                SyncService.start(this)
                bump()
            },
            dismissPairing = { pendingPairing = null },
            openAccessibilitySettings = { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) },
            setAutoSend = { on ->
                CopyDetectorService.setOn(this, on)
                // Turning it on for the first time also needs the accessibility service.
                if (on && !CopyDetectorService.isEnabled(this)) startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                refresh++
            },
        )

        setContent {
            ContinuityTheme {
                @Suppress("UNUSED_EXPRESSION") refresh // recompose on bump
                ContinuityApp(uiInfo(), actions)
            }
        }
    }

    override fun onResume() { super.onResume(); refresh++ }

    private fun bump() = window.decorView.postDelayed({ refresh++ }, 300)

    private fun uiInfo() = UiInfo(
        paired = store.isPaired,
        running = SyncService.isRunning,
        hubName = store.hubName,
        hubAddress = store.hubHost?.let { "$it:${store.hubPort}" },
        fingerprint = store.hubFingerprint,
        deviceName = store.deviceName,
        version = BuildConfig.VERSION_NAME,
        perms = Perms(
            notifications = Build.VERSION.SDK_INT < 33 || granted(Manifest.permission.POST_NOTIFICATIONS),
            photos = hasPhotos(),
            battery = getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName),
        ),
        pendingPairing = pendingPairing,
        autoSendOn = CopyDetectorService.isOn(this),
        autoSendService = CopyDetectorService.isEnabled(this) && CopyDetectorService.running,
    )

    // ------------------------------------------------------------ pairing

    private fun startScan() = scan.launch(
        ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE)
            .setPrompt("Scan the QR code on your PC (Strand → Devices)")
            .setBeepEnabled(false).setOrientationLocked(true)
    )

    private fun pastePairing() {
        val text = getSystemService(ClipboardManager::class.java).primaryClip?.getItemAt(0)?.coerceToText(this)?.toString()
        if (text.isNullOrBlank()) toast("Copy the pairing code on the PC first") else onPairingText(text)
    }

    private fun onPairingText(raw: String) {
        pendingPairing = PairingInfo.parse(raw) ?: run { toast("That isn't a Strand pairing code"); null }
    }

    private fun forget() {
        android.app.AlertDialog.Builder(this).setTitle("Forget this PC?")
            .setMessage("You'll need to scan a new QR code to pair again.")
            .setPositiveButton("Forget") { _, _ ->
                SyncService.stop(this); store.clear()
                SyncState.update { SyncStatus(Phase.NOT_PAIRED, "Not paired") }
                bump()
            }
            .setNegativeButton("Cancel", null).show()
    }

    private fun sendClipboard() {
        val text = getSystemService(ClipboardManager::class.java).primaryClip?.getItemAt(0)?.coerceToText(this)?.toString()
        toast(if (text.isNullOrEmpty()) "Clipboard has no text" else ClipboardFeature.send(text))
    }

    // ------------------------------------------------------------ permissions

    private fun requestRuntimePermissions() {
        val wanted = buildList {
            if (Build.VERSION.SDK_INT >= 33) { add(Manifest.permission.POST_NOTIFICATIONS); add(Manifest.permission.READ_MEDIA_IMAGES) }
            else add(Manifest.permission.READ_EXTERNAL_STORAGE)
        }.filterNot(::granted)
        if (wanted.isNotEmpty()) askPerms.launch(wanted.toTypedArray())
    }

    private fun hasPhotos() = granted(if (Build.VERSION.SDK_INT >= 33) Manifest.permission.READ_MEDIA_IMAGES else Manifest.permission.READ_EXTERNAL_STORAGE)
    private fun granted(p: String) = ContextCompat.checkSelfPermission(this, p) == PackageManager.PERMISSION_GRANTED
    private fun toast(msg: String) = Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()
}
