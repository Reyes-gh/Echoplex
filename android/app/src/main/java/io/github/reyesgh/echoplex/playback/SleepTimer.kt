package io.github.reyesgh.echoplex.playback

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * Temporizador de apagado, como en Echoplex del PC: la música se para a una hora (bajando el volumen los últimos
 * segundos) o al acabar la canción que suena. Lo cumple PlaybackService aunque la app esté cerrada.
 */
object SleepTimer {
    private val _endsAt = MutableStateFlow<Long?>(null)
    /** Cuándo se para (milisegundos del reloj del sistema), o null. */
    val endsAt: StateFlow<Long?> = _endsAt

    private val _endOfSong = MutableStateFlow(false)
    /** Se para al acabar la canción que suena. */
    val endOfSong: StateFlow<Boolean> = _endOfSong

    fun minutes(m: Int) {
        _endOfSong.value = false
        _endsAt.value = System.currentTimeMillis() + m * 60_000L
    }

    fun atEndOfSong() {
        _endsAt.value = null
        _endOfSong.value = true
    }

    fun cancel() {
        _endsAt.value = null
        _endOfSong.value = false
    }

    val active: Boolean get() = _endsAt.value != null || _endOfSong.value
}
