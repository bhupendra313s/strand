package dev.localcontinuity.app.ui

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Computer
import androidx.compose.material.icons.rounded.ContentPaste
import androidx.compose.material.icons.rounded.Extension
import androidx.compose.material.icons.rounded.Home
import androidx.compose.material.icons.rounded.QrCodeScanner
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.Shield
import androidx.compose.material.icons.rounded.Sync
import androidx.compose.material.icons.rounded.Wifi
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import dev.localcontinuity.app.PairingInfo

/** Callbacks into the Activity (permissions, scanner, service). Keeps screens free of Android plumbing. */
class AppActions(
    val scan: () -> Unit,
    val pastePairing: () -> Unit,
    val startSync: () -> Unit,
    val stopSync: () -> Unit,
    val sendClipboard: () -> Unit,
    val forget: () -> Unit,
    val requestPermissions: () -> Unit,
    val openBatterySettings: () -> Unit,
    val openAppSettings: () -> Unit,
    val confirmPairing: (PairingInfo) -> Unit,
    val dismissPairing: () -> Unit,
    val openAccessibilitySettings: () -> Unit,
    val setAutoSend: (Boolean) -> Unit,
)

data class Perms(val notifications: Boolean, val photos: Boolean, val battery: Boolean) {
    val allGood get() = notifications && photos && battery
}

data class UiInfo(
    val paired: Boolean,
    val running: Boolean,
    val hubName: String?,
    val hubAddress: String?,
    val fingerprint: String?,
    val deviceName: String,
    val version: String,
    val perms: Perms,
    val pendingPairing: PairingInfo?,
    /** In-app "Auto-send copied text" switch. */
    val autoSendOn: Boolean,
    /** The accessibility service behind it is enabled in Android settings. */
    val autoSendService: Boolean,
)

enum class Tab(val label: String, val icon: ImageVector) {
    Home("Home", Icons.Rounded.Home),
    Features("Features", Icons.Rounded.Extension),
    Pc("Devices", Icons.Rounded.Computer),
    Settings("Settings", Icons.Rounded.Settings),
}

@Composable
fun ContinuityApp(info: UiInfo, actions: AppActions) {
    info.pendingPairing?.let { PairConfirmDialog(it, actions) }

    if (!info.paired) { Onboarding(actions); return }

    var tab by rememberSaveable { mutableStateOf(Tab.Home) }
    Scaffold(
        bottomBar = {
            NavigationBar {
                Tab.entries.forEach { t ->
                    NavigationBarItem(
                        selected = tab == t, onClick = { tab = t },
                        icon = { Icon(t.icon, null) }, label = { Text(t.label) },
                    )
                }
            }
        },
    ) { padding ->
        AnimatedContent(tab, transitionSpec = { fadeIn() togetherWith fadeOut() }, label = "tab") { current ->
            when (current) {
                Tab.Home -> HomeScreen(info, actions, padding) { tab = it }
                Tab.Features -> FeaturesScreen(padding)
                Tab.Pc -> PcScreen(info, actions, padding)
                Tab.Settings -> SettingsScreen(info, actions, padding)
            }
        }
    }
}

/** Shared page frame: large title + scrolling content. */
@Composable
fun Page(title: String, subtitle: String?, padding: PaddingValues, content: LazyListScope.() -> Unit) {
    LazyColumn(
        Modifier.fillMaxSize().statusBarsPadding(),
        contentPadding = PaddingValues(start = 20.dp, end = 20.dp, top = 16.dp, bottom = padding.calculateBottomPadding() + 24.dp),
    ) {
        item {
            Text(title, style = MaterialTheme.typography.headlineLarge)
            if (subtitle != null) Text(subtitle, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 4.dp))
            Spacer(Modifier.height(16.dp))
        }
        content()
    }
}

// ===================================================================== onboarding

@Composable
private fun Onboarding(actions: AppActions) {
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(
            Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().padding(28.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Spacer(Modifier.weight(1f))
            StrandMark(tile = 96.dp, corner = 30.dp)
            Spacer(Modifier.height(28.dp))
            Text("Strand", style = MaterialTheme.typography.headlineLarge)
            Text(
                "Your phone and PC, one clipboard.\nNo accounts. No cloud. Just your Wi-Fi.",
                style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center, modifier = Modifier.padding(top = 8.dp),
            )
            Spacer(Modifier.height(36.dp))
            Surface(shape = RoundedCornerShape(24.dp), color = MaterialTheme.colorScheme.surfaceContainer) {
                Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(18.dp)) {
                    Step(Icons.Rounded.Computer, "Open Strand on your PC", "It shows a QR code under Devices → Pair a new phone")
                    Step(Icons.Rounded.Wifi, "Use the same Wi-Fi", "Both devices must be on the same network")
                    Step(Icons.Rounded.Shield, "Scan once, stay paired", "The phone locks to your PC's certificate")
                }
            }
            Spacer(Modifier.weight(1f))
            Button(onClick = actions.scan, modifier = Modifier.fillMaxWidth().height(56.dp), shape = RoundedCornerShape(18.dp)) {
                Icon(Icons.Rounded.QrCodeScanner, null)
                Spacer(Modifier.width(10.dp))
                Text("Scan QR code", style = MaterialTheme.typography.titleMedium)
            }
            TextButton(onClick = actions.pastePairing, modifier = Modifier.padding(top = 4.dp)) {
                Icon(Icons.Rounded.ContentPaste, null, Modifier.size(18.dp))
                Spacer(Modifier.width(8.dp))
                Text("Paste pairing code instead")
            }
        }
    }
}

@Composable
private fun Step(icon: ImageVector, title: String, subtitle: String) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        TonalIcon(icon)
        Spacer(Modifier.width(16.dp))
        Column {
            Text(title, style = MaterialTheme.typography.titleSmall)
            Text(subtitle, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun PairConfirmDialog(p: PairingInfo, actions: AppActions) {
    AlertDialog(
        onDismissRequest = actions.dismissPairing,
        icon = { Icon(Icons.Rounded.Computer, null) },
        title = { Text("Pair with ${p.name}?") },
        text = {
            Column {
                Text("Check that this fingerprint matches the one shown on your PC.", style = MaterialTheme.typography.bodyMedium)
                Spacer(Modifier.height(12.dp))
                Surface(shape = RoundedCornerShape(12.dp), color = MaterialTheme.colorScheme.surfaceContainerHighest) {
                    Box(Modifier.fillMaxWidth().padding(12.dp)) {
                        Text(groupFingerprint(p.fingerprint), style = MonoStyle)
                    }
                }
                Spacer(Modifier.height(8.dp))
                Text("${p.ip}:${p.port}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        },
        confirmButton = { Button(onClick = { actions.confirmPairing(p) }) { Text("Pair") } },
        dismissButton = { TextButton(onClick = actions.dismissPairing) { Text("Cancel") } },
    )
}

fun groupFingerprint(fp: String) = fp.uppercase().chunked(4).chunked(4).joinToString("\n") { it.joinToString("  ") }
