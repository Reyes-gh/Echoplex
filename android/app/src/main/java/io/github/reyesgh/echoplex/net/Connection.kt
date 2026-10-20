package io.github.reyesgh.echoplex.net

import io.github.reyesgh.echoplex.data.ServerConfig
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.math.min

/**
 * Por dónde salen las peticiones de la app (la API y el audio). Todo pasa por aquí: directas por la red del móvil, y lo
 * que va a Tailscale, por el Tailscale de la app.
 */
interface Transport {
    val client: OkHttpClient
}

/**
 * Directas por la red del móvil; lo que va a Tailscale, por el Tailscale de la app ([proxies]). Si una petición falla
 * por la red, avisa ([onNetworkError]) para que se vuelva a buscar el PC.
 */
class DirectTransport(proxies: java.net.ProxySelector? = null) : Transport {
    @Volatile
    var onNetworkError: (() -> Unit)? = null

    override val client: OkHttpClient = OkHttpClient.Builder()
        .apply { if (proxies != null) proxySelector(proxies) }
        .addInterceptor(Interceptor { chain ->
            try {
                chain.proceed(chain.request())
            } catch (e: IOException) {
                if (!chain.call().isCanceled()) onNetworkError?.invoke() // cancelar (saltar en la canción…) no es un fallo
                throw e
            }
        })
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()
}

/**
 * A qué dirección del PC hablar: la de casa si responde (en el wifi de casa) y si no, la de fuera (Tailscale).
 *
 * Si no llega a ninguna (recién cambiado de red, Tailscale aún conectándose, PC apagado…) no se rinde: sigue buscando
 * sola cada poco (1 s, 2 s, 4 s… hasta 15 s) hasta dar con él. Vuelve a buscar al cambiar de red, cuando se conecta el
 * Tailscale de la app y cuando una petición falla.
 */
class Connection(val transport: Transport, private val tailscaleUp: () -> Boolean = { false }) {
    companion object {
        /**
         * El nombre del PC en Tailscale, el mismo que le pone Echoplex del PC (echoplex-<nombre del equipo>): con el
         * Tailscale de la app se llega a él aunque el QR no trajera la dirección de fuera.
         */
        fun tailscaleName(c: ServerConfig): String? {
            val name = c.name.lowercase().replace(Regex("[^a-z0-9-]"), "-").trim('-')
            if (name.isEmpty()) return null
            val port = runCatching { java.net.URI(c.localUrl).port }.getOrDefault(-1).takeIf { it > 0 } ?: 4040
            return "http://echoplex-$name:$port"
        }
    }

    private val _baseUrl = MutableStateFlow<String?>(null)
    val baseUrl: StateFlow<String?> = _baseUrl

    private val _away = MutableStateFlow(false)
    /** Conectado por la dirección de fuera de casa. */
    val away: StateFlow<Boolean> = _away

    private val _reachable = MutableStateFlow(false)
    /** El PC responde ahora por [baseUrl]. */
    val reachable: StateFlow<Boolean> = _reachable

    var config: ServerConfig? = null
        private set

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val mutex = Mutex()
    private var search: Job? = null

    private val probeClient by lazy {
        // sin el aviso de fallo: que la de casa no responda al probar es normal fuera de casa
        transport.client.newBuilder().apply { interceptors().clear() }
            .connectTimeout(2, TimeUnit.SECONDS).readTimeout(4, TimeUnit.SECONDS).build()
    }

    init {
        (transport as? DirectTransport)?.onNetworkError = { lost() }
    }

    fun use(config: ServerConfig?) {
        this.config = config
        _reachable.value = false
        if (config == null) {
            _baseUrl.value = null
            search?.cancel()
        } else {
            if (_baseUrl.value == null) _baseUrl.value = config.localUrl
            lost()
        }
    }

    /** Prueba ya la de casa y, si no responde, la de fuera. Devuelve la elegida (o null si ninguna responde). */
    suspend fun probe(): String? = withContext(Dispatchers.IO) {
        mutex.withLock {
            val c = config ?: return@withLock null
            // casa, la de fuera del QR y, con el Tailscale de la app en marcha, el PC por su nombre en Tailscale
            val byName = if (tailscaleUp()) tailscaleName(c) else null
            for (url in listOfNotNull(c.localUrl, c.remoteUrl, byName).distinct()) {
                val ok = responds(url, c)
                android.util.Log.i("Echoplex", "probe $url -> ${if (ok) "ok" else "no"} (tailscale=${tailscaleUp()})")
                if (ok) {
                    _baseUrl.value = url
                    _away.value = url != c.localUrl
                    _reachable.value = true
                    return@withLock url
                }
            }
            _reachable.value = false
            null
        }
    }

    /** Algo falló o cambió la red: se busca el PC otra vez, en segundo plano, hasta encontrarlo. */
    fun lost() {
        if (config == null) return
        _reachable.value = false
        if (search?.isActive == true) return
        search = scope.launch {
            var wait = 1000L
            while (isActive && config != null && !_reachable.value) {
                if (probe() != null) break
                delay(wait)
                wait = min(wait * 2, 15_000L)
            }
        }
    }

    /** Espera a que el PC responda (como mucho [timeoutMs]). */
    suspend fun awaitReachable(timeoutMs: Long): Boolean =
        withTimeoutOrNull(timeoutMs) { reachable.first { it } } != null

    private fun responds(url: String, c: ServerConfig): Boolean = runCatching {
        val request = Request.Builder().url(Subsonic.buildUrl(url, c, "ping", emptyMap())).build()
        probeClient.newCall(request).execute().use { it.isSuccessful }
    }.onFailure { android.util.Log.i("Echoplex", "probe $url: $it") }.getOrDefault(false)
}
