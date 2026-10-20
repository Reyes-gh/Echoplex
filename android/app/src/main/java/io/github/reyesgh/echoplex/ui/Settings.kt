package io.github.reyesgh.echoplex.ui

import android.content.Intent
import android.net.Uri
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Computer
import androidx.compose.material.icons.rounded.Download
import androidx.compose.material.icons.rounded.ExpandMore
import androidx.compose.material.icons.rounded.Palette
import androidx.compose.material.icons.rounded.Public
import androidx.compose.material.icons.rounded.SystemUpdate
import androidx.compose.material.icons.rounded.VolumeUp
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.reyesgh.echoplex.EchoplexApp
import io.github.reyesgh.echoplex.data.Normalization
import io.github.reyesgh.echoplex.net.Connection
import kotlinx.coroutines.launch

/** Ajustes de la app: conexión, fuera de casa, sonido, descargas y temas (los mismos de Echoplex en el PC). */
@Composable
fun SettingsScreen(bottom: PaddingValues, onDisconnect: () -> Unit) {
    val app = EchoplexApp.instance
    val scope = rememberCoroutineScope()
    fun choose(key: String) {
        Ep.use(key)
        scope.launch { app.store.saveTheme(key) }
    }
    // Echoplex arriba, siempre a la vista; luego «Claros» y «Oscuros», plegados, y dentro sus familias, plegadas también
    // (como en Echoplex del PC). De primeras se abre solo el camino hasta el tema que llevas puesto.
    val groups = remember {
        listOf(false, true).map { dark ->
            (if (dark) "Oscuros" else "Claros") to ThemeFamilies.mapNotNull { family ->
                AllThemes.filter { it.group == family && it.isDark == dark }.takeIf { it.isNotEmpty() }?.let { family to it }
            }
        }
    }
    var open by rememberSaveable {
        val cur = AllThemes.firstOrNull { it.key == Ep.palette.key }
        mutableStateOf(
            if (cur == null || cur.group == MainFamily) emptyList()
            else (if (cur.isDark) "Oscuros" else "Claros").let { listOf(it, "$it/${cur.group}") }
        )
    }
    fun toggle(id: String) { open = if (id in open) open - id else open + id }

    LazyColumn(Modifier.fillMaxSize().statusBarsPadding(), contentPadding = bottom) {
        item { Text("Ajustes", color = Ep.Text, fontSize = 28.sp, fontWeight = FontWeight.Bold, modifier = Modifier.padding(start = 20.dp, top = 18.dp, bottom = 4.dp)) }
        item { ConnectionCard(onDisconnect) }
        item { TailscaleCard() }
        item { SoundCard() }
        item { DownloadsCard() }
        item { SettingsCard(Icons.Rounded.SystemUpdate, "Actualizaciones") { UpdateControls(inSettings = true) } }

        item { SectionLabel(Icons.Rounded.Palette, "Temas") }
        themeRows(AllThemes.filter { it.group == MainFamily }, ::choose)
        for ((group, families) in groups) {
            val count = families.sumOf { it.second.size }
            item(key = group) { FoldHeader(group, "$count temas", group in open, big = true) { toggle(group) } }
            if (group in open) for ((family, themes) in families) {
                val id = "$group/$family"
                item(key = id) { FoldHeader(family, "${themes.size}", id in open, big = false) { toggle(id) } }
                if (id in open) themeRows(themes, ::choose)
            }
        }
        item { Spacer(Modifier.height(20.dp)) }
    }
}

// ---------------------------------------------------------------- piezas comunes

/** El nombre de una sección, con su icono, encima de su tarjeta. */
@Composable
private fun SectionLabel(icon: ImageVector, text: String) {
    Row(Modifier.padding(start = 20.dp, end = 20.dp, top = 22.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, null, tint = Ep.Accent, modifier = Modifier.size(18.dp))
        Text(text.uppercase(), color = Ep.Accent, fontSize = 12.sp, fontWeight = FontWeight.Bold, letterSpacing = 1.2.sp,
            modifier = Modifier.padding(start = 8.dp))
    }
}

/** Una tarjeta de ajustes: fondo de superficie, esquinas redondeadas y aire dentro. */
@Composable
private fun SettingsCard(icon: ImageVector, title: String, content: @Composable ColumnScope.() -> Unit) {
    SectionLabel(icon, title)
    Column(
        Modifier.padding(horizontal = 16.dp).fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Ep.Surface).padding(16.dp),
        content = content,
    )
}

/** Un punto de color y una frase: cómo está algo (bien, esperando o mal). */
@Composable
private fun Status(color: Color, text: String) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(8.dp).clip(CircleShape).background(color))
        Text(text, color = Ep.Text2, fontSize = 13.sp, modifier = Modifier.padding(start = 8.dp))
    }
}

/** Botón grande de ancho completo (o la mitad, en una fila de dos). */
@Composable
private fun WideButton(text: String, modifier: Modifier = Modifier, primary: Boolean = false, danger: Boolean = false, enabled: Boolean = true, onClick: () -> Unit) {
    val bg = when {
        !enabled -> Ep.Raised.copy(alpha = 0.5f)
        primary -> Ep.Accent
        else -> Ep.Raised
    }
    val fg = when {
        !enabled -> Ep.Text3
        primary -> Ep.OnAccent
        danger -> Ep.Fav
        else -> Ep.Text
    }
    Box(
        modifier.height(46.dp).clip(RoundedCornerShape(12.dp)).background(bg).clickable(enabled = enabled, onClick = onClick).padding(horizontal = 12.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(text, color = fg, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

/** Opciones del mismo ancho, en filas de [perRow]: la elegida en el color de acento. */
@Composable
private fun Segments(options: List<Pair<String, String>>, selected: String, perRow: Int, onPick: (String) -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        options.chunked(perRow).forEach { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                row.forEach { (key, label) ->
                    val on = key == selected
                    Box(
                        Modifier.weight(1f).height(40.dp).clip(RoundedCornerShape(10.dp))
                            .background(if (on) Ep.Accent else Ep.Raised).clickable { onPick(key) },
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(label, color = if (on) Ep.OnAccent else Ep.Text, fontSize = 14.sp,
                            fontWeight = if (on) FontWeight.SemiBold else FontWeight.Normal, maxLines = 1)
                    }
                }
                repeat(perRow - row.size) { Spacer(Modifier.weight(1f)) }
            }
        }
    }
}

// ---------------------------------------------------------------- secciones

@Composable
private fun ConnectionCard(onDisconnect: () -> Unit) {
    val app = EchoplexApp.instance
    val away by app.connection.away.collectAsState()
    val reachable by app.connection.reachable.collectAsState()
    SettingsCard(Icons.Rounded.Computer, "Conexión") {
        Text(app.connection.config?.name ?: "Echoplex", color = Ep.Text, fontSize = 18.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(6.dp))
        when {
            !reachable -> Status(Ep.Text3, "Buscando tu Echoplex… (sigue intentándolo solo)")
            away -> Status(Ep.Accent, "Fuera de casa: las canciones llegan en MP3 para gastar menos datos")
            else -> Status(Ep.Accent, "En casa: las canciones llegan tal cual")
        }
        // las direcciones que conoce la app (la que está usando, marcada)
        val c = app.connection.config
        val base by app.connection.baseUrl.collectAsState()
        if (c != null) Column(Modifier.padding(top = 10.dp)) {
            val away2 = c.remoteUrl ?: Connection.tailscaleName(c)
            listOf("En casa" to c.localUrl, "Fuera de casa" to away2).forEach { (label, url) ->
                Row(Modifier.padding(top = 2.dp)) {
                    Text("$label: ", color = Ep.Text3, fontSize = 12.sp)
                    Text(url ?: "—", color = if (reachable && url == base) Ep.Accent else Ep.Text2, fontSize = 12.sp,
                        fontWeight = if (reachable && url == base) FontWeight.SemiBold else FontWeight.Normal, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
        }
        Spacer(Modifier.height(14.dp))
        WideButton("Cambiar de Echoplex (otro QR)", Modifier.fillMaxWidth(), onClick = onDisconnect)
    }
}

/**
 * Fuera de casa: el Tailscale de la app. Iniciar sesión abre el navegador con el enlace de Tailscale (misma cuenta que
 * en Echoplex del PC); al volver, la app ya llega al PC desde cualquier sitio.
 */
@Composable
private fun TailscaleCard() {
    SettingsCard(Icons.Rounded.Public, "Fuera de casa") { TailscaleControls() }
}

/** El estado del Tailscale de la app y sus botones (en Ajustes y en la pantalla de conectar). */
@Composable
fun TailscaleControls() = Column {
    val app = EchoplexApp.instance
    val ts = app.tailscale
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val s by ts.state.collectAsState()
    var openWhenReady by remember { mutableStateOf(false) }
    fun open(url: String) = runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) }
    // al pulsar «Iniciar sesión», el enlace se abre en cuanto llega
    LaunchedEffect(s.authUrl, openWhenReady) {
        val url = s.authUrl
        if (openWhenReady && s.state == "NeedsLogin" && url != null) { openWhenReady = false; open(url) }
    }
    fun login() {
        scope.launch { app.store.saveTailscale(true) }
        val url = s.authUrl
        if (s.state == "NeedsLogin" && url != null) open(url) else { openWhenReady = true; ts.login() }
    }
    fun turnOff() {
        scope.launch { app.store.saveTailscale(false) }
        ts.stop()
    }

    run {
        if (!ts.available) {
            Text("Este móvil no es de 64 bits: para escuchar fuera de casa usa la app de Tailscale.", color = Ep.Text2, fontSize = 14.sp)
            return@Column
        }
        when (s.state) {
            "Running" -> {
                Status(Ep.Accent, "Conectado a Tailscale")
                Spacer(Modifier.height(8.dp))
                s.user?.let { user ->
                    Text(
                        buildAnnotatedString {
                            append("Cuenta: ")
                            withStyle(SpanStyle(fontWeight = FontWeight.Bold, color = Ep.Text)) { append(user) }
                        },
                        color = Ep.Text2, fontSize = 15.sp,
                    )
                }
                s.dnsName?.substringBefore('.')?.let { Text("Este móvil: $it", color = Ep.Text3, fontSize = 13.sp, modifier = Modifier.padding(top = 2.dp)) }
                Text("Echoplex del PC te llega desde cualquier sitio.", color = Ep.Text2, fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp))
            }
            "Starting" -> Status(Ep.Text3, "Arrancando Tailscale…")
            "NeedsLogin" -> {
                Status(Ep.Text3, "Falta iniciar sesión")
                Text("Usa la misma cuenta que en Echoplex del PC. Se abre el navegador y luego vuelves aquí.",
                    color = Ep.Text2, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp))
            }
            "NeedsMachineAuth" -> Status(Ep.Fav, "Falta aprobar este móvil en la consola de Tailscale")
            "Error" -> Status(Ep.Fav, "No ha podido arrancar" + (s.error?.let { ": $it" } ?: ""))
            else -> Text(
                "Para escuchar fuera de casa, inicia sesión en Tailscale (gratis) con la misma cuenta que en Echoplex del PC. Va dentro de la app: no hace falta instalar nada más.",
                color = Ep.Text2, fontSize = 14.sp,
            )
        }
        Spacer(Modifier.height(14.dp))
        // dos botones a partes iguales (o uno a todo el ancho)
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            when (s.state) {
                "Running" -> {
                    WideButton("Cerrar sesión", Modifier.weight(1f)) { ts.logout() }
                    WideButton("Apagar", Modifier.weight(1f)) { turnOff() }
                }
                "Stopped" -> WideButton("Iniciar sesión en Tailscale", Modifier.weight(1f), primary = true) { login() }
                else -> {
                    WideButton("Iniciar sesión", Modifier.weight(1f), primary = true, enabled = s.state != "Starting") { login() }
                    WideButton("Apagar", Modifier.weight(1f)) { turnOff() }
                }
            }
        }
    }
}

/**
 * Actualizar la app desde GitHub: en Ajustes (con «Buscar actualizaciones») y como aviso en Inicio cuando hay versión
 * nueva. La primera vez Android pide permiso para que Echoplex instale apps; después, solo confirmar.
 */
@Composable
fun UpdateControls(inSettings: Boolean) = Column {
    val up = EchoplexApp.instance.updater
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val release = up.available
    val progress = up.progress
    if (inSettings) {
        Text("Versión ${up.currentVersion}", color = Ep.Text, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
        up.message?.let { Text(it, color = Ep.Text2, fontSize = 13.sp, modifier = Modifier.padding(top = 2.dp)) }
    } else if (release != null) {
        Text("Echoplex ${release.version} está disponible", color = Ep.Text, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
        Text(up.message?.takeIf { progress != null || it.startsWith("No se pudo") || it.startsWith("Confirma") } ?: "Se instala encima: no se pierde nada.",
            color = Ep.Text2, fontSize = 13.sp, modifier = Modifier.padding(top = 2.dp))
    }
    if (progress != null) {
        Box(Modifier.padding(top = 12.dp).fillMaxWidth().height(6.dp).clip(RoundedCornerShape(3.dp)).background(Ep.Raised)) {
            Box(Modifier.fillMaxWidth(progress.coerceIn(0f, 1f)).fillMaxHeight().background(Ep.Accent))
        }
        return@Column
    }
    if (!inSettings && release == null) return@Column
    Spacer(Modifier.height(12.dp))
    if (release != null) WideButton("Actualizar a la ${release.version}", Modifier.fillMaxWidth(), primary = true) {
        if (!up.canInstall()) {
            // la primera vez: Android tiene que dejar a Echoplex instalar apps (luego se vuelve y se pulsa otra vez)
            runCatching { context.startActivity(up.permissionIntent()) }
        } else scope.launch { up.install(release) }
    } else WideButton("Buscar actualizaciones", Modifier.fillMaxWidth()) { scope.launch { up.check() } }
}

@Composable
private fun SoundCard() {
    val app = EchoplexApp.instance
    val scope = rememberCoroutineScope()
    val norm by app.store.normalization.collectAsState(Normalization.OFF)
    SettingsCard(Icons.Rounded.VolumeUp, "Sonido") {
        Text("Normalizar volumen", color = Ep.Text, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
        Text(
            when (norm) {
                Normalization.TRACK -> "Todas las canciones suenan igual de fuerte."
                Normalization.ALBUM -> "Cada disco a su volumen, respetando las diferencias entre sus canciones."
                Normalization.SMART -> "Por álbum si escuchas un disco seguido; por canción si van mezcladas."
                else -> "Cada canción suena tal cual."
            },
            color = Ep.Text2, fontSize = 13.sp, modifier = Modifier.padding(top = 2.dp, bottom = 12.dp),
        )
        Segments(
            listOf(Normalization.OFF to "Apagado", Normalization.TRACK to "Por canción", Normalization.ALBUM to "Por álbum", Normalization.SMART to "Inteligente"),
            norm, perRow = 2,
        ) { scope.launch { app.store.saveNormalization(it) } }
    }
}

@Composable
private fun DownloadsCard() {
    val app = EchoplexApp.instance
    val scope = rememberCoroutineScope()
    val quality by app.store.downloadQuality.collectAsState("raw")
    val downloads = app.downloads
    val size = remember(downloads.version, downloads.index) { downloads.sizeBytes() }
    var confirm by remember { mutableStateOf(false) }
    SettingsCard(Icons.Rounded.Download, "Descargas") {
        Text("Calidad", color = Ep.Text, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
        Text(
            if (quality == "raw") "El archivo tal cual está en el PC (FLAC, MP3…): la mejor calidad, ocupa más."
            else "Convertidas a MP3 de $quality kbps: ocupan mucho menos.",
            color = Ep.Text2, fontSize = 13.sp, modifier = Modifier.padding(top = 2.dp, bottom = 12.dp),
        )
        Segments(listOf("raw" to "Original", "320" to "MP3 320", "192" to "MP3 192"), quality, perRow = 3) {
            scope.launch { app.store.saveDownloadQuality(it) }
        }
        val n = downloads.index.collections.size
        Row(Modifier.padding(top = 16.dp), verticalAlignment = Alignment.Bottom) {
            Text("%.1f MB".format(size / 1_048_576.0), color = Ep.Text, fontSize = 20.sp, fontWeight = FontWeight.Bold)
            Text("  en el móvil · $n ${if (n == 1) "descarga" else "descargas"}", color = Ep.Text2, fontSize = 13.sp, modifier = Modifier.padding(bottom = 2.dp))
        }
        if (n > 0) {
            Spacer(Modifier.height(12.dp))
            WideButton("Borrar todas las descargas", Modifier.fillMaxWidth(), danger = true) { confirm = true }
        }
    }
    if (confirm) AlertDialog(
        onDismissRequest = { confirm = false },
        containerColor = Ep.Surface,
        title = { Text("Borrar todas las descargas", color = Ep.Text) },
        text = { Text("Se borran del móvil. Todo sigue en el PC.", color = Ep.Text2) },
        confirmButton = { TextButton(onClick = { downloads.clear(); confirm = false }) { Text("Borrar", color = Ep.Fav) } },
        dismissButton = { TextButton(onClick = { confirm = false }) { Text("Cancelar", color = Ep.Text2) } },
    )
}

// ---------------------------------------------------------------- temas

/** Miniaturas de 3 en 3. */
private fun LazyListScope.themeRows(themes: List<ThemePalette>, choose: (String) -> Unit) {
    items(themes.chunked(3), key = { row -> "t:" + row.first().key }) { row ->
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            row.forEach { t -> ThemeTile(t, t.key == Ep.palette.key, Modifier.weight(1f)) { choose(t.key) } }
            repeat(3 - row.size) { Spacer(Modifier.weight(1f)) }
        }
        Spacer(Modifier.height(10.dp))
    }
}

/** Cabecera que pliega y despliega lo que tiene debajo (la flecha gira). */
@Composable
private fun FoldHeader(title: String, detail: String, open: Boolean, big: Boolean, onClick: () -> Unit) {
    val turn by animateFloatAsState(if (open) 180f else 0f, label = "flecha")
    Row(
        Modifier.fillMaxWidth().padding(start = if (big) 16.dp else 28.dp, end = 16.dp, top = 4.dp, bottom = 4.dp)
            .clip(RoundedCornerShape(12.dp)).background(if (big) Ep.Surface else Color.Transparent)
            .clickable(onClick = onClick).padding(horizontal = 14.dp, vertical = if (big) 14.dp else 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(title, color = Ep.Text, fontSize = if (big) 16.sp else 15.sp, fontWeight = if (big) FontWeight.SemiBold else FontWeight.Medium)
        Text(detail, color = Ep.Text3, fontSize = 13.sp, modifier = Modifier.padding(start = 8.dp).weight(1f))
        Icon(Icons.Rounded.ExpandMore, if (open) "Plegar" else "Desplegar", tint = Ep.Text2, modifier = Modifier.rotate(turn))
    }
}

/** Una miniatura del tema: su fondo, su barra, un texto y el color de acento. */
@Composable
private fun ThemeTile(t: ThemePalette, selected: Boolean, modifier: Modifier, onClick: () -> Unit) {
    Column(modifier.clip(RoundedCornerShape(10.dp)).clickable(onClick = onClick)) {
        Box(
            Modifier.fillMaxWidth().height(64.dp).clip(RoundedCornerShape(10.dp)).background(Color(t.bg))
                .border(if (selected) 2.dp else 1.dp, if (selected) Ep.Accent else Ep.Divider, RoundedCornerShape(10.dp)),
        ) {
            Box(Modifier.fillMaxHeight().width(18.dp).background(Color(t.surface)))
            Column(Modifier.padding(start = 26.dp, top = 10.dp)) {
                Text("Aa", color = Color(t.text), fontSize = 14.sp, fontWeight = FontWeight.Bold)
                Box(Modifier.padding(top = 6.dp).width(30.dp).height(4.dp).clip(RoundedCornerShape(2.dp)).background(Color(t.text3)))
            }
            Box(Modifier.align(Alignment.BottomEnd).padding(8.dp).size(14.dp).clip(CircleShape).background(Color(t.accent)))
        }
        Text(t.name, color = if (selected) Ep.Text else Ep.Text2, fontSize = 12.sp, fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal,
            maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 4.dp, start = 2.dp))
    }
}
