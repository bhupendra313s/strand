package dev.localcontinuity.app.ui

import android.text.format.DateUtils
import android.util.Size
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import dev.localcontinuity.app.Phase
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.height
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Laptop
import androidx.compose.material.icons.rounded.Smartphone
import androidx.compose.runtime.remember
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

@Composable
fun SectionTitle(text: String, modifier: Modifier = Modifier) {
    Text(
        text, style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary,
        modifier = modifier.padding(start = 4.dp, top = 24.dp, bottom = 8.dp),
    )
}

/** Feature/brand tile in the wallpaper palette's primary container. */
@Composable
fun AccentIcon(icon: ImageVector, size: Dp = 44.dp, corner: Dp = 14.dp) {
    Box(
        Modifier.size(size).clip(RoundedCornerShape(corner)).background(MaterialTheme.colorScheme.primaryContainer),
        contentAlignment = Alignment.Center,
    ) { Icon(icon, null, tint = MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.size(size * 0.5f)) }
}

/**
 * The signature element: this phone and the PC as two endpoints joined by a line.
 * Waiting: dashed outline line. Linked: solid primary line. [busy] (something is moving right now):
 * a tertiary dot travels along it, left to right for outgoing, right to left for incoming.
 * With system animations off, the dot rests in the middle instead.
 */
@Composable
fun LinkGraphic(phase: Phase, busy: Boolean, incoming: Boolean, left: String, right: String, modifier: Modifier = Modifier) {
    val scheme = MaterialTheme.colorScheme
    val linked = phase == Phase.CONNECTED
    val lineColor = when (phase) {
        Phase.CONNECTED -> scheme.primary
        Phase.CONNECTING -> scheme.tertiary
        Phase.ERROR -> scheme.error
        else -> scheme.outlineVariant
    }
    val ctx = LocalContext.current
    val animationsOn = remember {
        android.provider.Settings.Global.getFloat(ctx.contentResolver, android.provider.Settings.Global.ANIMATOR_DURATION_SCALE, 1f) > 0f
    }
    val travel = rememberInfiniteTransition(label = "packet")
    val p by travel.animateFloat(0f, 1f, infiniteRepeatable(tween(1400, easing = FastOutSlowInEasing)), label = "p")
    val dot = scheme.tertiary

    Row(modifier.fillMaxWidth(), verticalAlignment = Alignment.Top) {
        LinkEndpoint(Icons.Rounded.Smartphone, left, linked)
        Canvas(Modifier.weight(1f).height(56.dp).padding(horizontal = 6.dp)) {
            val y = size.height / 2
            val stroke = 3.dp.toPx()
            drawLine(
                lineColor, Offset(0f, y), Offset(size.width, y), strokeWidth = stroke, cap = StrokeCap.Round,
                pathEffect = if (linked) null else PathEffect.dashPathEffect(floatArrayOf(6.dp.toPx(), 7.dp.toPx())),
            )
            if (linked && busy) {
                val f = if (animationsOn) (if (incoming) 1f - p else p) else 0.5f
                drawCircle(dot, radius = 5.dp.toPx(), center = Offset(size.width * f, y))
            }
        }
        LinkEndpoint(Icons.Rounded.Laptop, right, linked)
    }
}

@Composable
private fun LinkEndpoint(icon: ImageVector, label: String, linked: Boolean) {
    val scheme = MaterialTheme.colorScheme
    Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.width(84.dp)) {
        Box(
            Modifier.size(56.dp).clip(CircleShape).background(if (linked) scheme.primaryContainer else scheme.surfaceContainerHighest),
            contentAlignment = Alignment.Center,
        ) { Icon(icon, null, tint = if (linked) scheme.onPrimaryContainer else scheme.onSurfaceVariant, modifier = Modifier.size(26.dp)) }
        Spacer(Modifier.height(8.dp))
        Text(label, style = MaterialTheme.typography.labelMedium, color = scheme.onSurface, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

/** Soft tonal tile used in lists. */
@Composable
fun TonalIcon(icon: ImageVector, size: Dp = 40.dp, tint: Color = MaterialTheme.colorScheme.primary) {
    Box(
        Modifier.size(size).clip(RoundedCornerShape(12.dp)).background(MaterialTheme.colorScheme.secondaryContainer),
        contentAlignment = Alignment.Center,
    ) { Icon(icon, null, tint = tint, modifier = Modifier.size(size * 0.5f)) }
}

/** Connection state in palette roles: linked = primary, connecting = tertiary, error = error, off = outline. */
@Composable
fun phaseColor(phase: Phase): Color = when (phase) {
    Phase.CONNECTED -> MaterialTheme.colorScheme.primary
    Phase.CONNECTING -> MaterialTheme.colorScheme.tertiary
    Phase.ERROR -> MaterialTheme.colorScheme.error
    else -> MaterialTheme.colorScheme.outline
}

/** Status orb; pulses while connecting. */
@Composable
fun StatusOrb(phase: Phase, icon: ImageVector, size: Dp = 64.dp) {
    val color = phaseColor(phase)
    val pulse = rememberInfiniteTransition(label = "pulse")
    val s by pulse.animateFloat(1f, if (phase == Phase.CONNECTING) 1.18f else 1f,
        infiniteRepeatable(tween(900), RepeatMode.Reverse), label = "scale")
    Box(Modifier.size(size), contentAlignment = Alignment.Center) {
        Box(Modifier.size(size).scale(s).clip(CircleShape).background(color.copy(alpha = 0.25f)))
        Box(Modifier.size(size * 0.7f).clip(CircleShape).background(color), contentAlignment = Alignment.Center) {
            Icon(icon, null, tint = MaterialTheme.colorScheme.surface, modifier = Modifier.size(size * 0.36f))
        }
    }
}

@Composable
fun StatusPill(text: String, color: Color) {
    Surface(shape = RoundedCornerShape(50), color = color.copy(alpha = 0.16f)) {
        Row(Modifier.padding(horizontal = 10.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(8.dp).clip(CircleShape).background(color))
            Spacer(Modifier.width(6.dp))
            Text(text, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurface)
        }
    }
}

/** A settings row with title, subtitle and a switch; the whole row is tappable. */
@Composable
fun SettingSwitch(title: String, subtitle: String?, checked: Boolean, onChange: (Boolean) -> Unit, enabled: Boolean = true) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).clickable(enabled) { onChange(!checked) }.padding(vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            if (subtitle != null) Text(subtitle, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Spacer(Modifier.width(12.dp))
        Switch(checked = checked, onCheckedChange = onChange, enabled = enabled)
    }
}

/** A tappable row: icon, title, subtitle, trailing content. */
@Composable
fun ListRow(
    icon: ImageVector, title: String, subtitle: String? = null, onClick: (() -> Unit)? = null,
    trailing: @Composable (() -> Unit)? = null,
) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp))
            .let { if (onClick != null) it.clickable(onClick = onClick) else it }
            .padding(horizontal = 16.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        TonalIcon(icon)
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (subtitle != null) Text(subtitle, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        trailing?.invoke()
    }
}

/** MediaStore thumbnail, loaded off the main thread. */
@Composable
fun Thumbnail(uri: android.net.Uri, modifier: Modifier = Modifier) {
    val ctx = LocalContext.current
    val bmp by produceState<ImageBitmap?>(null, uri) {
        value = withContext(Dispatchers.IO) {
            runCatching { ctx.contentResolver.loadThumbnail(uri, Size(320, 320), null).asImageBitmap() }.getOrNull()
        }
    }
    bmp?.let { Image(it, null, contentScale = ContentScale.Crop, modifier = modifier.clip(RoundedCornerShape(10.dp))) }
}

fun relativeTime(t: Long): String =
    if (System.currentTimeMillis() - t < 45_000) "Just now"
    else DateUtils.getRelativeTimeSpanString(t, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS).toString()

/** The Strand logo (two points joined by one line) on a primary-container tile, in the wallpaper palette. */
@Composable
fun StrandMark(tile: Dp = 44.dp, corner: Dp = 14.dp) {
    val scheme = MaterialTheme.colorScheme
    Box(Modifier.size(tile).clip(RoundedCornerShape(corner)).background(scheme.primaryContainer), contentAlignment = Alignment.Center) {
        Canvas(Modifier.size(tile * 0.62f)) {
            val y = size.height / 2
            val r = size.width * 0.13f
            val ink = scheme.onPrimaryContainer
            drawLine(ink, Offset(r, y), Offset(size.width - r, y), strokeWidth = size.width * 0.07f, cap = StrokeCap.Round)
            drawCircle(ink, r, Offset(r, y))
            drawCircle(ink, r, Offset(size.width - r, y))
        }
    }
}
