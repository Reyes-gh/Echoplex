package io.github.reyesgh.echoplex.data

import android.content.Context
import android.net.Uri
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

/**
 * El Echoplex del PC al que se conecta la app: lo que lleva el QR de Ajustes → Móvil.
 * [localUrl] es la dirección en el wifi de casa; [remoteUrl], la de fuera (Tailscale), si la hay.
 */
data class ServerConfig(
    val name: String,
    val localUrl: String,
    val remoteUrl: String?,
    val user: String,
    val password: String,
) {
    companion object {
        /** echoplex://conectar?v=1&n=…&l=…&r=…&u=…&p=… → la configuración, o null si no es un QR de Echoplex. */
        fun fromQr(text: String): ServerConfig? {
            val uri = runCatching { Uri.parse(text.trim()) }.getOrNull() ?: return null
            if (uri.scheme != "echoplex" || uri.host != "conectar") return null
            val local = uri.getQueryParameter("l").orEmpty()
            val remote = uri.getQueryParameter("r").orEmpty()
            val user = uri.getQueryParameter("u").orEmpty()
            val pass = uri.getQueryParameter("p").orEmpty()
            if ((local.isBlank() && remote.isBlank()) || user.isBlank() || pass.isBlank()) return null
            return ServerConfig(
                name = uri.getQueryParameter("n").orEmpty().ifBlank { "Echoplex" },
                localUrl = local.ifBlank { remote }.trimEnd('/'),
                remoteUrl = remote.ifBlank { null }?.trimEnd('/'),
                user = user,
                password = pass,
            )
        }
    }
}

private val Context.store by preferencesDataStore("echoplex")

/** Guarda la conexión en el almacenamiento privado de la app (no lo ve ninguna otra app). */
class ServerStore(private val context: Context) {
    private val name = stringPreferencesKey("name")
    private val local = stringPreferencesKey("local")
    private val remote = stringPreferencesKey("remote")
    private val user = stringPreferencesKey("user")
    private val pass = stringPreferencesKey("pass")

    val config: Flow<ServerConfig?> = context.store.data.map { p ->
        val l = p[local] ?: return@map null
        ServerConfig(p[name] ?: "Echoplex", l, p[remote], p[user] ?: return@map null, p[pass] ?: return@map null)
    }

    suspend fun save(c: ServerConfig) = context.store.edit { p ->
        p[name] = c.name
        p[local] = c.localUrl
        if (c.remoteUrl != null) p[remote] = c.remoteUrl else p.remove(remote)
        p[user] = c.user
        p[pass] = c.password
    }

    /** Al desconectar se olvida el servidor, pero no el tema elegido. */
    suspend fun clear() = context.store.edit { p -> listOf(name, local, remote, user, pass).forEach { p.remove(it) } }

    private val themeKey = stringPreferencesKey("theme")
    val theme: Flow<String?> = context.store.data.map { it[themeKey] }
    suspend fun saveTheme(key: String) = context.store.edit { it[themeKey] = key }

    private val normKey = stringPreferencesKey("normalization")
    /** Normalizar volumen, como en Echoplex del PC: off (de serie), track, album o smart. */
    val normalization: Flow<String> = context.store.data.map { it[normKey] ?: Normalization.OFF }
    suspend fun saveNormalization(mode: String) = context.store.edit { it[normKey] = mode }

    private val tsKey = androidx.datastore.preferences.core.booleanPreferencesKey("tailscale")
    /** El Tailscale de la app encendido (arranca solo al abrirla). */
    val tailscale: Flow<Boolean> = context.store.data.map { it[tsKey] ?: false }
    suspend fun saveTailscale(on: Boolean) = context.store.edit { it[tsKey] = on }

    private val dlKey = stringPreferencesKey("downloadQuality")
    /** Calidad de las descargas: raw (el archivo tal cual, de serie), 320 o 192 (MP3). */
    val downloadQuality: Flow<String> = context.store.data.map { it[dlKey] ?: "raw" }
    suspend fun saveDownloadQuality(q: String) = context.store.edit { it[dlKey] = q }
}

object Normalization {
    const val OFF = "off"
    const val TRACK = "track"
    const val ALBUM = "album"
    const val SMART = "smart"
}
