package io.github.reyesgh.echoplex.ui

import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.TextButton
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.draw.clip
import kotlinx.coroutines.launch
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.outlined.DarkMode
import androidx.compose.material.icons.outlined.Favorite
import androidx.compose.material.icons.outlined.WbSunny
import androidx.compose.material.icons.rounded.Download
import androidx.compose.material.icons.rounded.DownloadForOffline
import androidx.compose.material.icons.rounded.MoreVert
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.Shuffle
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.ui.platform.LocalContext
import io.github.reyesgh.echoplex.data.OfflineCollection
import androidx.compose.material3.ScrollableTabRow
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigation.NavController
import coil.ImageLoader
import io.github.reyesgh.echoplex.EchoplexApp
import io.github.reyesgh.echoplex.net.Album
import io.github.reyesgh.echoplex.net.SearchResult
import io.github.reyesgh.echoplex.net.Song
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import java.time.LocalTime

private val api get() = EchoplexApp.instance.api

// ======================================================================
// Inicio
// ======================================================================

@Composable
fun HomeScreen(nav: NavController, player: PlayerUi, loader: ImageLoader, bottom: PaddingValues, toTop: Flow<Unit>) {
    val hour = remember { LocalTime.now().hour }
    val night = hour < 7 || hour >= 21
    val greeting = when (hour) { in 7..13 -> "Buenos días"; in 14..20 -> "Buenas tardes"; else -> "Buenas noches" }
    val list = rememberLazyListState()
    LaunchedEffect(Unit) { toTop.collect { list.animateScrollToItem(0) } } // tocar Inicio estando en Inicio
    LazyColumn(Modifier.fillMaxSize().statusBarsPadding(), state = list, contentPadding = bottom) {
        item {
            Row(Modifier.fillMaxWidth().padding(start = 16.dp, top = 18.dp, end = 16.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(if (night) Icons.Outlined.DarkMode else Icons.Outlined.WbSunny, null, tint = Ep.Accent, modifier = Modifier.size(30.dp))
                Text(greeting, color = Ep.Text, fontSize = 28.sp, fontWeight = FontWeight.Bold, modifier = Modifier.padding(start = 12.dp).weight(1f))
            }
        }
        // versión nueva en GitHub: aviso arriba, con el botón para actualizar
        if (EchoplexApp.instance.updater.available != null) item {
            Column(Modifier.padding(start = 16.dp, end = 16.dp, top = 14.dp).fillMaxWidth().clip(RoundedCornerShape(16.dp))
                .background(Ep.Surface).padding(16.dp)) { UpdateControls(inSettings = false) }
        }
        item { AlbumShelf("Escuchado hace poco", "recent", nav, loader) }
        item { AlbumShelf("Lo que más escuchas", "frequent", nav, loader) }
        item { AlbumShelf("Añadido recientemente", "newest", nav, loader) }
        item {
            Row(Modifier.fillMaxWidth().clickable { nav.navigate("favorites") }, verticalAlignment = Alignment.CenterVertically) {
                SectionTitle("Favoritas", Modifier.weight(1f))
                Icon(Icons.Outlined.Favorite, null, tint = Ep.Fav, modifier = Modifier.padding(end = 20.dp, top = 12.dp))
            }
        }
        item { FavoritesPreview(nav, player, loader) }
    }
}

@Composable
private fun AlbumShelf(title: String, type: String, nav: NavController, loader: ImageLoader) {
    val (albums, loading, _) = load(type) { api.albumList(type, 14) }
    if (!loading && albums.isNullOrEmpty()) return
    SectionTitle(title)
    if (albums == null) Loading()
    else LazyRow(contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = CardRow) {
        items(albums, key = { it.id }) { a -> Card(a.coverArt, a.name, a.artist, loader) { nav.navigate("album/${a.id}") } }
    }
}

@Composable
private fun FavoritesPreview(nav: NavController, player: PlayerUi, loader: ImageLoader) {
    val (songs, _, error) = load("fav-preview") { api.starred() }
    val menu = rememberSongMenu()
    SongMenu(menu, player, nav)
    when {
        error != null -> Problem(error)
        songs == null -> Loading()
        songs.isEmpty() -> Problem("Aún no tienes favoritas: toca el corazón de una canción.")
        else -> Column {
            songs.take(6).forEachIndexed { i, s -> SongRow(s, loader, player.current?.mediaId == s.id, onMenu = { menu.open(s) }) { player.play(songs, i, api) } }
        }
    }
}

// ======================================================================
// Buscar
// ======================================================================

@Composable
fun SearchScreen(nav: NavController, player: PlayerUi, loader: ImageLoader, bottom: PaddingValues) {
    var query by rememberSaveable { mutableStateOf("") }
    var result by remember { mutableStateOf<SearchResult?>(null) }
    LaunchedEffect(query) {
        if (query.isBlank()) { result = null; return@LaunchedEffect }
        delay(300)
        result = runCatching { api.search(query) }.getOrNull()
    }
    val menu = rememberSongMenu()
    SongMenu(menu, player, nav)
    LazyColumn(Modifier.fillMaxSize().statusBarsPadding(), contentPadding = bottom) {
        item {
            OutlinedTextField(query, { query = it }, placeholder = { Text("Canciones, artistas, álbumes…") }, singleLine = true,
                modifier = Modifier.fillMaxWidth().padding(16.dp))
        }
        val r = result ?: return@LazyColumn
        if (r.artist.isNotEmpty()) {
            item { SectionTitle("Artistas") }
            items(r.artist, key = { "ar" + it.id }) { a -> SimpleRow(a.name, "${a.albumCount} álbumes", a.coverArt, loader, round = true) { nav.navigate("artist/${a.id}") } }
        }
        if (r.album.isNotEmpty()) {
            item { SectionTitle("Álbumes") }
            item {
                LazyRow(contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = CardRow) {
                    items(r.album, key = { "al" + it.id }) { a -> Card(a.coverArt, a.name, a.artist, loader) { nav.navigate("album/${a.id}") } }
                }
            }
        }
        if (r.song.isNotEmpty()) {
            item { SectionTitle("Canciones") }
            itemsIndexed(r.song, key = { _, s -> "s" + s.id }) { i, s ->
                SongRow(s, loader, player.current?.mediaId == s.id, onMenu = { menu.open(s) }) { player.play(r.song, i, api) }
            }
        }
        if (r.artist.isEmpty() && r.album.isEmpty() && r.song.isEmpty()) item { Problem("Nada con «$query».") }
    }
}

// ======================================================================
// Biblioteca
// ======================================================================

private val tabs = listOf("Playlists", "Descargas", "Artistas", "Álbumes", "Carpetas", "Géneros")

@Composable
fun LibraryScreen(nav: NavController, loader: ImageLoader, bottom: PaddingValues) {
    var tab by rememberSaveable { mutableIntStateOf(0) }
    Column(Modifier.fillMaxSize().statusBarsPadding()) {
        Text("Biblioteca", color = Ep.Text, fontSize = 28.sp, fontWeight = FontWeight.Bold, modifier = Modifier.padding(start = 16.dp, top = 18.dp, bottom = 6.dp))
        ScrollableTabRow(selectedTabIndex = tab, edgePadding = 8.dp, containerColor = Ep.Bg, contentColor = Ep.Accent) {
            tabs.forEachIndexed { i, t -> Tab(selected = tab == i, onClick = { tab = i }, text = { Text(t, color = if (tab == i) Ep.Text else Ep.Text2) }) }
        }
        when (tab) {
            0 -> {
                var creating by remember { mutableStateOf(false) }
                val (lists, _, error) = load("playlists") { api.playlists() }
                Grid(lists?.map { Triple(it.coverArt, it.name, "${it.songCount} canciones") to "playlist/${it.id}" }, error, nav, loader, bottom,
                    extra = { NewTile("Nueva playlist") { creating = true } })
                if (creating) NewPlaylistDialog(onDismiss = { creating = false }) { p ->
                    creating = false
                    nav.navigate("playlist/${p.id}")
                }
            }
            1 -> {
                // lo descargado se ve aunque el PC no responda (sin conexión)
                val downloads = EchoplexApp.instance.downloads
                val cols = downloads.index.collections
                if (cols.isEmpty()) Problem("Aún no has descargado nada. En un álbum o una playlist, toca ⬇ para escucharlo sin conexión; una canción suelta, desde su menú ⋮.")
                else Grid(cols.map { c ->
                    val p = downloads.progress[c.id]
                    Triple(c.coverArt, c.name, if (p != null) "Descargando ${p.first}/${p.second}" else "${c.songIds.size} canciones") to "offline/${Uri.encode(c.id)}"
                }, null, nav, loader, bottom)
            }
            2 -> {
                val (artists, _, error) = load("artists") { api.artists() }
                if (error != null) Problem(error) else if (artists == null) Loading()
                else LazyColumn(contentPadding = bottom) {
                    items(artists, key = { it.id }) { a -> SimpleRow(a.name, null, a.coverArt, loader, round = true) { nav.navigate("artist/${a.id}") } }
                }
            }
            3 -> {
                val (albums, _, error) = load("albums") { api.albumList("alphabeticalByName", 500) }
                Grid(albums?.map { Triple(it.coverArt, it.name, it.artist) to "album/${it.id}" }, error, nav, loader, bottom)
            }
            4 -> {
                val (dirs, _, error) = load("folders") { api.indexes() }
                if (error != null) Problem(error) else if (dirs == null) Loading()
                else LazyColumn(contentPadding = bottom) {
                    items(dirs, key = { it.id }) { d -> SimpleRow(d.name, null, d.id, loader) { nav.navigate("folder/${d.id}") } }
                }
            }
            else -> {
                val (genres, _, error) = load("genres") { api.genres() }
                if (error != null) Problem(error) else if (genres == null) Loading()
                else LazyColumn(contentPadding = bottom) {
                    items(genres, key = { it.value }) { g ->
                        Column(Modifier.fillMaxWidth().clickable { nav.navigate("genre/${Uri.encode(g.value)}") }.padding(horizontal = 16.dp, vertical = 12.dp)) {
                            Text(g.value, color = Ep.Text, fontSize = 16.sp)
                            Text("${g.songCount} canciones · ${g.albumCount} álbumes", color = Ep.Text2, fontSize = 13.sp)
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun Grid(
    items: List<Pair<Triple<String?, String, String>, String>>?, error: String?, nav: NavController, loader: ImageLoader, bottom: PaddingValues,
    extra: (@Composable () -> Unit)? = null,
) {
    when {
        error != null -> Problem(error)
        items == null -> Loading()
        items.isEmpty() && extra == null -> Problem("No hay nada aquí todavía.")
        else -> LazyVerticalGrid(GridCells.Adaptive(150.dp), contentPadding = PaddingValues(start = 10.dp, end = 10.dp, bottom = bottom.calculateBottomPadding())) {
            items(items, key = { it.second }) { (card, route) ->
                Box(Modifier.padding(2.dp)) { Card(card.first, card.second, card.third, loader, width = 168.dp) { nav.navigate(route) } }
            }
            // al final de todo (p. ej. el [+] de nueva playlist)
            if (extra != null) item(key = "extra") { Box(Modifier.padding(2.dp)) { extra() } }
        }
    }
}

/** Una tarjeta del tamaño de las demás con un [+] grande, para crear algo nuevo. */
@Composable
private fun NewTile(label: String, onClick: () -> Unit) {
    Column(Modifier.width(168.dp).clip(RoundedCornerShape(8.dp)).clickable(onClick = onClick).padding(4.dp)) {
        Box(
            Modifier.fillMaxWidth().aspectRatio(1f).clip(RoundedCornerShape(8.dp)).background(Ep.Raised)
                .border(1.5.dp, Ep.Divider, RoundedCornerShape(8.dp)),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.Rounded.Add, null, tint = Ep.Accent, modifier = Modifier.size(64.dp))
        }
        Spacer(Modifier.size(6.dp))
        Text(label, color = Ep.Text, fontSize = 14.sp, fontWeight = FontWeight.Medium, maxLines = 1)
        Text(" ", fontSize = 12.sp)
    }
}

/** Pide un nombre (p. ej. para renombrar una playlist). */
@Composable
private fun NameDialog(title: String, initial: String, onDismiss: () -> Unit, onConfirm: (String) -> Unit) {
    var name by remember { mutableStateOf(initial) }
    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = Ep.Surface,
        title = { Text(title, color = Ep.Text) },
        text = {
            OutlinedTextField(
                name, { name = it }, singleLine = true,
                colors = OutlinedTextFieldDefaults.colors(focusedTextColor = Ep.Text, unfocusedTextColor = Ep.Text, cursorColor = Ep.Accent,
                    focusedBorderColor = Ep.Accent, unfocusedBorderColor = Ep.Divider),
            )
        },
        confirmButton = { TextButton(enabled = name.isNotBlank(), onClick = { onConfirm(name.trim()) }) { Text("Guardar", color = Ep.Accent) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancelar", color = Ep.Text2) } },
    )
}

/** Pide el nombre y crea la playlist vacía (las canciones se añaden luego, desde el ⋮ de cada una). */
@Composable
private fun NewPlaylistDialog(onDismiss: () -> Unit, onCreated: (io.github.reyesgh.echoplex.net.Playlist) -> Unit) {
    var name by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = Ep.Surface,
        title = { Text("Nueva playlist", color = Ep.Text) },
        text = {
            Column {
                OutlinedTextField(
                    name, { name = it }, singleLine = true, placeholder = { Text("Nombre", color = Ep.Text3) },
                    colors = OutlinedTextFieldDefaults.colors(
                        focusedTextColor = Ep.Text, unfocusedTextColor = Ep.Text, cursorColor = Ep.Accent,
                        focusedBorderColor = Ep.Accent, unfocusedBorderColor = Ep.Divider,
                    ),
                )
                error?.let { Text(it, color = Ep.Fav, fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp)) }
            }
        },
        confirmButton = {
            TextButton(enabled = name.isNotBlank() && !busy, onClick = {
                busy = true
                scope.launch {
                    runCatching { api.createPlaylist(name.trim()) }
                        .onSuccess { p -> if (p != null) onCreated(p) else { error = "Echoplex no devolvió la playlist"; busy = false } }
                        .onFailure { error = it.message ?: "No se pudo crear"; busy = false }
                }
            }) { Text("Crear", color = if (name.isNotBlank()) Ep.Accent else Ep.Text3) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancelar", color = Ep.Text2) } },
    )
}

// ======================================================================
// Páginas: álbum, artista, playlist, carpeta, género, favoritas
// ======================================================================

/** Una opción del menú ⋮ de una página (renombrar, borrar…). */
data class PageAction(val label: String, val run: () -> Unit)

/**
 * Una página con su portada, título, botones (aleatorio, reproducir, descargar y ⋮) y la lista de canciones.
 * [offline]: cómo se guarda la página al descargarla (null = sin botón de descargar). [playlistId]: para poder quitar
 * canciones desde su menú. [onChanged]: se llama cuando algo cambió y hay que recargar.
 */
@Composable
private fun SongsPage(
    nav: NavController, player: PlayerUi, loader: ImageLoader, bottom: PaddingValues,
    cover: String?, title: String, subtitle: String, songs: List<Song>?, error: String?,
    offline: OfflineCollection? = null,
    playlistId: String? = null,
    actions: List<PageAction> = emptyList(),
    onChanged: () -> Unit = {},
    extra: (@Composable () -> Unit)? = null,
) {
    val menu = rememberSongMenu()
    SongMenu(menu, player, nav, onChanged)
    val context = LocalContext.current
    fun toast(t: String) = android.widget.Toast.makeText(context, t, android.widget.Toast.LENGTH_SHORT).show()
    LazyColumn(Modifier.fillMaxSize().statusBarsPadding(), contentPadding = bottom) {
        item {
            IconButton(onClick = { nav.popBackStack() }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Atrás", tint = Ep.Text) }
        }
        item {
            Column(Modifier.fillMaxWidth().padding(horizontal = 24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Cover(cover, 230.dp, loader, corner = 10.dp, px = CoverLarge)
                Spacer(Modifier.height(14.dp))
                Text(title, color = Ep.Text, fontSize = 22.sp, fontWeight = FontWeight.Bold, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text(subtitle, color = Ep.Text2, fontSize = 14.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Spacer(Modifier.height(12.dp))
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                    val has = !songs.isNullOrEmpty()
                    IconButton(onClick = { songs?.let { player.play(it, 0, api, shuffled = true) } }, enabled = has) {
                        Icon(Icons.Rounded.Shuffle, "Aleatorio", tint = if (has) Ep.Text else Ep.Text3, modifier = Modifier.size(26.dp))
                    }
                    Box(
                        Modifier.size(60.dp).clip(CircleShape).background(if (has) Ep.Accent else Ep.Raised)
                            .clickable(enabled = has) { songs?.let { player.play(it, 0, api) } },
                        contentAlignment = Alignment.Center,
                    ) { Icon(Icons.Rounded.PlayArrow, "Reproducir", tint = Ep.OnAccent, modifier = Modifier.size(34.dp)) }
                    if (offline != null) DownloadButton(offline, songs) { toast(it) }
                    PageMenu(songs, menu, actions, player)
                }
            }
        }
        if (extra != null) item { extra() }
        when {
            error != null -> item { Problem(error) }
            songs == null -> item { Loading() }
            else -> itemsIndexed(songs, key = { i, s -> s.id + i }) { i, s ->
                SongRow(s, loader, player.current?.mediaId == s.id, onMenu = { menu.open(s, playlistId, i) }) { player.play(songs, i, api) }
            }
        }
    }
}

/** ⬇ descargar · rueda con el progreso mientras baja · ✓ descargada (tocar para quitarla, preguntando antes). */
@Composable
private fun DownloadButton(offline: OfflineCollection, songs: List<Song>?, toast: (String) -> Unit) {
    val downloads = EchoplexApp.instance.downloads
    val saved = downloads.collection(offline.id)
    val progress = downloads.progress[offline.id]
    var confirm by remember { mutableStateOf(false) }
    Box(Modifier.size(48.dp), contentAlignment = Alignment.Center) {
        when {
            progress != null -> {
                CircularProgressIndicator(
                    progress = { if (progress.second > 0) progress.first.toFloat() / progress.second else 0f },
                    modifier = Modifier.size(30.dp), color = Ep.Accent, trackColor = Ep.Divider, strokeWidth = 3.dp,
                )
                Text("${progress.first}", color = Ep.Text2, fontSize = 10.sp)
            }
            saved != null -> IconButton(onClick = { confirm = true }) {
                Icon(Icons.Rounded.DownloadForOffline, "Descargada", tint = Ep.Accent, modifier = Modifier.size(28.dp))
            }
            else -> IconButton(onClick = {
                val list = songs?.filter { !it.isDir }
                if (list.isNullOrEmpty()) return@IconButton
                downloads.download(offline, list)
                toast("Descargando ${list.size} canciones…")
            }, enabled = !songs.isNullOrEmpty()) {
                Icon(Icons.Rounded.Download, "Descargar", tint = Ep.Text, modifier = Modifier.size(26.dp))
            }
        }
    }
    if (confirm) AlertDialog(
        onDismissRequest = { confirm = false },
        containerColor = Ep.Surface,
        title = { Text("Quitar la descarga", color = Ep.Text) },
        text = { Text("«${offline.name}» se borrará del móvil. Seguirás pudiendo escucharlo desde el PC.", color = Ep.Text2) },
        confirmButton = { TextButton(onClick = { downloads.remove(offline.id); confirm = false }) { Text("Quitar", color = Ep.Fav) } },
        dismissButton = { TextButton(onClick = { confirm = false }) { Text("Cancelar", color = Ep.Text2) } },
    )
}

/** ⋮ de la página: todo a la cola o a una playlist, más lo propio de la página (renombrar o borrar una playlist…). */
@Composable
private fun PageMenu(songs: List<Song>?, menu: SongMenuState, actions: List<PageAction>, player: PlayerUi) {
    var open by remember { mutableStateOf(false) }
    val context = LocalContext.current
    fun toast(t: String) = android.widget.Toast.makeText(context, t, android.widget.Toast.LENGTH_SHORT).show()
    Box {
        IconButton(onClick = { open = true }) { Icon(Icons.Rounded.MoreVert, "Más opciones", tint = Ep.Text, modifier = Modifier.size(26.dp)) }
        DropdownMenu(open, { open = false }, containerColor = Ep.Surface) {
            val list = songs?.filter { !it.isDir }.orEmpty()
            if (list.isNotEmpty()) {
                DropdownMenuItem({ Text("Reproducir a continuación", color = Ep.Text) }, { player.playNext(list, api); toast("Sonará a continuación"); open = false })
                DropdownMenuItem({ Text("Añadir a la cola", color = Ep.Text) }, { player.addToQueue(list, api); toast("Añadido a la cola"); open = false })
                DropdownMenuItem({ Text("Añadir a una playlist…", color = Ep.Text) }, { menu.addTo = list; open = false })
            }
            actions.forEach { a -> DropdownMenuItem({ Text(a.label, color = Ep.Text) }, { open = false; a.run() }) }
        }
    }
}

@Composable
fun AlbumScreen(id: String, nav: NavController, player: PlayerUi, loader: ImageLoader, bottom: PaddingValues) {
    val (album, _, error) = load(id) { api.album(id) }
    val sub = listOfNotNull(album?.artist, album?.year?.takeIf { it > 0 }?.toString()).joinToString(" · ")
    SongsPage(nav, player, loader, bottom, album?.coverArt ?: id, album?.name ?: "", sub, album?.song, error,
        offline = album?.let { OfflineCollection("album:${it.id}", "album", it.name, it.artist, it.coverArt) })
}

@Composable
fun PlaylistScreen(id: String, nav: NavController, player: PlayerUi, loader: ImageLoader, bottom: PaddingValues) {
    var rev by remember { mutableIntStateOf(0) }
    val (list, _, error) = load("$id:$rev") { api.playlist(id) }
    var renaming by remember { mutableStateOf(false) }
    var deleting by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    fun toast(t: String) = android.widget.Toast.makeText(context, t, android.widget.Toast.LENGTH_SHORT).show()
    val editable = list != null && !list.readonly
    SongsPage(
        nav, player, loader, bottom, list?.coverArt, list?.name ?: "",
        list?.let { if (it.readonly) "${it.songCount} canciones · inteligente" else "${it.songCount} canciones" } ?: "", list?.entry, error,
        offline = list?.let { OfflineCollection("playlist:${it.id}", "playlist", it.name, "${it.songCount} canciones", it.coverArt) },
        playlistId = if (editable) id else null,
        actions = if (!editable) emptyList() else listOf(PageAction("Renombrar") { renaming = true }, PageAction("Borrar la playlist") { deleting = true }),
        onChanged = { rev++ },
    )
    if (renaming && list != null) NameDialog("Renombrar", list.name, onDismiss = { renaming = false }) { name ->
        renaming = false
        scope.launch { runCatching { api.updatePlaylist(id, name = name) }.onSuccess { rev++ }.onFailure { toast(it.message ?: "No se pudo renombrar") } }
    }
    if (deleting && list != null) AlertDialog(
        onDismissRequest = { deleting = false },
        containerColor = Ep.Surface,
        title = { Text("Borrar «${list.name}»", color = Ep.Text) },
        text = { Text("Se borra también en Echoplex del PC. Las canciones no se tocan.", color = Ep.Text2) },
        confirmButton = {
            TextButton(onClick = {
                deleting = false
                scope.launch {
                    runCatching { api.deletePlaylist(id) }
                        .onSuccess { EchoplexApp.instance.downloads.remove("playlist:$id"); nav.popBackStack() }
                        .onFailure { toast(it.message ?: "No se pudo borrar") }
                }
            }) { Text("Borrar", color = Ep.Fav) }
        },
        dismissButton = { TextButton(onClick = { deleting = false }) { Text("Cancelar", color = Ep.Text2) } },
    )
}

/** Lo descargado, desde el móvil: funciona sin conexión con el PC. */
@Composable
fun OfflineScreen(id: String, nav: NavController, player: PlayerUi, loader: ImageLoader, bottom: PaddingValues) {
    val downloads = EchoplexApp.instance.downloads
    val c = downloads.collection(id)
    if (c == null) { LaunchedEffect(Unit) { nav.popBackStack() }; return }
    val songs = downloads.songsOf(c)
    SongsPage(nav, player, loader, bottom, c.coverArt, c.name, listOf(c.subtitle, "${songs.size} canciones").filter { it.isNotBlank() }.distinct().joinToString(" · "),
        songs, null, offline = c)
}

@Composable
fun GenreScreen(name: String, nav: NavController, player: PlayerUi, loader: ImageLoader, bottom: PaddingValues) {
    val (songs, _, error) = load(name) { api.songsByGenre(name) }
    SongsPage(nav, player, loader, bottom, songs?.firstOrNull()?.coverArt, name, songs?.let { "${it.size} canciones" } ?: "", songs, error)
}

@Composable
fun FavoritesScreen(nav: NavController, player: PlayerUi, loader: ImageLoader, bottom: PaddingValues) {
    val (songs, _, error) = load("favorites") { api.starred() }
    SongsPage(nav, player, loader, bottom, songs?.firstOrNull()?.coverArt, "Favoritas", songs?.let { "${it.size} canciones" } ?: "", songs, error,
        offline = songs?.let { OfflineCollection("favorites", "favorites", "Favoritas", "", it.firstOrNull()?.coverArt) })
}

@Composable
fun ArtistScreen(id: String, nav: NavController, player: PlayerUi, loader: ImageLoader, bottom: PaddingValues) {
    val (artist, _, error) = load(id) { api.artist(id) }
    // las canciones de todos sus álbumes, para Reproducir / Aleatorio
    val (songs, _, _) = load(artist?.id) { artist?.album?.flatMap { a -> api.album(a.id)?.song ?: emptyList() } }
    SongsPage(nav, player, loader, bottom, artist?.coverArt ?: id, artist?.name ?: "", artist?.let { "${it.albumCount} álbumes" } ?: "", songs, error) {
        val albums: List<Album> = artist?.album ?: emptyList()
        if (albums.isNotEmpty()) Column {
            SectionTitle("Discografía")
            LazyRow(contentPadding = PaddingValues(horizontal = 12.dp), horizontalArrangement = CardRow) {
                items(albums, key = { it.id }) { a -> Card(a.coverArt, a.name, if (a.year > 0) a.year.toString() else "", loader) { nav.navigate("album/${a.id}") } }
            }
            SectionTitle("Canciones")
        }
    }
}

@Composable
fun FolderScreen(id: String, nav: NavController, player: PlayerUi, loader: ImageLoader, bottom: PaddingValues) {
    val (dir, _, error) = load(id) { api.directory(id) }
    val subfolders = dir?.child?.filter { it.isDir } ?: emptyList()
    val songs = dir?.child?.filter { !it.isDir }
    SongsPage(nav, player, loader, bottom, id, dir?.name ?: "", songs?.let { "${subfolders.size} carpetas · ${it.size} canciones" } ?: "", songs, error) {
        if (subfolders.isNotEmpty()) Column {
            subfolders.forEach { f -> SimpleRow(f.title, f.artist.ifBlank { null }, f.coverArt ?: f.id, loader) { nav.navigate("folder/${f.id}") } }
        }
    }
}
