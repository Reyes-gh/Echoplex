package io.github.reyesgh.echoplex.ui

import android.app.Activity
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

/** Los colores de un tema de Echoplex (los mismos que en el PC; ver Palettes.kt). */
data class ThemePalette(
    val key: String,
    val name: String,
    val group: String,
    val isDark: Boolean,
    val bg: Long, val surface: Long, val raised: Long, val divider: Long,
    val text: Long, val text2: Long, val text3: Long,
    val accent: Long, val accentSoft: Long, val onAccent: Long,
    val fav: Long, val playCircle: Long, val onPlayCircle: Long,
)

/**
 * Los colores del tema elegido. Cada uno se lee del tema actual, así que al cambiarlo se repinta todo al momento.
 */
object Ep {
    var palette by mutableStateOf(AllThemes.first { it.key == "dark" })

    val Bg get() = Color(palette.bg)
    val Surface get() = Color(palette.surface)
    val Raised get() = Color(palette.raised)
    val Divider get() = Color(palette.divider)
    val Text get() = Color(palette.text)
    val Text2 get() = Color(palette.text2)
    val Text3 get() = Color(palette.text3)
    val Accent get() = Color(palette.accent)
    val AccentSoft get() = Color(palette.accentSoft)
    val OnAccent get() = Color(palette.onAccent)
    val Fav get() = Color(palette.fav)
    val PlayCircle get() = Color(palette.playCircle)
    val OnPlayCircle get() = Color(palette.onPlayCircle)

    fun use(key: String?) {
        palette = AllThemes.firstOrNull { it.key == key } ?: palette
    }
}

@Composable
fun EchoplexTheme(content: @Composable () -> Unit) {
    val p = Ep.palette
    val scheme = if (p.isDark) darkColorScheme(
        primary = Ep.Accent, onPrimary = Ep.OnAccent, secondary = Ep.Accent,
        background = Ep.Bg, onBackground = Ep.Text, surface = Ep.Bg, onSurface = Ep.Text,
        surfaceVariant = Ep.Surface, onSurfaceVariant = Ep.Text2, surfaceContainer = Ep.Surface, surfaceContainerHigh = Ep.Raised,
        outline = Ep.Divider, outlineVariant = Ep.Divider,
    ) else lightColorScheme(
        primary = Ep.Accent, onPrimary = Ep.OnAccent, secondary = Ep.Accent,
        background = Ep.Bg, onBackground = Ep.Text, surface = Ep.Bg, onSurface = Ep.Text,
        surfaceVariant = Ep.Surface, onSurfaceVariant = Ep.Text2, surfaceContainer = Ep.Surface, surfaceContainerHigh = Ep.Raised,
        outline = Ep.Divider, outlineVariant = Ep.Divider,
    )
    // iconos de la barra de estado y de navegación oscuros en los temas claros
    val view = LocalView.current
    if (!view.isInEditMode) SideEffect {
        val window = (view.context as? Activity)?.window ?: return@SideEffect
        WindowCompat.getInsetsController(window, view).apply {
            isAppearanceLightStatusBars = !p.isDark
            isAppearanceLightNavigationBars = !p.isDark
        }
    }
    MaterialTheme(colorScheme = scheme, content = content)
}
