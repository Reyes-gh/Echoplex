package io.github.reyesgh.echoplex.ui

import android.content.ComponentName
import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import io.github.reyesgh.echoplex.net.Song
import io.github.reyesgh.echoplex.net.Subsonic
import io.github.reyesgh.echoplex.playback.PlaybackService

/** El reproductor visto desde la pantalla: lo que suena y los botones (la música en sí va en PlaybackService). */
class PlayerUi(context: Context) {
    var controller by mutableStateOf<MediaController?>(null)
        private set
    var current by mutableStateOf<MediaItem?>(null)
        private set
    var isPlaying by mutableStateOf(false)
        private set
    var position by mutableLongStateOf(0L)
    var duration by mutableLongStateOf(0L)
        private set
    var shuffle by mutableStateOf(false)
        private set
    var repeat by mutableIntStateOf(Player.REPEAT_MODE_OFF)
        private set
    var starred by mutableStateOf(false)
    /** Cambia cada vez que cambia la cola (para repintar la lista). */
    var queueVersion by mutableIntStateOf(0)
        private set

    private val listener = object : Player.Listener {
        override fun onEvents(player: Player, events: Player.Events) {
            if (events.contains(Player.EVENT_TIMELINE_CHANGED) || events.contains(Player.EVENT_SHUFFLE_MODE_ENABLED_CHANGED)) queueVersion++
            sync(player)
        }
    }

    init {
        val token = SessionToken(context, ComponentName(context, PlaybackService::class.java))
        val future = MediaController.Builder(context, token).buildAsync()
        future.addListener({
            runCatching { future.get() }.getOrNull()?.let { c ->
                controller = c
                c.addListener(listener)
                sync(c)
            }
        }, ContextCompat.getMainExecutor(context))
    }

    private fun sync(p: Player) {
        if (current?.mediaId != p.currentMediaItem?.mediaId)
            starred = p.currentMediaItem?.mediaMetadata?.extras?.getBoolean(PlaybackService.EXTRA_STARRED) ?: false
        current = p.currentMediaItem
        isPlaying = p.isPlaying
        duration = p.duration.coerceAtLeast(0L)
        position = p.currentPosition
        shuffle = p.shuffleModeEnabled
        repeat = p.repeatMode
    }

    /** Pone una lista a sonar desde una canción. */
    fun play(songs: List<Song>, index: Int, api: Subsonic, shuffled: Boolean = false) {
        val c = controller ?: return
        val list = songs.filter { !it.isDir }
        if (list.isEmpty()) return
        val start = list.indexOfFirst { it.id == songs.getOrNull(index)?.id }.coerceAtLeast(0)
        c.shuffleModeEnabled = shuffled
        c.setMediaItems(list.map { PlaybackService.item(it, api) }, if (shuffled) (list.indices).random() else start, 0L)
        c.prepare()
        c.play()
    }

    fun toggle() = controller?.let { if (it.isPlaying) it.pause() else it.play() }
    fun next() = controller?.seekToNext()
    fun previous() = controller?.seekToPrevious()
    fun seek(ms: Long) = controller?.seekTo(ms)
    fun toggleShuffle() = controller?.let { it.shuffleModeEnabled = !it.shuffleModeEnabled }
    fun cycleRepeat() = controller?.let {
        it.repeatMode = when (it.repeatMode) {
            Player.REPEAT_MODE_OFF -> Player.REPEAT_MODE_ALL
            Player.REPEAT_MODE_ALL -> Player.REPEAT_MODE_ONE
            else -> Player.REPEAT_MODE_OFF
        }
    }

    /** Pide la posición al reproductor (la pantalla lo llama cada medio segundo mientras suena). */
    fun tick() = controller?.let {
        position = it.currentPosition
        duration = it.duration.coerceAtLeast(0L)
    }

    /** Lo que sonará después, en el orden en que sonará (también en aleatorio): índice en la cola y canción. */
    fun upNext(max: Int = 100): List<Pair<Int, MediaItem>> {
        val c = controller ?: return emptyList()
        val timeline = c.currentTimeline
        if (timeline.isEmpty) return emptyList()
        val result = mutableListOf<Pair<Int, MediaItem>>()
        var i = timeline.getNextWindowIndex(c.currentMediaItemIndex, Player.REPEAT_MODE_OFF, c.shuffleModeEnabled)
        while (i != androidx.media3.common.C.INDEX_UNSET && result.size < max) {
            result += i to c.getMediaItemAt(i)
            i = timeline.getNextWindowIndex(i, Player.REPEAT_MODE_OFF, c.shuffleModeEnabled)
        }
        return result
    }

    fun playAt(index: Int) = controller?.let { it.seekTo(index, 0L); it.play() }

    /** Toda la cola en el orden en que suena (también en aleatorio): índices de la cola. Para deslizar la carátula. */
    fun order(): List<Int> {
        val c = controller ?: return emptyList()
        val timeline = c.currentTimeline
        if (timeline.isEmpty) return emptyList()
        val result = ArrayList<Int>(timeline.windowCount)
        var i = timeline.getFirstWindowIndex(c.shuffleModeEnabled)
        while (i != androidx.media3.common.C.INDEX_UNSET) {
            result += i
            i = timeline.getNextWindowIndex(i, Player.REPEAT_MODE_OFF, c.shuffleModeEnabled)
        }
        return result
    }

    val currentIndex: Int get() = controller?.currentMediaItemIndex ?: -1

    fun itemAt(index: Int): MediaItem? = controller?.takeIf { index in 0 until it.mediaItemCount }?.getMediaItemAt(index)

    /** Salta a esa canción sin cambiar si suena o está en pausa (deslizar la carátula). */
    fun jumpTo(index: Int) = controller?.seekTo(index, 0L)

    /** Justo después de la que suena (si no suena nada, empieza a sonar). */
    fun playNext(songs: List<Song>, api: Subsonic) {
        val c = controller ?: return
        if (c.mediaItemCount == 0) return play(songs, 0, api)
        c.addMediaItems(c.currentMediaItemIndex + 1, songs.filter { !it.isDir }.map { PlaybackService.item(it, api) })
    }

    /** Al final de la cola. */
    fun addToQueue(songs: List<Song>, api: Subsonic) {
        val c = controller ?: return
        if (c.mediaItemCount == 0) return play(songs, 0, api)
        c.addMediaItems(songs.filter { !it.isDir }.map { PlaybackService.item(it, api) })
    }

    fun removeAt(index: Int) = controller?.removeMediaItem(index)
    fun move(from: Int, to: Int) = controller?.moveMediaItem(from, to)

    /**
     * Seguir donde se dejó: si al abrir no suena nada, carga lo último que sonó (aquí o en Echoplex del PC), en pausa
     * y en su punto. Una vez por arranque.
     */
    suspend fun restore(api: Subsonic) {
        if (restored) return
        restored = true
        val c = controller ?: return
        if (c.mediaItemCount > 0) return
        val q = runCatching { api.playQueue() }.getOrNull() ?: return
        val songs = q.entry.filter { !it.isDir }
        if (songs.isEmpty() || c.mediaItemCount > 0) return
        val start = songs.indexOfFirst { it.id == q.current }.coerceAtLeast(0)
        c.setMediaItems(songs.map { PlaybackService.item(it, api) }, start, q.position)
        c.prepare()
    }

    private var restored = false

    fun release() {
        controller?.removeListener(listener)
        controller?.release()
    }
}
