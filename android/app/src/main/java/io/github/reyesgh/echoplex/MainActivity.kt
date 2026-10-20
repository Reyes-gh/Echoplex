package io.github.reyesgh.echoplex

import android.Manifest
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.draw.clip
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Home
import androidx.compose.material.icons.outlined.LibraryMusic
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material.icons.rounded.Home
import androidx.compose.material.icons.rounded.LibraryMusic
import androidx.compose.material.icons.rounded.Search
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import io.github.reyesgh.echoplex.ui.AlbumScreen
import io.github.reyesgh.echoplex.ui.ArtistScreen
import io.github.reyesgh.echoplex.ui.EchoplexTheme
import io.github.reyesgh.echoplex.ui.Ep
import io.github.reyesgh.echoplex.ui.FavoritesScreen
import io.github.reyesgh.echoplex.ui.FolderScreen
import io.github.reyesgh.echoplex.ui.FullPlayer
import io.github.reyesgh.echoplex.ui.GenreScreen
import io.github.reyesgh.echoplex.ui.HomeScreen
import io.github.reyesgh.echoplex.ui.LibraryScreen
import io.github.reyesgh.echoplex.ui.MiniPlayer
import io.github.reyesgh.echoplex.ui.OfflineScreen
import io.github.reyesgh.echoplex.ui.PlayerUi
import io.github.reyesgh.echoplex.ui.PlaylistScreen
import io.github.reyesgh.echoplex.ui.SearchScreen
import io.github.reyesgh.echoplex.ui.SettingsScreen
import io.github.reyesgh.echoplex.ui.SetupScreen
import io.github.reyesgh.echoplex.ui.TickWhilePlaying
import io.github.reyesgh.echoplex.ui.rememberImageLoader
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private lateinit var player: PlayerUi

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        player = PlayerUi(this)
        // el tema guardado, antes del primer fotograma (para no ver un destello del oscuro)
        Ep.use(runBlocking { EchoplexApp.instance.store.theme.first() })
        val qr = intent?.data?.takeIf { it.scheme == "echoplex" }?.toString()
        setContent { EchoplexTheme { Root(player, qr) } }
    }

    override fun onDestroy() {
        player.release()
        super.onDestroy()
    }
}

@Composable
private fun Root(player: PlayerUi, initialQr: String?) {
    val app = EchoplexApp.instance
    val scope = rememberCoroutineScope()
    // null = mirando si ya hay un Echoplex guardado; false = hay que conectar; true = a la app
    var connected by remember { mutableStateOf<Boolean?>(null) }
    LaunchedEffect(Unit) {
        app.ready.await() // la conexión guardada ya la ha cargado EchoplexApp
        connected = app.connection.config != null && initialQr == null
    }
    Box(Modifier.fillMaxSize().background(Ep.Bg)) {
        when (connected) {
            null -> {}
            false -> SetupScreen(initialQr) { connected = true }
            true -> Main(player) {
                scope.launch {
                    player.controller?.stop()
                    player.controller?.clearMediaItems()
                    app.store.clear()
                    app.connection.use(null)
                    connected = false
                }
            }
        }
    }
}

private data class Tab(val route: String, val label: String, val on: ImageVector, val off: ImageVector)

private val tabs = listOf(
    Tab("home", "Inicio", Icons.Rounded.Home, Icons.Outlined.Home),
    Tab("search", "Buscar", Icons.Rounded.Search, Icons.Outlined.Search),
    Tab("library", "Biblioteca", Icons.Rounded.LibraryMusic, Icons.Outlined.LibraryMusic),
    Tab("settings", "Ajustes", Icons.Rounded.Settings, Icons.Outlined.Settings),
)

@Composable
private fun Main(player: PlayerUi, onDisconnect: () -> Unit) {
    val nav = rememberNavController()
    val loader = rememberImageLoader()
    var fullPlayer by remember { mutableStateOf(false) }
    TickWhilePlaying(player)
    // la pestaña en la que estás (un álbum abierto desde Inicio sigue siendo Inicio)
    var tab by rememberSaveable { mutableStateOf("home") }
    val homeTop = remember { MutableSharedFlow<Unit>(extraBufferCapacity = 1) }

    // Android 13+: permiso para la notificación de lo que suena
    val notifications = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
    LaunchedEffect(Unit) { if (Build.VERSION.SDK_INT >= 33) notifications.launch(Manifest.permission.POST_NOTIFICATIONS) }
    // seguir donde lo dejaste (aquí o en el PC), en cuanto el reproductor esté listo y se sepa por dónde hablar con el PC
    LaunchedEffect(player.controller) {
        if (player.controller == null) return@LaunchedEffect
        EchoplexApp.instance.connection.awaitReachable(30_000)
        player.restore(EchoplexApp.instance.api)
    }

    val entry by nav.currentBackStackEntryAsState()
    val route = entry?.destination?.route
    LaunchedEffect(route) { tabs.firstOrNull { it.route == route }?.let { tab = it.route } }

    fun open(t: Tab) {
        when {
            // Inicio siempre lleva a Inicio; si ya estás allí, sube arriba del todo
            t.route == "home" && route == "home" -> homeTop.tryEmit(Unit)
            t.route == "home" -> nav.popBackStack("home", inclusive = false)
            route == t.route -> {}
            else -> nav.navigate(t.route) {
                popUpTo(nav.graph.findStartDestination().id) { saveState = true }
                launchSingleTop = true
                restoreState = true
            }
        }
        tab = t.route
    }

    Scaffold(
        containerColor = Ep.Bg,
        bottomBar = {
            Column {
                MiniPlayer(player, loader) { fullPlayer = true }
                Row(Modifier.fillMaxWidth().background(Ep.Surface).navigationBarsPadding().height(60.dp)) {
                    tabs.forEach { t -> TabButton(t, tab == t.route, Modifier.weight(1f)) { open(t) } }
                }
            }
        },
    ) { padding ->
        val bottom = PaddingValues(bottom = padding.calculateBottomPadding())
        NavHost(nav, startDestination = "home") {
            composable("home") { HomeScreen(nav, player, loader, bottom, homeTop) }
            composable("settings") { SettingsScreen(bottom, onDisconnect) }
            composable("search") { SearchScreen(nav, player, loader, bottom) }
            composable("library") { LibraryScreen(nav, loader, bottom) }
            composable("favorites") { FavoritesScreen(nav, player, loader, bottom) }
            composable("offline/{id}") { OfflineScreen(Uri.decode(it.arguments?.getString("id") ?: ""), nav, player, loader, bottom) }
            composable("album/{id}") { AlbumScreen(it.arguments?.getString("id") ?: "", nav, player, loader, bottom) }
            composable("artist/{id}") { ArtistScreen(it.arguments?.getString("id") ?: "", nav, player, loader, bottom) }
            composable("playlist/{id}") { PlaylistScreen(it.arguments?.getString("id") ?: "", nav, player, loader, bottom) }
            composable("folder/{id}") { FolderScreen(it.arguments?.getString("id") ?: "", nav, player, loader, bottom) }
            composable("genre/{name}") { GenreScreen(Uri.decode(it.arguments?.getString("name") ?: ""), nav, player, loader, bottom) }
        }
    }

    AnimatedVisibility(fullPlayer, enter = slideInVertically { it }, exit = slideOutVertically { it }) {
        BackHandler { fullPlayer = false }
        FullPlayer(player, loader) { fullPlayer = false }
    }
}

/** Un botón de la barra de abajo: icono relleno y en color de acento si es la pestaña actual, de línea si no. */
@Composable
private fun TabButton(t: Tab, selected: Boolean, modifier: Modifier, onClick: () -> Unit) {
    val color = if (selected) Ep.Accent else Ep.Text3
    // el toque se ilumina dentro de una pastilla redondeada que cabe entera en la barra (sin canción sonando no hay nada encima)
    Column(
        modifier.fillMaxHeight().padding(horizontal = 6.dp, vertical = 5.dp).clip(RoundedCornerShape(16.dp)).clickable(onClick = onClick),
        horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center,
    ) {
        Icon(if (selected) t.on else t.off, null, tint = color, modifier = Modifier.size(24.dp))
        Text(t.label, color = if (selected) Ep.Text else Ep.Text3, fontSize = 11.sp,
            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal, modifier = Modifier.padding(top = 3.dp))
    }
}
