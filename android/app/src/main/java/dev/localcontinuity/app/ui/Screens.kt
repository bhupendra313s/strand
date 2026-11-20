package dev.localcontinuity.app.ui

import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.ArrowDownward
import androidx.compose.material.icons.rounded.ArrowUpward
import androidx.compose.material.icons.rounded.BatteryChargingFull
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.CheckCircle
import androidx.compose.material.icons.rounded.Computer
import androidx.compose.material.icons.rounded.ContentPaste
import androidx.compose.material.icons.rounded.Delete
import androidx.compose.material.icons.rounded.ErrorOutline
import androidx.compose.material.icons.rounded.History
import androidx.compose.material.icons.rounded.Info
import androidx.compose.material.icons.rounded.Lock
import androidx.compose.material.icons.rounded.Notifications
import androidx.compose.material.icons.rounded.Photo
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material.icons.rounded.QrCodeScanner
import androidx.compose.material.icons.rounded.Share
import androidx.compose.material.icons.rounded.Sync
import androidx.compose.material.icons.rounded.SyncDisabled
import androidx.compose.material.icons.rounded.TextFields
import androidx.compose.material.icons.rounded.Tune
import androidx.compose.material3.AssistChip
import androidx.compose.material3.AssistChipDefaults
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.TextButton
import androidx.compose.material3.IconButton
import androidx.compose.material.icons.rounded.ExpandLess
import androidx.compose.material.icons.rounded.ExpandMore
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.draw.alpha
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import dev.localcontinuity.app.ActivityItem
import dev.localcontinuity.app.ActivityLog
import dev.localcontinuity.app.Direction
import dev.localcontinuity.app.Phase
import dev.localcontinuity.app.SyncState
import dev.localcontinuity.app.features.FeatureRegistry
import dev.localcontinuity.app.features.QuickDropFeature
import dev.localcontinuity.app.features.ScreenShareFeature
import androidx.compose.material.icons.rounded.FileUpload
import androidx.compose.material.icons.rounded.ScreenShare
import androidx.compose.material.icons.rounded.StopScreenShare

// ===================================================================== home

@Composable
fun HomeScreen(info: UiInfo, actions: AppActions, padding: PaddingValues, goTo: (Tab) -> Unit) {
    val status by SyncState.status.collectAsState()
    val activity by ActivityLog.items.collectAsState()

    val pc = status.hubName ?: info.hubName ?: "PC"
    Page("Strand", null, padding) {
        item { Hero(status.phase, status.detail, pc, info.deviceName, info.running, actions) }

        if (!info.perms.allGood) item {
            Spacer(Modifier.height(12.dp))
            // Quiet on purpose: same surface as the tiles, one tertiary-tinted icon and a plain "Fix" action.
            Surface(
                onClick = { goTo(Tab.Settings) }, shape = RoundedCornerShape(24.dp),
                color = MaterialTheme.colorScheme.surfaceContainerLow, modifier = Modifier.fillMaxWidth(),
            ) {
                Row(Modifier.padding(start = 16.dp, end = 8.dp, top = 6.dp, bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Rounded.Tune, null, tint = MaterialTheme.colorScheme.tertiary, modifier = Modifier.size(20.dp))
                    Spacer(Modifier.width(12.dp))
                    Text("Sync may stop in the background", style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurface, modifier = Modifier.weight(1f))
                    TextButton(onClick = { goTo(Tab.Settings) }) { Text("Fix") }
                }
            }
        }

        // Four actions as a flat 2×2 grid.
        item {
            val ctx = LocalContext.current
            val sharing by ScreenShareFeature.sharing.collectAsState()
            val startSharing = ScreenShareFeature.rememberStartSharing(ctx)
            val pickFiles = QuickDropFeature.rememberPickFiles(ctx)
            Spacer(Modifier.height(12.dp))
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    QuickAction(Icons.Rounded.ContentPaste, "Send clipboard",
                        status.connected && dev.localcontinuity.app.features.ClipboardFeature.sendEnabled(ctx), actions.sendClipboard, Modifier.weight(1f))
                    if (sharing) QuickAction(Icons.Rounded.StopScreenShare, "Stop sharing", true,
                        { dev.localcontinuity.app.ScreenCaptureService.stop(ctx) }, Modifier.weight(1f), highlighted = true)
                    else QuickAction(Icons.Rounded.ScreenShare, "Share screen",
                        status.connected && ScreenShareFeature.id in status.hubFeatures, startSharing, Modifier.weight(1f))
                }
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    QuickAction(Icons.Rounded.FileUpload, "Send files",
                        status.connected && QuickDropFeature.id in status.hubFeatures, pickFiles, Modifier.weight(1f))
                    QuickAction(Icons.Rounded.Share, "Share sheet", true, { goTo(Tab.Settings) }, Modifier.weight(1f))
                }
            }
        }

        item { SectionTitle("Recent") }
        if (activity.isEmpty()) item {
            Surface(shape = RoundedCornerShape(24.dp), color = MaterialTheme.colorScheme.surfaceContainerLow, modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(20.dp)) {
                    Text("Copy something on $pc", style = MaterialTheme.typography.titleSmall)
                    Text("It lands on this phone's clipboard, and shows up here. Screenshots you take are ready to paste on the PC.",
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
        // One rounded group with hairline dividers, not a card per row.
        itemsIndexed(activity, key = { _, it -> it.id }) { i, it -> ActivityRow(it, first = i == 0, last = i == activity.lastIndex) }
    }
}

@Composable
private fun Hero(phase: Phase, detail: String, pcName: String, phoneName: String, running: Boolean, actions: AppActions) {
    val title = when (phase) {
        Phase.CONNECTED -> "Linked to $pcName"
        Phase.CONNECTING -> "Looking for $pcName"
        Phase.ERROR -> "Needs attention"
        Phase.STOPPED, Phase.NOT_PAIRED -> "Sync is off"
    }
    val subtitle = when (phase) {
        Phase.CONNECTED -> "Over your Wi-Fi. Nothing leaves your network."
        else -> detail
    }
    // The dot shows while sharing the screen, or for a moment after anything moves.
    val sharing by ScreenShareFeature.sharing.collectAsState()
    val pulse by ActivityLog.pulse.collectAsState()
    var recent by remember { mutableStateOf(false) }
    LaunchedEffect(pulse) {
        val left = 2_500 - (System.currentTimeMillis() - pulse.at)
        if (left > 0) { recent = true; kotlinx.coroutines.delay(left); recent = false }
    }

    Surface(shape = RoundedCornerShape(32.dp), color = MaterialTheme.colorScheme.surfaceContainer, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(20.dp).animateContentSize()) {
            LinkGraphic(phase, busy = sharing || recent, incoming = !sharing && pulse.direction == Direction.IN,
                left = phoneName.removePrefix("Google ").ifBlank { "This phone" }, right = pcName)
            Spacer(Modifier.height(18.dp))
            Text(title, style = MaterialTheme.typography.titleLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(subtitle, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.height(16.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                if (running) FilledTonalButton(onClick = actions.stopSync) {
                    Icon(Icons.Rounded.PowerSettingsNew, null, Modifier.size(18.dp)); Spacer(Modifier.width(8.dp)); Text("Turn off")
                } else Button(onClick = actions.startSync) {
                    Icon(Icons.Rounded.Sync, null, Modifier.size(18.dp)); Spacer(Modifier.width(8.dp)); Text("Turn on sync")
                }
                if (phase == Phase.ERROR) OutlinedButton(onClick = actions.scan) {
                    Icon(Icons.Rounded.QrCodeScanner, null, Modifier.size(18.dp)); Spacer(Modifier.width(8.dp)); Text("Scan new code")
                }
            }
        }
    }
}

@Composable
private fun QuickAction(icon: androidx.compose.ui.graphics.vector.ImageVector, title: String, enabled: Boolean,
                        onClick: () -> Unit, modifier: Modifier, highlighted: Boolean = false) {
    val scheme = MaterialTheme.colorScheme
    Surface(
        onClick = onClick, enabled = enabled, shape = RoundedCornerShape(24.dp), modifier = modifier.height(64.dp),
        color = if (highlighted) scheme.errorContainer else scheme.surfaceContainerLow,
        contentColor = if (highlighted) scheme.onErrorContainer else scheme.onSurface,
    ) {
        Row(Modifier.padding(horizontal = 16.dp).alpha(if (enabled) 1f else 0.45f), verticalAlignment = Alignment.CenterVertically) {
            Icon(icon, null, tint = if (highlighted) scheme.onErrorContainer else scheme.primary, modifier = Modifier.size(22.dp))
            Spacer(Modifier.width(12.dp))
            Text(title, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

private fun featureIcon(featureId: String) = when (featureId) {
    "screenshot" -> Icons.Rounded.Photo
    "file_drop" -> Icons.Rounded.FileUpload
    "screen_share" -> Icons.Rounded.ScreenShare
    else -> Icons.Rounded.TextFields
}

@Composable
private fun ActivityRow(item: ActivityItem, first: Boolean, last: Boolean) {
    val r = 24.dp
    val shape = RoundedCornerShape(if (first) r else 4.dp, if (first) r else 4.dp, if (last) r else 4.dp, if (last) r else 4.dp)
    Surface(shape = shape, color = MaterialTheme.colorScheme.surfaceContainerLow, modifier = Modifier.fillMaxWidth().padding(bottom = 2.dp)) {
        Row(Modifier.padding(horizontal = 16.dp, vertical = 14.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(featureIcon(item.featureId), null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(22.dp))
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(if (item.direction == Direction.IN) Icons.Rounded.ArrowDownward else Icons.Rounded.ArrowUpward,
                        if (item.direction == Direction.IN) "Received" else "Sent", Modifier.size(14.dp), tint = MaterialTheme.colorScheme.primary)
                    Spacer(Modifier.width(4.dp))
                    Text(item.title, style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(relativeTime(item.time), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Text(item.detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
            item.image?.let {
                Spacer(Modifier.width(12.dp))
                Thumbnail(it, Modifier.size(width = 40.dp, height = 56.dp))
            }
        }
    }
}

// ===================================================================== features

@Composable
fun FeaturesScreen(padding: PaddingValues) {
    val ctx = LocalContext.current
    val enabled by FeatureRegistry.enabled.collectAsState()
    val status by SyncState.status.collectAsState()
    if (enabled.isEmpty()) FeatureRegistry.load(ctx)

    Page("Features", "Each feature is a self-contained module. Switch them on or off; your PC has matching switches.", padding) {
        items(FeatureRegistry.all, key = { it.id }) { f ->
            val on = enabled[f.id] ?: f.enabledByDefault
            Card(
                shape = RoundedCornerShape(24.dp), modifier = Modifier.fillMaxWidth().padding(bottom = 12.dp).animateContentSize(),
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow),
            ) {
                // Only the main row shows; extra settings fold away behind the chevron.
                var expanded by rememberSaveable(f.id) { mutableStateOf(false) }
                Column(Modifier.padding(18.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        AccentIcon(f.icon)
                        Spacer(Modifier.width(14.dp))
                        Column(Modifier.weight(1f)) {
                            Text(f.title, style = MaterialTheme.typography.titleMedium)
                            Text(f.description, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            // Status only when something needs attention.
                            if (on && status.connected && f.id !in status.hubFeatures)
                                Text("Turned off on ${status.hubName ?: "the PC"}", style = MaterialTheme.typography.labelMedium,
                                    color = MaterialTheme.colorScheme.tertiary, modifier = Modifier.padding(top = 4.dp))
                        }
                        Spacer(Modifier.width(8.dp))
                        Switch(checked = on, onCheckedChange = { FeatureRegistry.setEnabled(ctx, f, it) })
                        if (f.hasSettings && on) IconButton(onClick = { expanded = !expanded }) {
                            Icon(if (expanded) Icons.Rounded.ExpandLess else Icons.Rounded.ExpandMore,
                                if (expanded) "Hide settings" else "More settings")
                        }
                    }
                    if (f.hasSettings && on && expanded) {
                        HorizontalDivider(Modifier.padding(vertical = 12.dp))
                        f.Settings(ctx)
                    }
                }
            }
        }
        item {
            OutlinedCard(shape = RoundedCornerShape(24.dp), modifier = Modifier.fillMaxWidth(),
                border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant)) {
                Row(Modifier.padding(18.dp), verticalAlignment = Alignment.CenterVertically) {
                    TonalIcon(Icons.Rounded.Add, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    Spacer(Modifier.width(14.dp))
                    Column {
                        Text("More on the way", style = MaterialTheme.typography.titleSmall)
                        Text("Notification mirroring and media controls can plug into the same model.",
                            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }
    }
}

// ===================================================================== PC

@Composable
fun PcScreen(info: UiInfo, actions: AppActions, padding: PaddingValues) {
    val status by SyncState.status.collectAsState()
    Page("Your PC", "Paired over your local network", padding) {
        item {
            Card(shape = RoundedCornerShape(28.dp), modifier = Modifier.fillMaxWidth(),
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow)) {
                Column(Modifier.fillMaxWidth().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    AccentIcon(Icons.Rounded.Computer, size = 72.dp, corner = 24.dp)
                    Spacer(Modifier.height(16.dp))
                    Text(status.hubName ?: info.hubName ?: "PC", style = MaterialTheme.typography.headlineSmall)
                    Spacer(Modifier.height(8.dp))
                    StatusPill(
                        when (status.phase) { Phase.CONNECTED -> "Connected"; Phase.CONNECTING -> "Connecting"; Phase.ERROR -> "Error"; else -> "Offline" },
                        phaseColor(status.phase),
                    )
                    info.hubAddress?.let {
                        Spacer(Modifier.height(8.dp))
                        Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }
        item { SectionTitle("Security") }
        item {
            Card(shape = RoundedCornerShape(20.dp), modifier = Modifier.fillMaxWidth(),
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow)) {
                Column(Modifier.padding(18.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Rounded.Lock, null, tint = MaterialTheme.colorScheme.primary)
                        Spacer(Modifier.width(10.dp))
                        Text("Pinned PC fingerprint", style = MaterialTheme.typography.titleSmall)
                    }
                    Text("This phone only talks to the PC with exactly this certificate.", style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp, bottom = 12.dp))
                    Surface(shape = RoundedCornerShape(12.dp), color = MaterialTheme.colorScheme.surfaceContainerHighest) {
                        Text(groupFingerprint(info.fingerprint ?: ""), style = MonoStyle, modifier = Modifier.fillMaxWidth().padding(12.dp))
                    }
                }
            }
        }
        item { SectionTitle("Pairing") }
        item {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                FilledTonalButton(onClick = actions.scan, modifier = Modifier.fillMaxWidth().height(52.dp)) {
                    Icon(Icons.Rounded.QrCodeScanner, null); Spacer(Modifier.width(10.dp)); Text("Scan a new QR code")
                }
                OutlinedButton(onClick = actions.pastePairing, modifier = Modifier.fillMaxWidth().height(52.dp)) {
                    Icon(Icons.Rounded.ContentPaste, null); Spacer(Modifier.width(10.dp)); Text("Paste pairing code")
                }
                OutlinedButton(
                    onClick = actions.forget, modifier = Modifier.fillMaxWidth().height(52.dp),
                    colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error),
                ) { Icon(Icons.Rounded.Delete, null); Spacer(Modifier.width(10.dp)); Text("Forget this PC") }
            }
        }
    }
}

// ===================================================================== settings

@Composable
fun SettingsScreen(info: UiInfo, actions: AppActions, padding: PaddingValues) {
    Page("Settings", null, padding) {
        item { SectionTitle("Permissions") }
        item {
            Card(shape = RoundedCornerShape(24.dp), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow)) {
                Column(Modifier.padding(vertical = 6.dp)) {
                    PermRow(Icons.Rounded.Notifications, "Notifications", "Shows sync status and the Send button", info.perms.notifications, actions.requestPermissions)
                    PermRow(Icons.Rounded.Photo, "Photos: allow all", "Needed to notice new screenshots", info.perms.photos, actions.openAppSettings)
                    PermRow(Icons.Rounded.BatteryChargingFull, "Battery: unrestricted", "Keeps the connection alive with the screen off", info.perms.battery, actions.openBatterySettings)
                }
            }
        }
        item { SectionTitle("Sending text to your PC") }
        item {
            Card(shape = RoundedCornerShape(24.dp), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow)) {
                Column(Modifier.padding(vertical = 6.dp)) {
                    val needsService = info.autoSendOn && !info.autoSendService
                    ListRow(
                        Icons.Rounded.ContentPaste, "Auto-send copied text",
                        when {
                            !info.autoSendOn -> "Off: copy, then Share → Send to PC"
                            needsService -> "Not running. Tap, then switch \"Strand: auto-send copied text\" off and on in Accessibility"
                            else -> "On: anything you copy goes to your PC"
                        },
                        onClick = if (needsService) actions.openAccessibilitySettings else ({ actions.setAutoSend(!info.autoSendOn) }),
                    ) {
                        Switch(checked = info.autoSendOn, onCheckedChange = actions.setAutoSend)
                    }
                    ListRow(Icons.Rounded.TextFields, "Select text → Send to PC", "In any app's text-selection menu")
                    ListRow(Icons.Rounded.Share, "Share → Send to PC", "From the share sheet")
                    ListRow(Icons.Rounded.Tune, "Quick Settings tile", "Add \"Send to PC\" to your quick settings")
                    ListRow(Icons.Rounded.Notifications, "Notification button", "Tap \"Send clipboard\"")
                    Text(
                        "Android 10 and later stop background apps from reading the clipboard. Auto-send works around this: when Android shows its clipboard preview after a copy, Strand briefly opens an invisible screen to read and send the text. Copies that password managers mark as sensitive are never sent.",
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(horizontal = 20.dp, vertical = 10.dp),
                    )
                }
            }
        }
        item { SectionTitle("About") }
        item {
            Card(shape = RoundedCornerShape(24.dp), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow)) {
                Row(Modifier.padding(18.dp), verticalAlignment = Alignment.Top) {
                    StrandMark()
                    Spacer(Modifier.width(14.dp))
                    Column {
                        Text("Strand ${info.version}", style = MaterialTheme.typography.titleSmall)
                        Text("This phone: ${info.deviceName}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        Spacer(Modifier.height(8.dp))
                        Text("No accounts, no cloud, no telemetry. Data moves only between your devices over your Wi-Fi, encrypted with certificates exchanged by QR code.",
                            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }
    }
}

@Composable
private fun PermRow(icon: androidx.compose.ui.graphics.vector.ImageVector, title: String, subtitle: String, granted: Boolean, fix: () -> Unit) {
    ListRow(icon, title, subtitle, onClick = if (granted) null else fix) {
        if (granted) Icon(Icons.Rounded.CheckCircle, "Granted", tint = MaterialTheme.colorScheme.primary)
        else Button(onClick = fix, contentPadding = PaddingValues(horizontal = 14.dp)) { Text("Allow") }
    }
}
