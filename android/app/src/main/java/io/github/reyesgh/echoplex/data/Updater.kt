package io.github.reyesgh.echoplex.data

import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import android.widget.Toast
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import io.github.reyesgh.echoplex.EchoplexApp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.longOrNull
import okhttp3.Request
import java.io.File
import java.security.MessageDigest

/** Una versión publicada en GitHub: su número, el APK, su huella SHA-256 y su tamaño. */
data class AppRelease(val version: String, val apkUrl: String, val sha256: String?, val size: Long)

/**
 * Actualizaciones de la app desde las releases de GitHub, como hace Echoplex del PC: mira la última release, y si es más
 * nueva, descarga Echoplex-<versión>-android.apk, comprueba su huella SHA-256 con la que publica GitHub y se la da a
 * Android para instalarla (pide confirmación; la primera vez, además, permiso para instalar apps desde Echoplex). Como va
 * firmada con la misma clave, se instala encima y lo conserva todo. Nunca se instala nada sin preguntar.
 */
class Updater(private val context: Context) {
    companion object {
        const val REPOSITORY = "Reyes-gh/echoplex"
        const val ACTION_INSTALLED = "io.github.reyesgh.echoplex.INSTALL_STATUS"
    }

    /** La versión nueva disponible (null si no hay o no se ha mirado). */
    var available by mutableStateOf<AppRelease?>(null)
        private set

    /** Descargando: de 0 a 1 (null si no). */
    var progress by mutableStateOf<Float?>(null)
        private set

    /** Lo último que pasó, para enseñarlo en Ajustes. */
    var message by mutableStateOf<String?>(null)
        private set

    val currentVersion: String = runCatching {
        context.packageManager.getPackageInfo(context.packageName, 0).versionName
    }.getOrNull() ?: "0"

    private val json = Json { ignoreUnknownKeys = true }
    private val client get() = EchoplexApp.instance.connection.transport.client

    /** Mira si hay una versión más nueva (no descarga nada). */
    suspend fun check(): AppRelease? = withContext(Dispatchers.IO) {
        runCatching {
            val req = Request.Builder().url("https://api.github.com/repos/$REPOSITORY/releases/latest")
                .header("Accept", "application/vnd.github+json").header("User-Agent", "Echoplex-Android/$currentVersion").build()
            client.newCall(req).execute().use { r ->
                if (r.code == 404) { message = "Aún no hay versiones publicadas"; return@runCatching null }
                if (!r.isSuccessful) error("GitHub respondió ${r.code}")
                val root = json.parseToJsonElement(r.body!!.string()).jsonObject
                val tag = root["tag_name"]?.jsonPrimitive?.contentOrNull?.trimStart('v', 'V') ?: error("release sin versión")
                if (!newer(tag, currentVersion)) {
                    message = "Tienes la última versión ($currentVersion)"
                    available = null
                    return@runCatching null
                }
                val asset = root["assets"]?.jsonArray?.map { it.jsonObject }
                    ?.firstOrNull { it["name"]?.jsonPrimitive?.contentOrNull?.endsWith("-android.apk") == true }
                    ?: run { message = "La versión $tag aún no incluye la app de Android"; return@runCatching null }
                val release = AppRelease(
                    tag,
                    asset["browser_download_url"]!!.jsonPrimitive.content,
                    asset["digest"]?.jsonPrimitive?.contentOrNull?.takeIf { it.startsWith("sha256:") }?.substring(7),
                    asset["size"]?.jsonPrimitive?.longOrNull ?: 0L,
                )
                message = "Echoplex ${release.version} está disponible"
                available = release
                release
            }
        }.getOrElse { message = "No se pudo consultar GitHub: ${it.message}"; null }
    }

    /** 2.0.1 > 2.0.0, 2.1 > 2.0.9 (por números, no por texto). */
    private fun newer(a: String, b: String): Boolean {
        val x = a.split('.').map { it.takeWhile(Char::isDigit).toIntOrNull() ?: 0 }
        val y = b.split('.').map { it.takeWhile(Char::isDigit).toIntOrNull() ?: 0 }
        for (i in 0 until maxOf(x.size, y.size)) {
            val d = x.getOrElse(i) { 0 } - y.getOrElse(i) { 0 }
            if (d != 0) return d > 0
        }
        return false
    }

    /** Si Android aún no deja a Echoplex instalar apps, hay que darle permiso (una vez) en Ajustes del sistema. */
    fun canInstall(): Boolean = Build.VERSION.SDK_INT < 26 || context.packageManager.canRequestPackageInstalls()

    fun permissionIntent(): Intent =
        Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, android.net.Uri.parse("package:${context.packageName}"))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

    /** Descarga, comprueba e instala la versión disponible (Android pide confirmar). */
    suspend fun install(release: AppRelease) {
        progress = 0f
        message = "Descargando Echoplex ${release.version}…"
        val error = withContext(Dispatchers.IO) {
            runCatching {
                val dir = File(context.cacheDir, "actualizaciones").apply { deleteRecursively(); mkdirs() }
                val apk = File(dir, "Echoplex-${release.version}.apk")
                val req = Request.Builder().url(release.apkUrl).header("User-Agent", "Echoplex-Android/$currentVersion").build()
                val digest = MessageDigest.getInstance("SHA-256")
                client.newCall(req).execute().use { r ->
                    if (!r.isSuccessful) error("GitHub respondió ${r.code}")
                    val total = r.body!!.contentLength().takeIf { it > 0 } ?: release.size
                    r.body!!.byteStream().use { input ->
                        apk.outputStream().use { out ->
                            val buf = ByteArray(64 * 1024)
                            var done = 0L
                            while (true) {
                                val n = input.read(buf)
                                if (n < 0) break
                                out.write(buf, 0, n)
                                digest.update(buf, 0, n)
                                done += n
                                if (total > 0) progress = done.toFloat() / total
                            }
                        }
                    }
                }
                val hash = digest.digest().joinToString("") { "%02x".format(it) }
                if (release.sha256 != null && !hash.equals(release.sha256, ignoreCase = true)) {
                    apk.delete()
                    error("la descarga no coincide con la huella publicada en GitHub; no se ha instalado")
                }
                // a Android: una sesión de instalación con el APK; él pide la confirmación
                val installer = context.packageManager.packageInstaller
                val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
                    .apply { setAppPackageName(context.packageName) }
                val id = installer.createSession(params)
                installer.openSession(id).use { session ->
                    session.openWrite("echoplex.apk", 0, apk.length()).use { out -> apk.inputStream().use { it.copyTo(out) }; session.fsync(out) }
                    val flags = PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
                    val pi = PendingIntent.getBroadcast(context, id, Intent(context, InstallReceiver::class.java).setAction(ACTION_INSTALLED), flags)
                    session.commit(pi.intentSender)
                }
                null
            }.exceptionOrNull()
        }
        progress = null
        message = if (error == null) "Confirma la instalación en el aviso de Android" else "No se pudo actualizar: ${error.message}"
    }
}

/** Lo que dice Android de la instalación: si hace falta que el usuario confirme, le enseña el aviso. */
class InstallReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                val confirm = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT) ?: return
                context.startActivity(confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
            PackageInstaller.STATUS_SUCCESS -> {}
            PackageInstaller.STATUS_FAILURE_ABORTED -> Toast.makeText(context, "Actualización cancelada", Toast.LENGTH_SHORT).show()
            else -> Toast.makeText(context, "No se pudo instalar: " + (intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: "error"), Toast.LENGTH_LONG).show()
        }
    }
}
