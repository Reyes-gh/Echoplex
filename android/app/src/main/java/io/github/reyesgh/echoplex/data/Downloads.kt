package io.github.reyesgh.echoplex.data

import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import io.github.reyesgh.echoplex.EchoplexApp
import io.github.reyesgh.echoplex.net.Song
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import okhttp3.Request
import java.io.File

/** Algo descargado: un álbum, una playlist o las canciones sueltas. */
@Serializable
data class OfflineCollection(
    val id: String,
    val kind: String,
    val name: String,
    val subtitle: String = "",
    val coverArt: String? = null,
    val songIds: List<String> = emptyList(),
)

@Serializable
data class OfflineIndex(val songs: Map<String, Song> = emptyMap(), val collections: List<OfflineCollection> = emptyList())

/**
 * Las descargas para escuchar sin conexión: los archivos van a la carpeta privada de la app (nadie más los ve y se
 * borran al desinstalar), con su carátula. Se bajan de una en una; si la app se cierra a medias, sigue al volver.
 */
class Downloads(context: Context, private val app: EchoplexApp) {
    companion object {
        const val SONGS = "sueltas"
    }

    private val dir = File(context.filesDir, "descargas")
    private val songsDir = File(dir, "canciones").apply { mkdirs() }
    private val coversDir = File(dir, "caratulas").apply { mkdirs() }
    private val indexFile = File(dir, "indice.json")
    private val json = Json { ignoreUnknownKeys = true }

    /** Lo descargado (o pedido): cambia en el hilo principal, así la pantalla se repinta sola. */
    var index by mutableStateOf(load())
        private set

    /** Lo que se está bajando ahora: id de la colección → (hechas, total). */
    var progress by mutableStateOf<Map<String, Pair<Int, Int>>>(emptyMap())
        private set

    /** Cambia con cada archivo nuevo (para repintar las marcas de «descargada»). */
    var version by mutableStateOf(0)
        private set

    private val wake = Channel<Unit>(Channel.CONFLATED)

    init {
        app.scope.launch { worker() }
    }

    private fun load(): OfflineIndex =
        runCatching { json.decodeFromString<OfflineIndex>(indexFile.readText()) }.getOrDefault(OfflineIndex())

    private suspend fun save(i: OfflineIndex) {
        index = i
        withContext(Dispatchers.IO) {
            val tmp = File(dir, "indice.tmp")
            tmp.writeText(json.encodeToString(i))
            tmp.renameTo(indexFile)
        }
    }

    fun songFile(id: String): File? = File(songsDir, id).takeIf { it.exists() }
    fun hasSong(id: String): Boolean = File(songsDir, id).exists()
    fun coverFile(id: String?): File? = id?.let { File(coversDir, it.replace('/', '_')) }?.takeIf { it.exists() }
    fun collection(id: String): OfflineCollection? = index.collections.firstOrNull { it.id == id }
    fun songsOf(c: OfflineCollection): List<Song> = c.songIds.mapNotNull { index.songs[it] }

    /** Pide descargar una colección entera (o añadir canciones a «sueltas»). */
    fun download(c: OfflineCollection, songs: List<Song>) {
        app.scope.launch {
            val old = collection(c.id)
            val ids = if (c.id == SONGS) ((old?.songIds ?: emptyList()) + songs.map { it.id }).distinct() else songs.map { it.id }
            val merged = c.copy(songIds = ids)
            save(index.copy(
                songs = index.songs + songs.associateBy { it.id },
                collections = index.collections.filter { it.id != c.id } + merged,
            ))
            wake.trySend(Unit)
        }
    }

    /** Quita una colección (o una canción suelta) y borra los archivos que ya no use nadie. */
    fun remove(collectionId: String, songId: String? = null) {
        app.scope.launch {
            val cols = index.collections.mapNotNull { c ->
                when {
                    c.id != collectionId -> c
                    songId == null -> null
                    else -> c.copy(songIds = c.songIds - songId).takeIf { it.songIds.isNotEmpty() }
                }
            }
            val used = cols.flatMap { it.songIds }.toSet()
            val gone = index.songs.keys - used
            withContext(Dispatchers.IO) { gone.forEach { File(songsDir, it).delete() } }
            val songs = index.songs - gone
            val covers = (songs.values.mapNotNull { it.coverArt } + cols.mapNotNull { it.coverArt }).toSet()
            withContext(Dispatchers.IO) { coversDir.listFiles()?.filter { it.name !in covers.map { c -> c.replace('/', '_') } }?.forEach { it.delete() } }
            save(OfflineIndex(songs, cols))
            version++
        }
    }

    fun clear() {
        app.scope.launch {
            withContext(Dispatchers.IO) {
                songsDir.listFiles()?.forEach { it.delete() }
                coversDir.listFiles()?.forEach { it.delete() }
            }
            save(OfflineIndex())
            version++
        }
    }

    fun sizeBytes(): Long = (songsDir.listFiles()?.sumOf { it.length() } ?: 0L) + (coversDir.listFiles()?.sumOf { it.length() } ?: 0L)

    /** Vuelve a mirar si falta algo por bajar (al abrir la app o al recuperar la conexión). */
    fun resume() { wake.trySend(Unit) }

    private suspend fun worker() {
        for (signal in wake) {
            while (true) {
                // la primera colección a la que le falte algo
                val c = index.collections.firstOrNull { col -> col.songIds.any { !hasSong(it) } } ?: break
                val missing = c.songIds.filter { !hasSong(it) }
                var done = c.songIds.size - missing.size
                progress = progress + (c.id to (done to c.songIds.size))
                var failed = false
                coverOf(c.coverArt)
                for (id in missing) {
                    if (collection(c.id) == null) break // la han quitado mientras bajaba
                    val ok = fetchSong(id)
                    if (!ok) { failed = true; break }
                    index.songs[id]?.coverArt?.let { coverOf(it) }
                    done++
                    progress = progress + (c.id to (done to c.songIds.size))
                    version++
                }
                progress = progress - c.id
                if (failed) break // sin conexión con el PC: se reintenta en el próximo aviso
            }
        }
    }

    private suspend fun fetchSong(id: String): Boolean = withContext(Dispatchers.IO) {
        runCatching {
            val quality = app.store.downloadQuality.first()
            val url = if (quality == "raw") app.api.url("download", mapOf("id" to id))
            else app.api.url("stream", mapOf("id" to id, "format" to "mp3", "maxBitRate" to quality))
            val tmp = File(songsDir, "$id.part")
            app.connection.transport.client.newCall(Request.Builder().url(url).build()).execute().use { r ->
                if (!r.isSuccessful || r.header("Content-Type")?.contains("json") == true) error("HTTP ${r.code}")
                r.body!!.byteStream().use { input -> tmp.outputStream().use { input.copyTo(it) } }
            }
            check(tmp.length() > 0)
            tmp.renameTo(File(songsDir, id))
        }.getOrDefault(false)
    }

    private suspend fun coverOf(coverId: String?) {
        if (coverId == null || coverFile(coverId) != null) return
        withContext(Dispatchers.IO) {
            runCatching {
                val url = app.api.coverUrl(coverId, 600) ?: return@runCatching
                val target = File(coversDir, coverId.replace('/', '_'))
                app.connection.transport.client.newCall(Request.Builder().url(url).build()).execute().use { r ->
                    if (r.isSuccessful) r.body!!.byteStream().use { input -> target.outputStream().use { input.copyTo(it) } }
                }
            }
        }
    }
}
