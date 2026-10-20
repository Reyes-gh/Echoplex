package io.github.reyesgh.echoplex.ui

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.material.icons.rounded.DownloadDone
import androidx.compose.material.icons.rounded.MoreVert
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.outlined.FavoriteBorder
import androidx.compose.material.icons.outlined.MusicNote
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil.ImageLoader
import coil.compose.AsyncImage
import coil.request.ImageRequest
import io.github.reyesgh.echoplex.EchoplexApp
import io.github.reyesgh.echoplex.net.Song
import kotlinx.coroutines.launch

/** Las carátulas se piden por la misma conexión que todo lo demás (preparado para Tailscale integrado). */
@Composable
fun rememberImageLoader(): ImageLoader {
    val context = LocalContext.current
    return remember { ImageLoader.Builder(context).okHttpClient(EchoplexApp.instance.connection.transport.client).crossfade(true).build() }
}

@Composable
fun Cover(id: String?, size: Dp, loader: ImageLoader, modifier: Modifier = Modifier, corner: Dp = 6.dp, px: Int = CoverMedium) {
    val api = EchoplexApp.instance.api
    Box(
        modifier.size(size).clip(RoundedCornerShape(corner)).background(Ep.Raised),
        contentAlignment = Alignment.Center,
    ) {
        Icon(Icons.Outlined.MusicNote, null, tint = Ep.Text3, modifier = Modifier.size(size / 3))
        val context = LocalContext.current
        // si está descargada, del móvil; si no, del PC. La dirección lleva un token de un solo uso: la caché se guarda
        // por carátula y tamaño, así se baja una vez
        val local = EchoplexApp.instance.downloads.coverFile(id)
        val request = remember(id, px, local != null) {
            if (local != null) ImageRequest.Builder(context).data(local).memoryCacheKey(coverKey(id, px) + ":f").size(px).crossfade(true).build()
            else runCatching { api.coverUrl(id, px) }.getOrNull()?.let { url ->
                ImageRequest.Builder(context).data(url).memoryCacheKey(coverKey(id, px)).diskCacheKey(coverKey(id, px)).crossfade(true).build()
            }
        }
        if (request != null) AsyncImage(request, null, loader, Modifier.fillMaxSize(), contentScale = ContentScale.Crop)
    }
}

/** Tarjeta cuadrada (álbum, playlist, artista) con su título y un subtítulo. */
/** Clave de caché de una carátula (sin el token de la dirección). */
fun coverKey(id: String?, px: Int) = "cover:$id:$px"

/** Tamaños de carátula: pocos, para que el PC haga menos versiones y se reaprovechen. */
const val CoverSmall = 120
const val CoverMedium = 300
const val CoverLarge = 600

@Composable
fun Card(cover: String?, title: String, subtitle: String, loader: ImageLoader, width: Dp = 148.dp, onClick: () -> Unit) {
    Column(Modifier.width(width).clip(RoundedCornerShape(8.dp)).clickable(onClick = onClick).padding(4.dp)) {
        Cover(cover, width - 8.dp, loader, Modifier.aspectRatio(1f), corner = 8.dp)
        Spacer(Modifier.size(6.dp))
        Text(title, color = Ep.Text, fontSize = 14.sp, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(subtitle, color = Ep.Text2, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
fun SectionTitle(text: String, modifier: Modifier = Modifier) {
    Text(text, color = Ep.Text, fontSize = 19.sp, fontWeight = FontWeight.SemiBold, modifier = modifier.padding(start = 16.dp, top = 22.dp, bottom = 10.dp))
}

/**
 * Una canción en una lista: carátula, título, artista (con una marca si está descargada), duración, corazón y ⋮.
 * Mantener pulsada o el ⋮ abre sus opciones ([onMenu]).
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun SongRow(song: Song, loader: ImageLoader, playing: Boolean, onMenu: (() -> Unit)? = null, onClick: () -> Unit) {
    val app = EchoplexApp.instance
    val scope = rememberCoroutineScope()
    var fav by remember(song.id) { mutableStateOf(song.starred != null) }
    val downloaded = remember(song.id, app.downloads.version) { app.downloads.hasSong(song.id) }
    Row(
        Modifier.fillMaxWidth().combinedClickable(onClick = onClick, onLongClick = onMenu).padding(start = 16.dp, end = 4.dp, top = 7.dp, bottom = 7.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Cover(song.coverArt, 46.dp, loader, corner = 4.dp, px = CoverSmall)
        Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
            Text(song.title, color = if (playing) Ep.Accent else Ep.Text, fontSize = 15.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (downloaded) Icon(Icons.Rounded.DownloadDone, "Descargada", tint = Ep.Accent, modifier = Modifier.padding(end = 4.dp).size(14.dp))
                Text(song.artist, color = Ep.Text2, fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        if (song.duration > 0) Text(time(song.duration * 1000L), color = Ep.Text3, fontSize = 12.sp)
        IconButton(onClick = {
            fav = !fav
            scope.launch { runCatching { app.api.star(song.id, fav) }.onFailure { fav = !fav } }
        }) {
            Icon(if (fav) Icons.Filled.Favorite else Icons.Outlined.FavoriteBorder, "Favorita", tint = if (fav) Ep.Fav else Ep.Text3, modifier = Modifier.size(20.dp))
        }
        if (onMenu != null) IconButton(onClick = onMenu, Modifier.size(36.dp)) {
            Icon(Icons.Rounded.MoreVert, "Más opciones", tint = Ep.Text3, modifier = Modifier.size(20.dp))
        }
    }
}

/** Una fila sencilla (carpeta, género, artista en lista). */
@Composable
fun SimpleRow(title: String, subtitle: String?, cover: String?, loader: ImageLoader, round: Boolean = false, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Cover(cover, 50.dp, loader, corner = if (round) 25.dp else 6.dp, px = CoverSmall)
        Column(Modifier.padding(start = 12.dp)) {
            Text(title, color = Ep.Text, fontSize = 15.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (!subtitle.isNullOrBlank()) Text(subtitle, color = Ep.Text2, fontSize = 13.sp, maxLines = 1)
        }
    }
}

@Composable
fun Loading() = Box(Modifier.fillMaxWidth().padding(40.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Ep.Accent) }

@Composable
fun Problem(text: String) = Text(text, color = Ep.Text2, fontSize = 14.sp, modifier = Modifier.padding(24.dp))

/**
 * Carga algo de la API: null mientras llega, y el error si falla. Si falló por no llegar al PC, se vuelve a cargar
 * sola en cuanto el PC responde otra vez.
 */
@Composable
fun <T> load(key: Any?, block: suspend () -> T): Triple<T?, Boolean, String?> {
    var value by remember(key) { mutableStateOf<T?>(null) }
    var loading by remember(key) { mutableStateOf(true) }
    var error by remember(key) { mutableStateOf<String?>(null) }
    var attempt by remember(key) { mutableStateOf(0) }
    val reachable by EchoplexApp.instance.connection.reachable.collectAsState()
    LaunchedEffect(key, attempt) {
        loading = true
        runCatching { block() }.onSuccess { value = it; error = null }.onFailure { error = friendly(it) }
        loading = false
    }
    // falló por la red y el PC ya responde: otra vez
    LaunchedEffect(reachable) { if (reachable && error != null && value == null) attempt++ }
    return Triple(value, loading, error)
}

/** El error, dicho para personas: sin red no es «failed to connect», es que el PC no responde. */
private fun friendly(e: Throwable): String = when (e) {
    is java.io.IOException -> "No llego a tu Echoplex. Mira que esté abierto en el PC (y, fuera de casa, Tailscale en Ajustes). Lo sigo intentando: en cuanto responda, se carga solo."
    else -> e.message ?: "No se pudo cargar"
}

fun time(ms: Long): String {
    val s = (ms / 1000).coerceAtLeast(0)
    return "%d:%02d".format(s / 60, s % 60)
}

@Composable
fun HSpacer(w: Dp) = Spacer(Modifier.width(w))

val CardRow = Arrangement.spacedBy(6.dp)
