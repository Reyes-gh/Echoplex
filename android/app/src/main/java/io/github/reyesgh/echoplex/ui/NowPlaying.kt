package io.github.reyesgh.echoplex.ui

import android.graphics.drawable.BitmapDrawable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.DragHandle
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.zIndex
import kotlin.math.roundToInt
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.palette.graphics.Palette
import coil.ImageLoader
import coil.request.ImageRequest
import io.github.reyesgh.echoplex.EchoplexApp
import io.github.reyesgh.echoplex.net.Lyrics
import io.github.reyesgh.echoplex.playback.PlaybackService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlin.math.cos
import kotlin.math.sin

/** Los colores de una carátula (los más vivos primero), para la aurora. */
@Composable
fun rememberCoverColors(coverId: String?, loader: ImageLoader): List<Color> {
    val context = LocalContext.current
    var colors by remember(coverId) { mutableStateOf(emptyList<Color>()) }
    LaunchedEffect(coverId) {
        val url = EchoplexApp.instance.api.coverUrl(coverId, CoverSmall) ?: return@LaunchedEffect
        val result = loader.execute(
            ImageRequest.Builder(context).data(url).allowHardware(false)
                .memoryCacheKey(coverKey(coverId, CoverSmall) + ":sw").diskCacheKey(coverKey(coverId, CoverSmall)).build()
        )
        val bitmap = (result.drawable as? BitmapDrawable)?.bitmap ?: return@LaunchedEffect
        val p = withContext(Dispatchers.Default) { Palette.from(bitmap).generate() }
        colors = listOfNotNull(p.vibrantSwatch, p.darkVibrantSwatch, p.mutedSwatch, p.lightVibrantSwatch, p.dominantSwatch)
            .map { Color(it.rgb) }.distinct().take(3)
    }
    return colors
}

/** Luces de los colores de la carátula flotando despacio detrás de todo, como en Echoplex del PC. */
@Composable
fun Aurora(colors: List<Color>, modifier: Modifier = Modifier) {
    val t by rememberInfiniteTransition(label = "aurora").animateFloat(
        0f, (2 * Math.PI).toFloat(), infiniteRepeatable(tween(40_000, easing = LinearEasing), RepeatMode.Restart), label = "t",
    )
    val strength = if (Ep.palette.isDark) 0.42f else 0.30f
    Canvas(modifier.fillMaxSize()) {
        colors.forEachIndexed { i, c ->
            val phase = t + i * 2.1f
            val center = Offset(size.width * (0.5f + 0.35f * cos(phase)), size.height * (0.32f + 0.22f * sin(phase * 0.8f)))
            val radius = size.maxDimension * (0.55f - i * 0.08f)
            drawCircle(Brush.radialGradient(listOf(c.copy(alpha = strength), Color.Transparent), center, radius), radius, center)
        }
        // abajo vuelve al fondo del tema, para que los controles se lean bien
        drawRect(Brush.verticalGradient(listOf(Color.Transparent, Ep.Bg.copy(alpha = 0.85f)), startY = size.height * 0.45f))
    }
}

/** La letra: si es sincronizada, sigue a la canción (la línea actual destacada); tocar una línea salta allí. */
@Composable
fun LyricsPane(songId: String, player: PlayerUi, modifier: Modifier = Modifier) {
    val (lyrics, loading, error) = load("lyrics:$songId") { EchoplexApp.instance.api.lyrics(songId) }
    when {
        loading -> Loading()
        error != null -> Problem(error)
        lyrics == null -> Problem("Esta canción no tiene letra (ni en el archivo, ni en Echoplex, ni en internet si lo tienes activado en el PC).")
        else -> LyricsList(lyrics, player, modifier)
    }
}

@Composable
private fun LyricsList(lyrics: Lyrics, player: PlayerUi, modifier: Modifier) {
    val state = rememberLazyListState()
    val current = if (!lyrics.synced) -1 else lyrics.line.indexOfLast { (it.start ?: Long.MAX_VALUE) <= player.position + 300 }
    LaunchedEffect(current) { if (current >= 0) state.animateScrollToItem(maxOf(0, current - 2)) }
    LazyColumn(modifier.fillMaxSize(), state = state, contentPadding = androidx.compose.foundation.layout.PaddingValues(vertical = 24.dp)) {
        itemsIndexed(lyrics.line) { i, line ->
            val active = i == current
            Text(
                line.value.ifBlank { "♪" },
                color = when {
                    !lyrics.synced -> Ep.Text
                    active -> Ep.Text
                    i < current -> Ep.Text3
                    else -> Ep.Text2
                },
                fontSize = if (active) 24.sp else 21.sp,
                fontWeight = if (active) FontWeight.Bold else FontWeight.SemiBold,
                modifier = Modifier.fillMaxWidth()
                    .clickable(enabled = lyrics.synced && line.start != null) { line.start?.let { player.seek(it) } }
                    .padding(vertical = 7.dp),
            )
        }
    }
}

/**
 * Lo que sonará después, en su orden: tocar una canción la pone a sonar, la ✕ la quita y arrastrando el asa se cambia
 * de sitio (en aleatorio no, porque el orden lo decide el aleatorio).
 */
@Composable
fun QueuePane(player: PlayerUi, loader: ImageLoader, modifier: Modifier = Modifier) {
    val upNext = remember(player.queueVersion, player.current?.mediaId, player.shuffle) { player.upNext() }
    if (upNext.isEmpty()) {
        Problem("No hay nada más en la cola.")
        return
    }
    val rowHeight = 58.dp
    val rowPx = with(LocalDensity.current) { rowHeight.toPx() }
    var dragging by remember(upNext) { mutableStateOf<Int?>(null) } // posición en upNext de la que se arrastra
    var offset by remember(upNext) { mutableFloatStateOf(0f) }
    val target = dragging?.let { (it + (offset / rowPx).roundToInt()).coerceIn(0, upNext.lastIndex) }
    val canMove = !player.shuffle
    LazyColumn(modifier.fillMaxSize()) {
        item {
            Text(if (canMove) "A continuación" else "A continuación (en aleatorio)", color = Ep.Text2, fontSize = 13.sp, modifier = Modifier.padding(vertical = 8.dp))
        }
        itemsIndexed(upNext, key = { _, it -> "${it.first}:${it.second.mediaId}" }) { pos, (index, item) ->
            val meta = item.mediaMetadata
            val dragged = dragging == pos
            // las demás se apartan para dejar hueco donde caerá la arrastrada
            val shift = when {
                dragging == null || dragged || target == null -> 0f
                pos in (dragging!! + 1)..target -> -rowPx
                pos in target until dragging!! -> rowPx
                else -> 0f
            }
            Row(
                Modifier.fillMaxWidth().height(rowHeight).zIndex(if (dragged) 1f else 0f)
                    .graphicsLayer { translationY = if (dragged) offset else shift }
                    .clip(RoundedCornerShape(8.dp)).background(if (dragged) Ep.Raised else Color.Transparent)
                    .clickable { player.playAt(index) },
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (canMove) Icon(
                    Icons.Rounded.DragHandle, "Mover", tint = Ep.Text3,
                    modifier = Modifier.padding(end = 4.dp).size(40.dp).padding(8.dp).pointerInput(pos, upNext) {
                        detectDragGestures(
                            onDragStart = { dragging = pos; offset = 0f },
                            onDragCancel = { dragging = null; offset = 0f },
                            onDragEnd = {
                                val from = dragging
                                val to = from?.let { (it + (offset / rowPx).roundToInt()).coerceIn(0, upNext.lastIndex) }
                                if (from != null && to != null && from != to) player.move(upNext[from].first, upNext[to].first)
                                dragging = null
                                offset = 0f
                            },
                        ) { change, amount -> change.consume(); offset += amount.y }
                    },
                )
                Cover(meta.extras?.getString(PlaybackService.EXTRA_COVER), 44.dp, loader, corner = 4.dp, px = CoverSmall)
                Column(Modifier.weight(1f).padding(start = 12.dp)) {
                    Text(meta.title?.toString() ?: "", color = Ep.Text, fontSize = 15.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(meta.artist?.toString() ?: "", color = Ep.Text2, fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                IconButton(onClick = { player.removeAt(index) }) { Icon(Icons.Rounded.Close, "Quitar de la cola", tint = Ep.Text3, modifier = Modifier.size(20.dp)) }
            }
        }
    }
}
