package io.github.reyesgh.echoplex.net

import android.content.Context
import android.net.ConnectivityManager
import android.os.Build
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.io.File
import java.io.IOException
import java.io.OutputStreamWriter
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Proxy
import java.net.ProxySelector
import java.net.SocketAddress
import java.net.URI

/** Cómo está el Tailscale de la app. */
data class TailscaleState(
    val state: String = "Stopped", // Starting, NeedsLogin, NeedsMachineAuth, Running, Stopped, Error
    val authUrl: String? = null,
    val ip: String? = null,
    val dnsName: String? = null,
    val user: String? = null,
    val error: String? = null,
)

/**
 * Tailscale dentro de la app, sin la app de Tailscale ni la VPN del sistema: un nodo propio (tsnet) que corre como
 * proceso aparte (libechoplexts.so, el mismo programa que el paquete fuera de casa del PC) y abre un proxy en
 * 127.0.0.1. Solo lo que va a una dirección de Tailscale (100.64.0.0/10, *.ts.net) pasa por él; lo de casa va directo.
 * Si la app se cierra, el proceso se cierra con ella.
 */
class TailscaleNode(private val context: Context) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val json = Json { ignoreUnknownKeys = true }
    private var process: Process? = null
    private var input: OutputStreamWriter? = null

    private val _state = MutableStateFlow(TailscaleState())
    val state: StateFlow<TailscaleState> = _state

    @Volatile
    var proxyPort: Int? = null
        private set

    private val binary: File get() = File(context.applicationInfo.nativeLibraryDir, "libechoplexts.so")

    /** Solo hay Tailscale en móviles de 64 bits (ARM): es donde va el programa. */
    val available: Boolean get() = binary.exists()

    val running: Boolean get() = process?.isAlive == true

    @Synchronized
    fun start() {
        if (running || !available) return
        val dir = File(context.filesDir, "tailscale").apply { mkdirs() }
        val host = "echoplex-" + Build.MODEL.lowercase().replace(Regex("[^a-z0-9-]"), "-").trim('-').take(40)
        // el DNS lo resuelve el propio programa con el del sistema (androidbin de Tailscale)
        val args = mutableListOf(binary.absolutePath, "-mode", "client", "-dir", dir.absolutePath, "-hostname", host, "-v")
        _state.value = TailscaleState("Starting")
        val p = try {
            ProcessBuilder(args).directory(dir).redirectErrorStream(false).start()
        } catch (e: IOException) {
            _state.value = TailscaleState("Error", error = e.message)
            return
        }
        process = p
        input = OutputStreamWriter(p.outputStream)
        // el registro de Tailscale va a un archivo (Android/data/…/files/tailscale.log, máx. ~1 MB) para poder ver qué
        // pasa si algo no conecta; lo último se enseña en Ajustes si se cierra
        var lastErr: String? = null
        val logFile = context.getExternalFilesDir(null)?.let { File(it, "tailscale.log") }
        scope.launch {
            val w = runCatching { logFile?.let { if (it.length() > 1_000_000) it.delete(); java.io.FileWriter(it, true) } }.getOrNull()
            p.errorStream.bufferedReader().forEachLine { line ->
                if (line.isNotBlank()) lastErr = line.take(200)
                runCatching { w?.apply { write(line); write("\n"); flush() } }
            }
            runCatching { w?.close() }
        }
        scope.launch {
            p.inputStream.bufferedReader().forEachLine { onLine(it) }
            // se acabó la salida: el proceso ha terminado
            proxyPort = null
            if (process == p) {
                process = null
                if (_state.value.state != "Stopped") {
                    val why = _state.value.error ?: lastErr
                    _state.value = TailscaleState("Error", error = "se ha cerrado" + (why?.let { " ($it)" } ?: ""))
                }
            }
        }
    }

    private fun onLine(line: String) {
        val o: JsonObject = runCatching { json.parseToJsonElement(line).jsonObject }.getOrNull() ?: return
        fun s(k: String) = o[k]?.jsonPrimitive?.contentOrNull?.takeIf { it.isNotEmpty() }
        when (s("type")) {
            "proxy" -> {
                proxyPort = o["port"]?.jsonPrimitive?.intOrNull
                android.util.Log.i("Echoplex", "tailscale: proxy en $proxyPort")
            }
            "state" -> _state.value = TailscaleState(s("state") ?: "Starting", s("authUrl"), s("ip"), s("dnsName"), s("user"))
            "error" -> _state.value = _state.value.copy(error = s("message"))
        }
    }

    private fun send(cmd: String) = scope.launch {
        runCatching { input?.apply { write(cmd + "\n"); flush() } }
    }

    fun login() { if (!running) start() else send("login") }
    fun logout() { send("logout") }

    @Synchronized
    fun stop() {
        val p = process ?: return
        process = null
        proxyPort = null
        _state.value = TailscaleState("Stopped")
        runCatching { input?.apply { write("quit\n"); flush(); close() } }
        scope.launch {
            Thread.sleep(2000)
            if (p.isAlive) p.destroy()
        }
    }

    /**
     * Al cambiar de red (wifi ↔ datos) se reinicia: así se reconecta en un par de segundos, en vez de esperar a notar
     * que la red de antes ya no está. La sesión se conserva (no hay que volver a iniciarla).
     */
    fun restart() {
        val p = process ?: return
        scope.launch {
            synchronized(this@TailscaleNode) {
                if (process != p) return@launch
                process = null
                proxyPort = null
                _state.value = TailscaleState("Starting")
                runCatching { input?.apply { write("quit\n"); flush(); close() } }
            }
            // que el anterior suelte su carpeta antes de arrancar el nuevo
            if (!p.waitFor(3, java.util.concurrent.TimeUnit.SECONDS)) { p.destroy(); p.waitFor(2, java.util.concurrent.TimeUnit.SECONDS) }
            start()
        }
    }

    /** Borra la sesión (para empezar de cero). */
    fun forget() {
        stop()
        File(context.filesDir, "tailscale").deleteRecursively()
    }

    /** Los DNS de la red que tenga el móvil ahora (Android no tiene /etc/resolv.conf para el programa). */
    private fun dnsServers(): List<String> = runCatching {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        cm.getLinkProperties(cm.activeNetwork)?.dnsServers?.mapNotNull { it.hostAddress } ?: emptyList()
    }.getOrDefault(emptyList()).ifEmpty { listOf("1.1.1.1", "8.8.8.8") }

    /** Para OkHttp: lo de Tailscale por el proxy (si está en marcha); lo demás, directo. */
    val proxySelector = object : ProxySelector() {
        override fun select(uri: URI?): List<Proxy> {
            val port = proxyPort
            val host = uri?.host ?: return listOf(Proxy.NO_PROXY)
            if (port == null || !isTailscaleHost(host)) return listOf(Proxy.NO_PROXY)
            // 127.0.0.1, que es donde escucha el proxy (getLoopbackAddress() da ::1 en Android, y ahí no hay nadie)
            return listOf(Proxy(Proxy.Type.HTTP, InetSocketAddress(LOOPBACK4, port)))
        }

        override fun connectFailed(uri: URI?, sa: SocketAddress?, ioe: IOException?) {}
    }

    companion object {
        private val LOOPBACK4: InetAddress = InetAddress.getByAddress(byteArrayOf(127, 0, 0, 1))

        /** 100.64.0.0/10 (las IP de Tailscale), un nombre de MagicDNS o el nombre corto de un Echoplex (echoplex-…). */
        fun isTailscaleHost(host: String): Boolean {
            if (host.endsWith(".ts.net")) return true
            if (host.startsWith("echoplex-") && !host.contains('.')) return true
            val parts = host.split('.')
            if (parts.size != 4) return false
            val a = parts[0].toIntOrNull() ?: return false
            val b = parts[1].toIntOrNull() ?: return false
            return a == 100 && b in 64..127
        }
    }
}
