package io.github.reyesgh.echoplex.ui

import android.widget.Toast
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.PlaylistAdd
import androidx.compose.material.icons.automirrored.rounded.QueueMusic
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.Album
import androidx.compose.material.icons.rounded.Download
import androidx.compose.material.icons.rounded.DownloadDone
import androidx.compose.material.icons.rounded.Person
import androidx.compose.material.icons.rounded.QueuePlayNext
import androidx.compose.material.icons.rounded.RemoveCircleOutline
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigation.NavController
import io.github.reyesgh.echoplex.EchoplexApp
import io.github.reyesgh.echoplex.data.Downloads
import io.github.reyesgh.echoplex.data.OfflineCollection
import io.github.reyesgh.echoplex.net.Song
import kotlinx.coroutines.launch

/** La canción cuyas opciones están abiertas; [playlistId] e [index] si se abrió dentro de una playlist (para quitarla). */
data class MenuTarget(val song: Song, val playlistId: String? = null, val index: Int = -1)

class SongMenuState {
    var target by mutableStateOf<MenuTarget?>(null)
    /** Canciones esperando a que se elija playlist (el diálogo sigue abierto aunque la hoja ya se haya cerrado). */
    var addTo by mutableStateOf<List<Song>?>(null)
    fun open(song: Song, playlistId: String? = null, index: Int = -1) { target = MenuTarget(song, playlistId, index) }
}

@Composable
fun rememberSongMenu() = remember { SongMenuState() }

/** Las opciones de una canción, en una hoja que sube desde abajo. [onChanged] se llama si cambió la playlist abierta. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SongMenu(state: SongMenuState, player: PlayerUi, nav: NavController, onChanged: () -> Unit = {}) {
    // antes del return: lo que se lanza al tocar una opción sigue vivo aunque la hoja ya se haya cerrado
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    state.addTo?.let { songs -> AddToPlaylistDialog(songs) { state.addTo = null; onChanged() } }
    val t = state.target ?: return
    val app = EchoplexApp.instance
    val song = t.song
    fun close() { state.target = null }
    fun toast(text: String) = Toast.makeText(context, text, Toast.LENGTH_SHORT).show()

    ModalBottomSheet(onDismissRequest = ::close, containerColor = Ep.Surface) {
        Column(Modifier.fillMaxWidth().padding(bottom = 24.dp)) {
            Text(song.title, color = Ep.Text, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(horizontal = 24.dp))
            Text(listOf(song.artist, song.album).filter { it.isNotBlank() }.joinToString(" · "), color = Ep.Text2, fontSize = 13.sp, maxLines = 1,
                overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(start = 24.dp, end = 24.dp, bottom = 10.dp))
            SheetRow(Icons.Rounded.QueuePlayNext, "Reproducir a continuación") { player.playNext(listOf(song), app.api); toast("Sonará a continuación"); close() }
            SheetRow(Icons.AutoMirrored.Rounded.QueueMusic, "Añadir a la cola") { player.addToQueue(listOf(song), app.api); toast("Añadida a la cola"); close() }
            SheetRow(Icons.AutoMirrored.Rounded.PlaylistAdd, "Añadir a una playlist…") { state.addTo = listOf(song); close() }
            if (t.playlistId != null && t.index >= 0) SheetRow(Icons.Rounded.RemoveCircleOutline, "Quitar de esta playlist") {
                scope.launch {
                    runCatching { app.api.updatePlaylist(t.playlistId, removeIndexes = listOf(t.index)) }
                        .onSuccess { onChanged() }.onFailure { toast(it.message ?: "No se pudo quitar") }
                }
                close()
            }
            if (app.downloads.hasSong(song.id)) SheetRow(Icons.Rounded.DownloadDone, "Quitar la descarga", Ep.Accent) {
                // solo si se bajó suelta; si es de un álbum o playlist descargados, se quita desde allí
                if (app.downloads.collection(Downloads.SONGS)?.songIds?.contains(song.id) == true) app.downloads.remove(Downloads.SONGS, song.id)
                else toast("Es de un álbum o playlist descargados: quítalo desde allí")
                close()
            } else SheetRow(Icons.Rounded.Download, "Descargar") {
                app.downloads.download(OfflineCollection(Downloads.SONGS, "songs", "Canciones sueltas"), listOf(song))
                toast("Descargando…")
                close()
            }
            song.albumId?.let { id -> SheetRow(Icons.Rounded.Album, "Ir al álbum") { close(); nav.navigate("album/$id") } }
            song.artistId?.let { id -> SheetRow(Icons.Rounded.Person, "Ir al artista") { close(); nav.navigate("artist/$id") } }
        }
    }
}

/** Elegir playlist (o crear una nueva) para añadir esas canciones. */
@Composable
fun AddToPlaylistDialog(songs: List<Song>, onDone: () -> Unit) {
    val app = EchoplexApp.instance
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val (lists, _, error) = load("playlists-add") { app.api.playlists().filter { !it.readonly } }
    var naming by remember { mutableStateOf(false) }
    var name by remember { mutableStateOf("") }
    fun finish(text: String) { Toast.makeText(context, text, Toast.LENGTH_SHORT).show(); onDone() }
    val ids = songs.map { it.id }
    AlertDialog(
        onDismissRequest = onDone,
        containerColor = Ep.Surface,
        title = { Text(if (naming) "Nueva playlist" else "Añadir a una playlist", color = Ep.Text) },
        text = {
            if (naming) OutlinedTextField(
                name, { name = it }, singleLine = true, placeholder = { Text("Nombre", color = Ep.Text3) },
                colors = OutlinedTextFieldDefaults.colors(focusedTextColor = Ep.Text, unfocusedTextColor = Ep.Text, cursorColor = Ep.Accent,
                    focusedBorderColor = Ep.Accent, unfocusedBorderColor = Ep.Divider),
            ) else LazyColumn(Modifier.heightIn(max = 360.dp)) {
                item { SheetRow(Icons.Rounded.Add, "Nueva playlist", Ep.Accent) { naming = true } }
                when {
                    error != null -> item { Problem(error) }
                    lists == null -> item { Loading() }
                    else -> items(lists, key = { it.id }) { p ->
                        Column(Modifier.fillMaxWidth().clickable {
                            scope.launch {
                                runCatching { app.api.updatePlaylist(p.id, add = ids) }
                                    .onSuccess { finish("Añadida a «${p.name}»") }.onFailure { finish(it.message ?: "No se pudo añadir") }
                            }
                        }.padding(horizontal = 24.dp, vertical = 12.dp)) {
                            Text(p.name, color = Ep.Text, fontSize = 16.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text("${p.songCount} canciones", color = Ep.Text3, fontSize = 12.sp)
                        }
                    }
                }
            }
        },
        confirmButton = {
            if (naming) TextButton(enabled = name.isNotBlank(), onClick = {
                scope.launch {
                    runCatching { app.api.createPlaylist(name.trim(), ids) }
                        .onSuccess { finish("Creada «${name.trim()}»") }.onFailure { finish(it.message ?: "No se pudo crear") }
                }
            }) { Text("Crear", color = Ep.Accent) }
        },
        dismissButton = { TextButton(onClick = onDone) { Text("Cancelar", color = Ep.Text2) } },
    )
}
