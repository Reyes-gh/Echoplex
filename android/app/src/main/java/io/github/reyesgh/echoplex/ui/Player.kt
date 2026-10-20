package io.github.reyesgh.echoplex.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.material.icons.rounded.Bedtime
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.Timer
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.snapshotFlow
import io.github.reyesgh.echoplex.playback.SleepTimer
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.QueueMusic
import androidx.compose.material.icons.rounded.Favorite
import androidx.compose.material.icons.rounded.FavoriteBorder
import androidx.compose.material.icons.rounded.KeyboardArrowDown
import androidx.compose.material.icons.rounded.Lyrics
import androidx.compose.material.icons.rounded.Pause
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.Repeat
import androidx.compose.material.icons.rounded.RepeatOne
import androidx.compose.material.icons.rounded.Shuffle
import androidx.compose.material.icons.rounded.SkipNext
import androidx.compose.material.icons.rounded.SkipPrevious
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.media3.common.Player
import coil.ImageLoader
import io.github.reyesgh.echoplex.EchoplexApp
import io.github.reyesgh.echoplex.playback.PlaybackService
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Mientras suena, la posición se pide cada medio segundo. */
@Composable
fun TickWhilePlaying(player: PlayerUi) {
    LaunchedEffect(player.isPlaying) {
        while (player.isPlaying) {
            player.tick()
            delay(500)
        }
    }
}

/** La barra de abajo: lo que suena, play/pausa y siguiente. Un toque abre la pantalla completa. */
@Composable
fun MiniPlayer(player: PlayerUi, loader: ImageLoader, onOpen: () -> Unit) {
    val item = player.current ?: return
    val meta = item.mediaMetadata
    Column(Modifier.fillMaxWidth().background(Ep.Surface).clickable(onClick = onOpen)) {
        // barra fina de progreso (propia: la de Material lleva un punto al final que aquí sobra)
        val progress = if (player.duration > 0) (player.position.toFloat() / player.duration).coerceIn(0f, 1f) else 0f
        Box(Modifier.fillMaxWidth().height(3.dp).background(Ep.Divider)) {
            Box(Modifier.fillMaxWidth(progress).fillMaxHeight().background(Ep.Accent))
        }
        Row(Modifier.fillMaxWidth().padding(start = 12.dp, end = 4.dp, top = 8.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Cover(meta.extras?.getString(PlaybackService.EXTRA_COVER), 44.dp, loader, corner = 6.dp, px = CoverSmall)
            Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                Text(meta.title?.toString() ?: "", color = Ep.Text, fontSize = 14.sp, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(meta.artist?.toString() ?: "", color = Ep.Text2, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            IconButton(onClick = { player.toggle() }) {
                Icon(if (player.isPlaying) Icons.Rounded.Pause else Icons.Rounded.PlayArrow, "Reproducir o pausar", tint = Ep.Text, modifier = Modifier.size(30.dp))
            }
            IconButton(onClick = { player.next() }) { Icon(Icons.Rounded.SkipNext, "Siguiente", tint = Ep.Text, modifier = Modifier.size(28.dp)) }
        }
    }
}

/** La pantalla de reproducción: carátula grande (o letra o cola), barra, controles y, abajo, los botones de letra y cola. */
@Composable
fun FullPlayer(player: PlayerUi, loader: ImageLoader, onClose: () -> Unit) {
    val item = player.current ?: run {
        LaunchedEffect(Unit) { onClose() } // se acabó la cola: se cierra sola
        return
    }
    val meta = item.mediaMetadata
    val scope = rememberCoroutineScope()
    var dragging by remember { mutableStateOf<Float?>(null) }
    var mode by remember { mutableStateOf(0) } // 0 portada · 1 letra · 2 cola
    val cover = meta.extras?.getString(PlaybackService.EXTRA_COVER)
    val colors = rememberCoverColors(cover, loader)
    Box(Modifier.fillMaxSize().background(Ep.Bg)) {
    Aurora(colors)
    Column(
        Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().padding(horizontal = 24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Box(Modifier.fillMaxWidth().padding(top = 4.dp), contentAlignment = Alignment.Center) {
            IconButton(onClick = onClose, Modifier.align(Alignment.CenterStart)) {
                Icon(Icons.Rounded.KeyboardArrowDown, "Cerrar", tint = Ep.Text, modifier = Modifier.size(32.dp))
            }
            Text(meta.albumTitle?.toString() ?: "", color = Ep.Text2, fontSize = 13.sp, fontWeight = FontWeight.Medium, maxLines = 1,
                overflow = TextOverflow.Ellipsis, textAlign = TextAlign.Center, modifier = Modifier.padding(horizontal = 56.dp))
        }
        Box(Modifier.weight(1f).fillMaxWidth().padding(vertical = 12.dp), contentAlignment = Alignment.Center) {
            when (mode) {
                1 -> LyricsPane(item.mediaId, player)
                2 -> QueuePane(player, loader)
                else -> CoverPager(player, loader)
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(meta.title?.toString() ?: "", color = Ep.Text, fontSize = 22.sp, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(meta.artist?.toString() ?: "", color = Ep.Text2, fontSize = 15.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            IconButton(onClick = {
                val on = !player.starred
                player.starred = on
                scope.launch { runCatching { EchoplexApp.instance.api.star(item.mediaId, on) }.onFailure { player.starred = !on } }
            }) {
                Icon(if (player.starred) Icons.Rounded.Favorite else Icons.Rounded.FavoriteBorder, "Favorita",
                    tint = if (player.starred) Ep.Fav else Ep.Text2, modifier = Modifier.size(28.dp))
            }
        }
        Spacer(Modifier.height(10.dp))
        val dur = player.duration.coerceAtLeast(1L)
        Slider(
            value = dragging ?: (player.position.toFloat() / dur).coerceIn(0f, 1f),
            onValueChange = { dragging = it },
            onValueChangeFinished = { dragging?.let { player.seek((it * dur).toLong()) }; dragging = null },
            colors = SliderDefaults.colors(thumbColor = Ep.Text, activeTrackColor = Ep.Accent, inactiveTrackColor = Ep.Divider),
        )
        Row(Modifier.fillMaxWidth()) {
            Text(time(dragging?.let { (it * dur).toLong() } ?: player.position), color = Ep.Text3, fontSize = 12.sp)
            Spacer(Modifier.weight(1f))
            Text(time(player.duration), color = Ep.Text3, fontSize = 12.sp)
        }
        Spacer(Modifier.height(6.dp))
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            Toggle(Icons.Rounded.Shuffle, "Aleatorio", player.shuffle) { player.toggleShuffle() }
            IconButton(onClick = { player.previous() }, Modifier.size(56.dp)) {
                Icon(Icons.Rounded.SkipPrevious, "Anterior", tint = Ep.Text, modifier = Modifier.size(40.dp))
            }
            Box(Modifier.size(70.dp).clip(CircleShape).background(Ep.PlayCircle).clickable { player.toggle() }, contentAlignment = Alignment.Center) {
                Icon(if (player.isPlaying) Icons.Rounded.Pause else Icons.Rounded.PlayArrow, "Reproducir o pausar", tint = Ep.OnPlayCircle, modifier = Modifier.size(40.dp))
            }
            IconButton(onClick = { player.next() }, Modifier.size(56.dp)) {
                Icon(Icons.Rounded.SkipNext, "Siguiente", tint = Ep.Text, modifier = Modifier.size(40.dp))
            }
            Toggle(if (player.repeat == Player.REPEAT_MODE_ONE) Icons.Rounded.RepeatOne else Icons.Rounded.Repeat, "Repetir",
                player.repeat != Player.REPEAT_MODE_OFF) { player.cycleRepeat() }
        }
        Spacer(Modifier.height(14.dp))
        // abajo, a mano del pulgar: letra a un lado, cola al otro (tocar otra vez vuelve a la carátula)
        val endsAt by SleepTimer.endsAt.collectAsState()
        val endOfSong by SleepTimer.endOfSong.collectAsState()
        var timerSheet by remember { mutableStateOf(false) }
        val timerCaption = when {
            endOfSong -> "fin"
            endsAt != null -> "${((endsAt!! - System.currentTimeMillis()) / 60_000 + 1).coerceAtLeast(1)} min"
            else -> null
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Toggle(Icons.Rounded.Lyrics, "Letra", mode == 1) { mode = if (mode == 1) 0 else 1 }
            Toggle(Icons.Rounded.Bedtime, "Temporizador", timerCaption != null, timerCaption) { timerSheet = true }
            Toggle(Icons.AutoMirrored.Rounded.QueueMusic, "Cola", mode == 2) { mode = if (mode == 2) 0 else 2 }
        }
        Spacer(Modifier.height(8.dp))
        if (timerSheet) SleepSheet { timerSheet = false }
    }
    }
}

/**
 * La carátula se desliza como en Tempus: a la izquierda, la siguiente; a la derecha, la anterior (en el orden en que
 * suenan, también en aleatorio). Si la canción cambia por otro lado, la carátula la sigue.
 */
@Composable
private fun CoverPager(player: PlayerUi, loader: ImageLoader) {
    val order = remember(player.queueVersion, player.shuffle, player.controller) { player.order() }
    if (order.isEmpty()) return
    val pos = order.indexOf(player.currentIndex).coerceAtLeast(0)
    val pager = rememberPagerState(initialPage = pos) { order.size }
    val latestPos by rememberUpdatedState(pos)
    val latestOrder by rememberUpdatedState(order)
    LaunchedEffect(pos, order.size) {
        if (pager.currentPage == pos || pager.isScrollInProgress) return@LaunchedEffect
        if (kotlin.math.abs(pager.currentPage - pos) == 1) pager.animateScrollToPage(pos) else pager.scrollToPage(pos)
    }
    LaunchedEffect(pager) {
        snapshotFlow { pager.settledPage }.collect { page ->
            if (page != latestPos && page in latestOrder.indices) player.jumpTo(latestOrder[page])
        }
    }
    HorizontalPager(pager, Modifier.fillMaxWidth(), pageSpacing = 20.dp, beyondViewportPageCount = 1) { page ->
        val cover = player.itemAt(order.getOrElse(page) { -1 })?.mediaMetadata?.extras?.getString(PlaybackService.EXTRA_COVER)
        Box(Modifier.fillMaxWidth().aspectRatio(1f)) {
            Cover(cover, 1000.dp, loader, Modifier.fillMaxSize(), corner = 12.dp, px = CoverLarge)
        }
    }
}

/** Temporizador de apagado: a qué hora se para la música (bajando poco a poco) o al acabar esta canción. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SleepSheet(onClose: () -> Unit) {
    ModalBottomSheet(onDismissRequest = onClose, containerColor = Ep.Surface) {
        Column(Modifier.fillMaxWidth().padding(bottom = 24.dp)) {
            Text("Temporizador de apagado", color = Ep.Text, fontSize = 18.sp, fontWeight = FontWeight.SemiBold,
                modifier = Modifier.padding(start = 24.dp, bottom = 8.dp))
            listOf(15, 30, 45, 60, 90).forEach { m ->
                SheetRow(Icons.Rounded.Timer, "$m minutos") { SleepTimer.minutes(m); onClose() }
            }
            SheetRow(Icons.Rounded.Bedtime, "Al acabar esta canción") { SleepTimer.atEndOfSong(); onClose() }
            if (SleepTimer.active) SheetRow(Icons.Rounded.Close, "Apagar el temporizador") { SleepTimer.cancel(); onClose() }
        }
    }
}

/** Una opción de una hoja de abajo: icono y texto. */
@Composable
fun SheetRow(icon: ImageVector, text: String, tint: androidx.compose.ui.graphics.Color = Ep.Text2, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 24.dp, vertical = 14.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, null, tint = tint, modifier = Modifier.size(22.dp))
        Text(text, color = Ep.Text, fontSize = 16.sp, modifier = Modifier.padding(start = 18.dp))
    }
}

/** Botón de icono que se enciende (color de acento y un puntito debajo, o un texto corto como «23 min»). */
@Composable
private fun Toggle(icon: ImageVector, label: String, on: Boolean, caption: String? = null, onClick: () -> Unit) {
    Column(
        Modifier.size(width = 64.dp, height = 48.dp).clip(RoundedCornerShape(16.dp)).clickable(onClick = onClick),
        horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center,
    ) {
        Icon(icon, label, tint = if (on) Ep.Accent else Ep.Text2, modifier = Modifier.size(24.dp))
        if (caption != null) Text(caption, color = Ep.Accent, fontSize = 10.sp, fontWeight = FontWeight.Medium, maxLines = 1)
        else Box(Modifier.padding(top = 3.dp).size(4.dp).clip(CircleShape).background(if (on) Ep.Accent else androidx.compose.ui.graphics.Color.Transparent))
    }
}
