package io.github.reyesgh.echoplex

import android.app.Application
import android.net.ConnectivityManager
import android.net.Network
import io.github.reyesgh.echoplex.data.Downloads
import io.github.reyesgh.echoplex.data.ServerStore
import io.github.reyesgh.echoplex.data.Updater
import io.github.reyesgh.echoplex.net.Connection
import io.github.reyesgh.echoplex.net.DirectTransport
import io.github.reyesgh.echoplex.net.Subsonic
import io.github.reyesgh.echoplex.net.TailscaleNode
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

/** Lo compartido por toda la app: la conexión con el PC, el cliente de la API y las descargas. */
class EchoplexApp : Application() {
    lateinit var store: ServerStore
        private set
    lateinit var downloads: Downloads
        private set
    lateinit var updater: Updater
        private set

    /** El Tailscale de la app (para fuera de casa, sin la app de Tailscale). */
    val tailscale = TailscaleNode(this)

    /** La conexión: lo que va a Tailscale sale por el Tailscale de la app; lo demás, directo. */
    val connection = Connection(DirectTransport(tailscale.proxySelector)) { tailscale.proxyPort != null }
    val api by lazy { Subsonic(connection) }
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    /** Se completa cuando ya se sabe si hay un Echoplex guardado (lo esperan Android Auto y la cola guardada). */
    val ready = CompletableDeferred<Unit>()

    override fun onCreate() {
        super.onCreate()
        instance = this
        store = ServerStore(this)
        downloads = Downloads(this, this)
        updater = Updater(this)
        // ¿hay versión nueva en GitHub? (solo mira; se avisa en Inicio y se actualiza si se pulsa)
        scope.launch { kotlinx.coroutines.delay(5000); updater.check() }
        // la conexión guardada se carga aquí (no en la pantalla): así también la tiene Android Auto sin abrir la app
        scope.launch {
            if (store.tailscale.first()) tailscale.start()
            store.config.first()?.let { connection.use(it) } // empieza a buscar el PC (y sigue hasta dar con él)
            ready.complete(Unit)
        }
        // cada vez que el PC vuelve a responder: lo que quedara por descargar sigue
        scope.launch { connection.reachable.collect { if (it) downloads.resume() } }
        // al conectarse el Tailscale de la app, la dirección de fuera ya responde: se busca otra vez
        scope.launch {
            tailscale.state.collect { s -> if (s.state == "Running") connection.lost() }
        }
        // al cambiar de red (salir de casa, entrar en el wifi, quitar los datos…): Tailscale se reconecta por la red
        // nueva y se vuelve a buscar qué dirección del PC responde
        getSystemService(ConnectivityManager::class.java)?.registerDefaultNetworkCallback(object : ConnectivityManager.NetworkCallback() {
            private var current: Network? = null

            override fun onAvailable(network: Network) {
                val changed = current != null && current != network
                current = network
                scope.launch {
                    ready.await()
                    if (changed) tailscale.restart()
                    connection.lost()
                }
            }

            override fun onLost(network: Network) {
                scope.launch { ready.await(); connection.lost() }
            }
        })
    }

    companion object {
        lateinit var instance: EchoplexApp
            private set
    }
}
