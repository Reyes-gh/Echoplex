package io.github.reyesgh.echoplex.playback

import android.app.PendingIntent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.media3.common.util.BitmapLoader
import androidx.media3.session.CacheBitmapLoader
import android.content.Intent
import android.media.audiofx.LoudnessEnhancer
import android.net.Uri
import android.os.Bundle
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.upstream.DefaultLoadErrorHandlingPolicy
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.ResolvingDataSource
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.session.LibraryResult
import androidx.media3.session.MediaLibraryService
import androidx.media3.session.MediaSession
import com.google.common.collect.ImmutableList
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.SettableFuture
import io.github.reyesgh.echoplex.EchoplexApp
import io.github.reyesgh.echoplex.MainActivity
import io.github.reyesgh.echoplex.data.Normalization
import io.github.reyesgh.echoplex.net.Song
import io.github.reyesgh.echoplex.net.Subsonic
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlin.math.log10
import kotlin.math.min
import kotlin.math.pow

/**
 * La música suena aquí, en segundo plano: con la app cerrada, en la notificación, en la pantalla de bloqueo, con los
 * botones de los auriculares y en Android Auto. Las canciones se piden al PC justo al cargarlas, por la dirección que
 * valga en ese momento (casa o fuera), o salen del móvil si están descargadas; sus escuchas se apuntan en el PC.
 */
class PlaybackService : MediaLibraryService() {
    private var session: MediaLibrarySession? = null
    private lateinit var player: ExoPlayer
    private val scope = MainScope()
    private val app get() = EchoplexApp.instance
    private var watch: Job? = null
    private var saveJob: Job? = null
    private var recovery: Job? = null
    private var scrobbledId: String? = null

    // volumen: normalización (gain, baja) × fundido del temporizador; lo que haya que subir lo hace el LoudnessEnhancer
    private var normMode = Normalization.OFF
    private var gainVolume = 1f
    private var fade = 1f
    private var enhancer: LoudnessEnhancer? = null

    override fun onCreate() {
        super.onCreate()
        val http = OkHttpDataSource.Factory(app.connection.transport.client)
        // echoplex://cancion/<id> → el archivo descargado si lo hay; si no, la dirección del PC que responda ahora (token nuevo)
        val resolving = ResolvingDataSource.Factory(DefaultDataSource.Factory(this, http)) { spec ->
            val uri = spec.uri
            if (uri.scheme != SCHEME) spec else {
                val id = uri.lastPathSegment ?: ""
                app.downloads.songFile(id)?.let { spec.withUri(Uri.fromFile(it)) } ?: spec.withUri(Uri.parse(app.api.streamUrl(id)))
            }
        }
        player = ExoPlayer.Builder(this)
            // más reintentos que de serie: cada uno vuelve a preguntar la dirección, así sirve la nueva tras cambiar de red
            .setMediaSourceFactory(DefaultMediaSourceFactory(this).setDataSourceFactory(resolving).setLoadErrorHandlingPolicy(DefaultLoadErrorHandlingPolicy(8)))
            .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MUSIC).build(), true)
            .setHandleAudioBecomingNoisy(true)
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build()
        player.addListener(object : Player.Listener {
            override fun onMediaItemTransition(item: MediaItem?, reason: Int) {
                scrobbledId = null
                applyGain()
                item?.mediaId?.let { id -> scope.launch { runCatching { app.api.scrobble(id, submission = false) } } }
            }

            override fun onEvents(p: Player, events: Player.Events) {
                if (events.containsAny(Player.EVENT_MEDIA_ITEM_TRANSITION, Player.EVENT_TIMELINE_CHANGED, Player.EVENT_IS_PLAYING_CHANGED)) saveQueueSoon()
                if (events.contains(Player.EVENT_TIMELINE_CHANGED)) applyGain() // en «inteligente» importa qué hay al lado
            }

            override fun onPlayWhenReadyChanged(playWhenReady: Boolean, reason: Int) {
                if (reason == Player.PLAY_WHEN_READY_CHANGE_REASON_END_OF_MEDIA_ITEM) SleepTimer.cancel()
            }

            /**
             * Se cortó por la red (sin cobertura, cambio de wifi a datos…): en cuanto el PC vuelve a responder, sigue
             * por donde iba, sin tocar nada. Si en un minuto y medio no hay manera, se queda en pausa.
             */
            override fun onPlayerError(error: PlaybackException) {
                if (error.errorCode !in 2000..2999) return // solo los de red y lectura
                recovery?.cancel()
                recovery = scope.launch {
                    app.connection.lost()
                    if (app.connection.awaitReachable(90_000) && player.playbackState == Player.STATE_IDLE) player.prepare()
                }
            }

            override fun onAudioSessionIdChanged(audioSessionId: Int) {
                enhancer?.release()
                enhancer = null
                applyGain()
            }
        })
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        session = MediaLibrarySession.Builder(this, player, LibraryCallback()).setSessionActivity(open)
            .setBitmapLoader(CacheBitmapLoader(CoverLoader())).build()
        watch = scope.launch { watchListening(app.api) }
        scope.launch { app.store.normalization.collect { normMode = it; applyGain() } }
        scope.launch { SleepTimer.endOfSong.collect { player.pauseAtEndOfMediaItems = it } }
        scope.launch { sleepLoop() }
    }

    // ---------------------------------------------------------------- normalización

    /** El volumen de cada canción según el modo elegido (el mismo cálculo que hace el PC con ReplayGain o lo medido). */
    private fun applyGain() {
        val e = player.currentMediaItem?.mediaMetadata?.extras
        fun d(key: String) = e?.getDouble(key, Double.NaN)?.takeIf { !it.isNaN() }
        val track = d(EXTRA_TRACK_GAIN) to d(EXTRA_TRACK_PEAK)
        val album = d(EXTRA_ALBUM_GAIN) to d(EXTRA_ALBUM_PEAK)
        val (db, peak) = when (normMode) {
            Normalization.TRACK -> if (track.first != null) track else album
            Normalization.ALBUM -> if (album.first != null) album else track
            Normalization.SMART -> if (albumNeighbours() && album.first != null) album else if (track.first != null) track else album
            else -> null to null
        }
        var gain = db ?: 0.0
        if (peak != null && peak > 0) gain = min(gain, -20 * log10(peak)) // sin pasarse del pico: nada de saturar
        gainVolume = if (gain < 0) 10.0.pow(gain / 20).toFloat() else 1f
        boost(if (gain > 0) gain else 0.0)
        applyVolume()
    }

    /** «Inteligente»: por álbum si la de antes o la de después son del mismo disco (escuchas el álbum), si no por canción. */
    private fun albumNeighbours(): Boolean {
        val album = player.currentMediaItem?.mediaMetadata?.extras?.getString(EXTRA_ALBUM) ?: return false
        return listOf(player.previousMediaItemIndex, player.nextMediaItemIndex).any { i ->
            i != C.INDEX_UNSET && player.getMediaItemAt(i).mediaMetadata.extras?.getString(EXTRA_ALBUM) == album
        }
    }

    /** Subir por encima del volumen normal: con el amplificador de sonido de Android. */
    private fun boost(db: Double) {
        runCatching {
            if (db <= 0) { enhancer?.enabled = false; return }
            val en = enhancer ?: LoudnessEnhancer(player.audioSessionId).also { enhancer = it }
            en.setTargetGain((db * 100).toInt())
            en.enabled = true
        }
    }

    private fun applyVolume() { player.volume = gainVolume * fade }

    // ---------------------------------------------------------------- temporizador

    private suspend fun sleepLoop() {
        while (scope.isActive) {
            delay(1000)
            val end = SleepTimer.endsAt.value
            val target = if (end == null) 1f else {
                val left = end - System.currentTimeMillis()
                if (left <= 0) {
                    player.pause()
                    SleepTimer.cancel()
                    1f
                } else (left / 15_000f).coerceAtMost(1f) // los últimos 15 s, bajando poco a poco
            }
            if (target != fade) { fade = target; applyVolume() }
        }
    }

    // ---------------------------------------------------------------- escuchas y cola

    /** Una canción cuenta como escuchada (en Echoplex del PC) con la mitad o 4 minutos, como allí. */
    private suspend fun watchListening(api: Subsonic) {
        while (scope.isActive) {
            delay(5000)
            val id = player.currentMediaItem?.mediaId ?: continue
            val duration = player.duration
            if (!player.isPlaying || id == scrobbledId || duration <= 30_000) continue
            if (player.currentPosition >= min(duration / 2, 240_000L)) {
                scrobbledId = id
                runCatching { api.scrobble(id, submission = true) }
            }
        }
    }

    /** La cola se guarda en el PC (al cambiar de canción, al pausar, al tocarla) para seguir donde se dejó. */
    private fun saveQueueSoon() {
        saveJob?.cancel()
        saveJob = scope.launch {
            delay(3000)
            val n = player.mediaItemCount
            if (n == 0) return@launch
            val ids = (0 until min(n, 1000)).map { player.getMediaItemAt(it).mediaId }
            runCatching { app.api.savePlayQueue(ids, player.currentMediaItem?.mediaId, player.currentPosition) }
        }
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaLibrarySession? = session

    override fun onTaskRemoved(rootIntent: Intent?) {
        // cerrar la app desde recientes: si no suena nada, se para el servicio; si suena, sigue
        if (!player.playWhenReady || player.mediaItemCount == 0) stopSelf()
    }

    override fun onDestroy() {
        watch?.cancel()
        scope.cancel()
        runCatching { enhancer?.release() }
        session?.run {
            player.release()
            release()
        }
        session = null
        super.onDestroy()
    }

    // ---------------------------------------------------------------- carátulas de la notificación

    /**
     * Las carátulas de la notificación, la pantalla de bloqueo y el coche. Las canciones las piden como
     * echoplex://caratula/<id>, y aquí se resuelven al momento: del móvil si está descargada, y si no del PC por la
     * dirección que valga ahora y por la misma conexión que todo lo demás (también por el Tailscale de la app).
     */
    private inner class CoverLoader : BitmapLoader {
        override fun supportsMimeType(mimeType: String) = mimeType.startsWith("image/")

        override fun decodeBitmap(data: ByteArray): ListenableFuture<Bitmap> = load { decode(data) }

        override fun loadBitmap(uri: Uri): ListenableFuture<Bitmap> = load {
            val bytes = when {
                uri.scheme == "file" -> java.io.File(uri.path ?: "").readBytes()
                uri.scheme == SCHEME && uri.host == "caratula" -> {
                    val id = uri.lastPathSegment ?: error("sin carátula")
                    app.downloads.coverFile(id)?.readBytes() ?: fetch(app.api.coverUrl(id, 600) ?: error("sin carátula"))
                }
                else -> fetch(uri.toString())
            }
            decode(bytes)
        }

        private fun fetch(url: String): ByteArray =
            app.connection.transport.client.newCall(okhttp3.Request.Builder().url(url).build()).execute().use { r ->
                if (!r.isSuccessful) error("HTTP ${r.code}")
                r.body!!.bytes()
            }

        // como mucho ~512 px: es lo que pinta la notificación, y así no se gasta memoria de más
        private fun decode(data: ByteArray): Bitmap {
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeByteArray(data, 0, data.size, bounds)
            var sample = 1
            while (maxOf(bounds.outWidth, bounds.outHeight) / (sample * 2) >= 512) sample *= 2
            return BitmapFactory.decodeByteArray(data, 0, data.size, BitmapFactory.Options().apply { inSampleSize = sample })
                ?: error("no es una imagen")
        }

        private fun load(block: () -> Bitmap): ListenableFuture<Bitmap> {
            val f = SettableFuture.create<Bitmap>()
            scope.launch(kotlinx.coroutines.Dispatchers.IO) { runCatching(block).onSuccess { f.set(it) }.onFailure { f.setException(it) } }
            return f
        }
    }

    // ---------------------------------------------------------------- Android Auto (y lo que llega de la pantalla)

    /**
     * Lo que se ve en el coche: Escuchado hace poco, Playlists, Favoritas y Descargas. Las canciones del coche llevan
     * en su id de dónde vienen (song|album|<id>|<canción>), para que al tocar una suene con todo su álbum o playlist.
     */
    private inner class LibraryCallback : MediaLibrarySession.Callback {
        private fun <T> future(block: suspend () -> T): ListenableFuture<T> {
            val f = SettableFuture.create<T>()
            scope.launch {
                runCatching { app.ready.await(); block() }.onSuccess { f.set(it) }.onFailure { f.setException(it) }
            }
            return f
        }

        override fun onGetLibraryRoot(
            session: MediaLibrarySession, browser: MediaSession.ControllerInfo, params: LibraryParams?,
        ): ListenableFuture<LibraryResult<MediaItem>> = Futures.immediateFuture(LibraryResult.ofItem(folder(ROOT, "Echoplex"), params))

        override fun onGetItem(session: MediaLibrarySession, browser: MediaSession.ControllerInfo, mediaId: String): ListenableFuture<LibraryResult<MediaItem>> =
            future {
                val songId = mediaId.substringAfterLast('|')
                val song = if (mediaId.startsWith("song|")) app.downloads.index.songs[songId] ?: app.api.song(songId) else null
                LibraryResult.ofItem(song?.let { item(it, app.api, mediaId) } ?: folder(mediaId, mediaId), null)
            }

        override fun onGetChildren(
            session: MediaLibrarySession, browser: MediaSession.ControllerInfo, parentId: String, page: Int, pageSize: Int, params: LibraryParams?,
        ): ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> = future {
            val list = runCatching { children(parentId) }.getOrDefault(emptyList())
            LibraryResult.ofItemList(ImmutableList.copyOf(list), params)
        }

        private suspend fun children(parentId: String): List<MediaItem> {
            val api = app.api
            val (kind, id) = parentId.split('|', limit = 2).let { it[0] to it.getOrNull(1) }
            return when (kind) {
                ROOT -> listOf(folder("recent", "Escuchado hace poco"), folder("playlists", "Playlists"), folder("favorites", "Favoritas"), folder("offline", "Descargas"))
                "recent" -> api.albumList("recent", 30).map { folder("album|${it.id}", it.name, it.artist, it.coverArt) }
                "playlists" -> api.playlists().map { folder("playlist|${it.id}", it.name, "${it.songCount} canciones", it.coverArt) }
                "offline" -> app.downloads.index.collections.map { folder("offline|${it.id}", it.name, it.subtitle, it.coverArt) }
                else -> songsOf(kind, id).map { item(it, api, "song|$kind|${id ?: ""}|${it.id}") }
            }
        }

        private suspend fun songsOf(kind: String, id: String?): List<Song> = when (kind) {
            "album" -> app.api.album(id ?: "")?.song ?: emptyList()
            "playlist" -> app.api.playlist(id ?: "")?.entry ?: emptyList()
            "favorites" -> app.api.starred()
            "offline" -> app.downloads.collection(id ?: "")?.let { app.downloads.songsOf(it) } ?: emptyList()
            else -> emptyList()
        }

        private fun folder(id: String, title: String, subtitle: String? = null, cover: String? = null): MediaItem =
            MediaItem.Builder().setMediaId(id).setMediaMetadata(
                MediaMetadata.Builder().setTitle(title).setSubtitle(subtitle).setIsBrowsable(true).setIsPlayable(false)
                    .setMediaType(MediaMetadata.MEDIA_TYPE_FOLDER_MIXED)
                    .setArtworkUri(cover?.let { runCatching { app.api.coverUrl(it, 300) }.getOrNull() }?.let(Uri::parse))
                    .build()
            ).build()

        // lo que llega de la pantalla o del coche viene sin dirección (Android no la deja pasar): se rehace desde el id
        override fun onAddMediaItems(
            mediaSession: MediaSession, controller: MediaSession.ControllerInfo, mediaItems: MutableList<MediaItem>,
        ): ListenableFuture<MutableList<MediaItem>> = future { mediaItems.map { resolve(it) }.toMutableList() }

        override fun onSetMediaItems(
            mediaSession: MediaSession, controller: MediaSession.ControllerInfo, mediaItems: MutableList<MediaItem>, startIndex: Int, startPositionMs: Long,
        ): ListenableFuture<MediaSession.MediaItemsWithStartPosition> = future {
            val single = mediaItems.singleOrNull()?.mediaId
            if (single != null && single.startsWith("song|")) {
                // una canción tocada en el coche: suena con todo lo de su sitio, empezando por ella
                val parts = single.split('|')
                val songs = runCatching { songsOf(parts[1], parts[2]) }.getOrDefault(emptyList())
                val start = songs.indexOfFirst { it.id == parts[3] }
                if (start >= 0) return@future MediaSession.MediaItemsWithStartPosition(songs.map { item(it, app.api) }, start, 0L)
            }
            MediaSession.MediaItemsWithStartPosition(mediaItems.map { resolve(it) }, startIndex, startPositionMs)
        }

        private suspend fun resolve(item: MediaItem): MediaItem {
            val id = item.mediaId.substringAfterLast('|')
            if (item.mediaMetadata.title == null) {
                // del coche o de otra app solo llega el id: se buscan sus datos
                val song = app.downloads.index.songs[id] ?: runCatching { app.api.song(id) }.getOrNull()
                if (song != null) return item(song, app.api)
            }
            return item.buildUpon().setMediaId(id).setUri("$SCHEME://cancion/$id").build()
        }
    }

    companion object {
        const val SCHEME = "echoplex"
        private const val ROOT = "root"
        const val EXTRA_TRACK_GAIN = "trackGain"
        const val EXTRA_ALBUM_GAIN = "albumGain"
        const val EXTRA_TRACK_PEAK = "trackPeak"
        const val EXTRA_ALBUM_PEAK = "albumPeak"
        const val EXTRA_STARRED = "starred"
        const val EXTRA_ALBUM = "albumId"
        const val EXTRA_ARTIST = "artistId"
        const val EXTRA_COVER = "coverArt"

        /** Una canción como elemento de la cola: la dirección se resuelve al cargarla (ver arriba). */
        fun item(song: Song, api: Subsonic, mediaId: String = song.id): MediaItem {
            val rg = song.replayGain
            val extras = Bundle().apply {
                putDouble(EXTRA_TRACK_GAIN, rg?.trackGain ?: Double.NaN)
                putDouble(EXTRA_ALBUM_GAIN, rg?.albumGain ?: Double.NaN)
                putDouble(EXTRA_TRACK_PEAK, rg?.trackPeak ?: Double.NaN)
                putDouble(EXTRA_ALBUM_PEAK, rg?.albumPeak ?: Double.NaN)
                putBoolean(EXTRA_STARRED, song.starred != null)
                putString(EXTRA_ALBUM, song.albumId)
                putString(EXTRA_ARTIST, song.artistId)
                putString(EXTRA_COVER, song.coverArt)
            }
            val meta = MediaMetadata.Builder()
                .setTitle(song.title)
                .setArtist(song.artist)
                .setAlbumTitle(song.album)
                // se resuelve al pintarla (CoverLoader): así vale aunque cambie la red o la dirección del PC
                .setArtworkUri(song.coverArt?.let { Uri.parse("$SCHEME://caratula/${Uri.encode(it)}") })
                .setDurationMs(song.duration * 1000L)
                .setIsPlayable(true)
                .setIsBrowsable(false)
                .setMediaType(MediaMetadata.MEDIA_TYPE_MUSIC)
                .setExtras(extras)
                .build()
            return MediaItem.Builder().setMediaId(mediaId).setUri("$SCHEME://cancion/${song.id}").setMediaMetadata(meta).build()
        }
    }
}
