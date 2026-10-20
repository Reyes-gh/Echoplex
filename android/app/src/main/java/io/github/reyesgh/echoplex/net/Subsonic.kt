package io.github.reyesgh.echoplex.net

import io.github.reyesgh.echoplex.data.ServerConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.decodeFromJsonElement
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.FormBody
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.Request
import java.security.MessageDigest
import java.security.SecureRandom

// ---------- lo que devuelve el servidor (solo lo que usa la app; lo demás se ignora) ----------

@Serializable
data class Song(
    val id: String,
    val title: String = "",
    val artist: String = "",
    val album: String = "",
    val albumId: String? = null,
    val artistId: String? = null,
    val parent: String? = null,
    val isDir: Boolean = false,
    val coverArt: String? = null,
    val duration: Int = 0,
    val track: Int = 0,
    val year: Int = 0,
    val suffix: String = "",
    val starred: String? = null,
    val replayGain: ReplayGain? = null,
)

@Serializable
data class ReplayGain(val trackGain: Double? = null, val albumGain: Double? = null, val trackPeak: Double? = null, val albumPeak: Double? = null)

@Serializable
data class Album(
    val id: String,
    val name: String = "",
    val artist: String = "",
    val artistId: String? = null,
    val coverArt: String? = null,
    val songCount: Int = 0,
    val duration: Int = 0,
    val year: Int = 0,
    val song: List<Song> = emptyList(),
)

@Serializable
data class Artist(val id: String, val name: String = "", val coverArt: String? = null, val albumCount: Int = 0, val album: List<Album> = emptyList())

@Serializable
data class Playlist(
    val id: String,
    val name: String = "",
    val songCount: Int = 0,
    val duration: Int = 0,
    val coverArt: String? = null,
    val readonly: Boolean = false,
    val entry: List<Song> = emptyList(),
)

@Serializable
data class Genre(val value: String = "", val songCount: Int = 0, val albumCount: Int = 0)

@Serializable
data class IndexEntry(val id: String, val name: String = "")

@Serializable
data class Index(val name: String = "", val artist: List<IndexEntry> = emptyList())

@Serializable
data class Directory(val id: String, val name: String = "", val parent: String? = null, val child: List<Song> = emptyList())

@Serializable
data class SearchResult(val artist: List<Artist> = emptyList(), val album: List<Album> = emptyList(), val song: List<Song> = emptyList())

@Serializable
data class LyricLine(val start: Long? = null, val value: String = "")

@Serializable
data class Lyrics(val synced: Boolean = false, val line: List<LyricLine> = emptyList())

@Serializable
data class PlayQueue(val current: String? = null, val position: Long = 0, val entry: List<Song> = emptyList())

class SubsonicException(val code: Int, message: String) : Exception(message)

/**
 * Cliente de la API de Subsonic que sirve Echoplex en el PC. Cada petición lleva el usuario y un token de un solo uso
 * (md5 de la contraseña + una sal nueva): la contraseña no viaja nunca tal cual.
 */
class Subsonic(private val connection: Connection) {
    private val json = Json { ignoreUnknownKeys = true; coerceInputValues = true; isLenient = true }

    companion object {
        private const val CLIENT = "Echoplex Android"
        private val random = SecureRandom()

        fun buildUrl(base: String, c: ServerConfig, method: String, params: Map<String, Any?>): HttpUrl {
            val salt = ByteArray(8).also { random.nextBytes(it) }.joinToString("") { "%02x".format(it) }
            val token = MessageDigest.getInstance("MD5").digest((c.password + salt).toByteArray())
                .joinToString("") { "%02x".format(it) }
            val b = "$base/rest/$method".toHttpUrl().newBuilder()
                .addQueryParameter("u", c.user).addQueryParameter("t", token).addQueryParameter("s", salt)
                .addQueryParameter("v", "1.16.1").addQueryParameter("c", CLIENT).addQueryParameter("f", "json")
            for ((k, v) in params) {
                when (v) {
                    null -> {}
                    is Iterable<*> -> v.forEach { if (it != null) b.addQueryParameter(k, it.toString()) }
                    else -> b.addQueryParameter(k, v.toString())
                }
            }
            return b.build()
        }
    }

    private fun base(): Pair<String, ServerConfig> {
        val c = connection.config ?: throw SubsonicException(0, "Sin conectar")
        return (connection.baseUrl.value ?: c.localUrl) to c
    }

    fun url(method: String, params: Map<String, Any?> = emptyMap()): String {
        val (b, c) = base()
        return buildUrl(b, c, method, params).toString()
    }

    /** La dirección de la carátula a ese tamaño (null si no tiene). */
    fun coverUrl(id: String?, size: Int = 300): String? = id?.let { url("getCoverArt", mapOf("id" to it, "size" to size)) }

    fun streamUrl(id: String): String = url("stream", mapOf("id" to id))

    /**
     * Llama y devuelve el cuerpo de «subsonic-response» (o lanza el error del servidor). Con [post], los parámetros van
     * en el cuerpo (formPost de OpenSubsonic): para listas largas de canciones que no caben en una dirección.
     */
    private suspend fun call(method: String, params: Map<String, Any?> = emptyMap(), post: Boolean = false): JsonObject = withContext(Dispatchers.IO) {
        val request = if (!post) Request.Builder().url(url(method, params)).build() else {
            val (b, c) = base()
            val full = buildUrl(b, c, method, params)
            val form = FormBody.Builder()
            for (i in 0 until full.querySize) form.add(full.queryParameterName(i), full.queryParameterValue(i) ?: "")
            Request.Builder().url(full.newBuilder().query(null).build()).post(form.build()).build()
        }
        connection.transport.client.newCall(request).execute().use { r ->
            val body = r.body?.string() ?: throw SubsonicException(0, "Respuesta vacía (${r.code})")
            val root = json.parseToJsonElement(body).jsonObject["subsonic-response"]?.jsonObject
                ?: throw SubsonicException(0, "Respuesta rara del servidor (${r.code})")
            if (root["status"]?.jsonPrimitive?.content != "ok") {
                val e = root["error"]?.jsonObject
                throw SubsonicException(e?.get("code")?.jsonPrimitive?.content?.toIntOrNull() ?: 0, e?.get("message")?.jsonPrimitive?.content ?: "Error")
            }
            root
        }
    }

    private inline fun <reified T> JsonElement?.list(key: String): List<T> =
        (this as? JsonObject)?.get(key)?.let { json.decodeFromJsonElement<List<T>>(it) } ?: emptyList()

    private inline fun <reified T> JsonElement?.obj(): T? = this?.let { json.decodeFromJsonElement<T>(it) }

    suspend fun ping() = call("ping")

    suspend fun albumList(type: String, size: Int = 20, offset: Int = 0, extra: Map<String, Any?> = emptyMap()): List<Album> =
        call("getAlbumList2", mapOf("type" to type, "size" to size, "offset" to offset) + extra)["albumList2"].list("album")

    suspend fun album(id: String): Album? = call("getAlbum", mapOf("id" to id))["album"].obj()

    suspend fun artist(id: String): Artist? = call("getArtist", mapOf("id" to id))["artist"].obj()

    suspend fun artists(): List<Artist> =
        call("getArtists")["artists"].list<Index>("index").flatMap { idx -> idx.artist.map { Artist(it.id, it.name, it.id) } }

    suspend fun indexes(): List<IndexEntry> = call("getIndexes")["indexes"].list<Index>("index").flatMap { it.artist }

    suspend fun directory(id: String): Directory? = call("getMusicDirectory", mapOf("id" to id))["directory"].obj()

    suspend fun genres(): List<Genre> = call("getGenres")["genres"].list<Genre>("genre").sortedByDescending { it.songCount }

    suspend fun songsByGenre(genre: String): List<Song> = call("getSongsByGenre", mapOf("genre" to genre, "count" to 500))["songsByGenre"].list("song")

    suspend fun playlists(): List<Playlist> = call("getPlaylists")["playlists"].list("playlist")

    suspend fun playlist(id: String): Playlist? = call("getPlaylist", mapOf("id" to id))["playlist"].obj()

    /** Crea una playlist (vacía o con esas canciones); aparece también en Echoplex del PC. */
    suspend fun createPlaylist(name: String, songIds: List<String> = emptyList()): Playlist? =
        call("createPlaylist", mapOf("name" to name, "songId" to songIds), post = true)["playlist"].obj()

    /** Renombra, añade canciones al final o quita canciones por su posición. */
    suspend fun updatePlaylist(id: String, name: String? = null, add: List<String> = emptyList(), removeIndexes: List<Int> = emptyList()) {
        call("updatePlaylist", mapOf("playlistId" to id, "name" to name, "songIdToAdd" to add, "songIndexToRemove" to removeIndexes), post = true)
    }

    suspend fun deletePlaylist(id: String) { call("deletePlaylist", mapOf("id" to id)) }

    suspend fun song(id: String): Song? = call("getSong", mapOf("id" to id))["song"].obj()

    /** Lo último que sonó (aquí o en Echoplex del PC), para seguir donde se dejó. */
    suspend fun playQueue(): PlayQueue? = call("getPlayQueue")["playQueue"].obj()

    suspend fun savePlayQueue(ids: List<String>, current: String?, positionMs: Long) {
        call("savePlayQueue", mapOf("id" to ids, "current" to current, "position" to positionMs), post = true)
    }

    suspend fun starred(): List<Song> = call("getStarred2")["starred2"].list("song")

    suspend fun search(query: String): SearchResult =
        call("search3", mapOf("query" to query, "artistCount" to 8, "albumCount" to 12, "songCount" to 40))["searchResult3"].obj() ?: SearchResult()

    suspend fun similar(id: String, count: Int = 50): List<Song> = call("getSimilarSongs", mapOf("id" to id, "count" to count))["similarSongs"].list("song")

    /** La letra (sincronizada si la hay), la misma que en Echoplex del PC; null si no tiene. */
    suspend fun lyrics(id: String): Lyrics? =
        call("getLyricsBySongId", mapOf("id" to id))["lyricsList"].list<Lyrics>("structuredLyrics").firstOrNull()?.takeIf { it.line.isNotEmpty() }

    suspend fun star(id: String, on: Boolean) { call(if (on) "star" else "unstar", mapOf("id" to id)) }

    suspend fun scrobble(id: String, submission: Boolean) {
        call("scrobble", mapOf("id" to id, "submission" to submission, "time" to System.currentTimeMillis()))
    }
}
