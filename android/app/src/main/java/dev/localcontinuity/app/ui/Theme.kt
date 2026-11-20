package dev.localcontinuity.app.ui

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

// Colours come from the wallpaper (Material You). There are deliberately no brand hues: every accent is a
// colorScheme role, so the app re-tints with the user's wallpaper, and the PC hub mirrors it (see PhonePalette).

private val LightFallback = lightColorScheme(
    primary = Color(0xFF2F5BD3), secondary = Color(0xFF5B5D9E), tertiary = Color(0xFF7C3AED),
    background = Color(0xFFF8F9FF), surface = Color(0xFFF8F9FF),
)
private val DarkFallback = darkColorScheme(
    primary = Color(0xFFB2C5FF), secondary = Color(0xFFC3C3FF), tertiary = Color(0xFFD7BAFF),
    background = Color(0xFF111318), surface = Color(0xFF111318),
)

/** Minimal, cool: light headline weights with slightly tight tracking; hierarchy comes from size, not bold. */
private val AppTypography = Typography().let { t ->
    t.copy(
        headlineLarge = t.headlineLarge.copy(fontWeight = FontWeight.Normal, letterSpacing = (-0.5).sp),
        headlineSmall = t.headlineSmall.copy(fontWeight = FontWeight.Normal, letterSpacing = (-0.25).sp),
        titleLarge = t.titleLarge.copy(fontWeight = FontWeight.Normal),
        titleMedium = t.titleMedium.copy(fontWeight = FontWeight.Medium),
        titleSmall = t.titleSmall.copy(fontWeight = FontWeight.Medium),
        labelLarge = t.labelLarge.copy(fontWeight = FontWeight.Medium),
    )
}

val MonoStyle = TextStyle(fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace, fontSize = 13.sp, lineHeight = 20.sp)

@Composable
fun ContinuityTheme(content: @Composable () -> Unit) {
    val dark = isSystemInDarkTheme()
    val ctx = LocalContext.current
    val scheme = when {
        Build.VERSION.SDK_INT >= 31 -> if (dark) dynamicDarkColorScheme(ctx) else dynamicLightColorScheme(ctx)
        dark -> DarkFallback
        else -> LightFallback
    }
    MaterialTheme(colorScheme = scheme, typography = AppTypography, content = content)
}

/**
 * The phone's Material You palette as JSON for the PC hub (sent in `hello`), so both devices share the
 * wallpaper colours. Plain function: dynamic*ColorScheme(context) needs no composition.
 */
object PhonePalette {
    fun json(ctx: android.content.Context): org.json.JSONObject? {
        if (Build.VERSION.SDK_INT < 31) return null
        fun hex(c: Color) = "#%06X".format(c.toArgb() and 0xFFFFFF)
        fun roles(s: androidx.compose.material3.ColorScheme) = org.json.JSONObject()
            .put("primary", hex(s.primary)).put("on_primary", hex(s.onPrimary))
            .put("primary_container", hex(s.primaryContainer)).put("on_primary_container", hex(s.onPrimaryContainer))
            .put("tertiary", hex(s.tertiary)).put("surface_container", hex(s.surfaceContainer))
        return org.json.JSONObject()
            .put("light", roles(dynamicLightColorScheme(ctx)))
            .put("dark", roles(dynamicDarkColorScheme(ctx)))
    }
}
