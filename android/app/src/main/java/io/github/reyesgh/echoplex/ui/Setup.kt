package io.github.reyesgh.echoplex.ui

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import io.github.reyesgh.echoplex.EchoplexApp
import io.github.reyesgh.echoplex.data.ServerConfig
import kotlinx.coroutines.launch
import java.util.concurrent.Executors

/** Primera vez: conectar con el Echoplex del PC escaneando el QR de Ajustes → Móvil (o a mano). */
@Composable
fun SetupScreen(initialQr: String?, onConnected: () -> Unit) {
    val context = LocalContext.current
    val app = EchoplexApp.instance
    val scope = rememberCoroutineScope()
    var scanning by remember { mutableStateOf(false) }
    var manual by remember { mutableStateOf(false) }
    var checking by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    // escaneado pero aún sin llegar al PC (p. ej. fuera de casa sin Tailscale): se sigue intentando solo
    var pending by remember { mutableStateOf<ServerConfig?>(null) }
    val reachable by app.connection.reachable.collectAsState()
    val ts by app.tailscale.state.collectAsState()

    fun connect(config: ServerConfig) {
        scanning = false
        checking = true
        error = null
        scope.launch {
            app.connection.use(config) // empieza a buscarlo (casa y Tailscale) y no para hasta dar con él
            if (app.connection.probe() != null) {
                app.store.save(config)
                onConnected()
            } else pending = config
            checking = false
        }
    }

    // en cuanto responda (por casa o por Tailscale, al iniciar sesión), dentro
    LaunchedEffect(pending, reachable) {
        val c = pending ?: return@LaunchedEffect
        if (reachable && app.connection.config == c) {
            app.store.save(c)
            onConnected()
        }
    }

    fun onCode(text: String) {
        val config = ServerConfig.fromQr(text)
        if (config == null) {
            scanning = false
            error = "Ese QR no es el de Echoplex. Ábrelo en el PC: Ajustes → Móvil → «Mostrar QR para la app Echoplex»."
        } else connect(config)
    }

    // abierta desde un QR escaneado con otra app de cámara
    remember(initialQr) { if (initialQr != null) onCode(initialQr); 0 }

    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) scanning = true else error = "Sin permiso de cámara no se puede leer el QR. Puedes escribir los datos a mano."
    }

    Column(
        Modifier.fillMaxSize().background(Ep.Bg).statusBarsPadding().verticalScroll(rememberScrollState()).padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Spacer(Modifier.height(24.dp))
        Text("Conecta con tu Echoplex", color = Ep.Text, fontSize = 28.sp, fontWeight = FontWeight.Bold)
        Text(
            "En el PC, abre Echoplex → Ajustes → Móvil, activa «Dejar que el móvil escuche tu biblioteca» y pulsa «Mostrar QR para la app Echoplex». Luego escanéalo aquí.",
            color = Ep.Text2, fontSize = 15.sp,
        )
        val waiting = pending
        if (waiting != null) {
            // escaneado, pero el PC aún no responde: lo más normal es estar fuera de casa sin Tailscale todavía
            Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Ep.Surface).padding(16.dp)) {
                Text("QR de «${waiting.name}» leído", color = Ep.Text, fontSize = 17.sp, fontWeight = FontWeight.Bold)
                Spacer(Modifier.height(6.dp))
                Text(
                    if (ts.state == "Running") "Buscando tu Echoplex por Tailscale… Mira que el PC esté encendido, con Echoplex abierto y su Tailscale conectado (Ajustes → Móvil)."
                    else "Si estás fuera de casa, inicia sesión en Tailscale con la misma cuenta que en el PC: en cuanto conecte, entras solo.",
                    color = Ep.Text2, fontSize = 14.sp,
                )
                Spacer(Modifier.height(14.dp))
                TailscaleControls()
            }
            TextButton(onClick = { pending = null; app.connection.use(null) }) { Text("Escanear otro QR", color = Ep.Text2) }
        } else if (scanning) {
            Box(Modifier.fillMaxWidth().aspectRatio(1f).clip(RoundedCornerShape(14.dp))) { QrScanner(::onCode) }
            TextButton(onClick = { scanning = false }) { Text("Cancelar", color = Ep.Text2) }
        } else if (!manual) {
            Button(
                onClick = {
                    error = null
                    if (ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) scanning = true
                    else permission.launch(Manifest.permission.CAMERA)
                },
                enabled = !checking,
                colors = ButtonDefaults.buttonColors(containerColor = Ep.Accent),
                modifier = Modifier.fillMaxWidth().height(52.dp),
            ) { Text(if (checking) "Conectando…" else "Escanear QR", fontSize = 16.sp) }
            TextButton(onClick = { manual = true; error = null }) { Text("Escribir los datos a mano", color = Ep.Text2) }
            // fuera de casa también se puede empezar por aquí: con Tailscale ya conectado, el QR entra a la primera
            Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Ep.Surface).padding(16.dp)) {
                Text("¿Estás fuera de casa?", color = Ep.Text, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
                Text("Inicia sesión en Tailscale (la misma cuenta que en Echoplex del PC) y luego escanea el QR.",
                    color = Ep.Text2, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp, bottom = 12.dp))
                TailscaleControls()
            }
        }
        if (manual) ManualForm(checking, ::connect) { manual = false }
        error?.let { Text(it, color = Ep.Fav, fontSize = 14.sp) }
    }
}

@Composable
private fun ManualForm(checking: Boolean, onConnect: (ServerConfig) -> Unit, onCancel: () -> Unit) {
    var url by remember { mutableStateOf("http://") }
    var away by remember { mutableStateOf("") }
    var user by remember { mutableStateOf("echoplex") }
    var pass by remember { mutableStateOf("") }
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        OutlinedTextField(url, { url = it }, label = { Text("Dirección en casa (http://…:4040)") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
        OutlinedTextField(away, { away = it }, label = { Text("Fuera de casa (opcional)") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri))
        OutlinedTextField(user, { user = it }, label = { Text("Usuario") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(pass, { pass = it }, label = { Text("Contraseña") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
            visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password))
        Button(
            onClick = { onConnect(ServerConfig("Echoplex", url.trim().trimEnd('/'), away.trim().trimEnd('/').ifBlank { null }, user.trim(), pass)) },
            enabled = !checking && url.length > 8 && user.isNotBlank() && pass.isNotBlank(),
            colors = ButtonDefaults.buttonColors(containerColor = Ep.Accent),
            modifier = Modifier.fillMaxWidth().height(50.dp),
        ) { Text(if (checking) "Conectando…" else "Conectar") }
        TextButton(onClick = onCancel) { Text("Mejor con el QR", color = Ep.Text2) }
    }
}

/** La cámara trasera buscando un QR (ZXing, sin servicios de Google). */
@Composable
fun QrScanner(onCode: (String) -> Unit) {
    val owner = LocalLifecycleOwner.current
    val context = LocalContext.current
    val executor = remember { Executors.newSingleThreadExecutor() }
    DisposableEffect(Unit) {
        onDispose {
            runCatching { ProcessCameraProvider.getInstance(context).get().unbindAll() }
            executor.shutdown()
        }
    }
    AndroidView(modifier = Modifier.fillMaxSize(), factory = { ctx ->
        PreviewView(ctx).also { view ->
            val future = ProcessCameraProvider.getInstance(ctx)
            future.addListener({
                val provider = future.get()
                val preview = Preview.Builder().build().also { it.setSurfaceProvider(view.surfaceProvider) }
                val reader = MultiFormatReader().apply { setHints(mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE))) }
                var done = false
                val analysis = ImageAnalysis.Builder().setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).build()
                analysis.setAnalyzer(executor) { image ->
                    try {
                        if (!done) {
                            val plane = image.planes[0]
                            val buffer = plane.buffer
                            val bytes = ByteArray(buffer.remaining()).also { buffer.get(it) }
                            val source = PlanarYUVLuminanceSource(bytes, plane.rowStride, image.height, 0, 0, image.width, image.height, false)
                            val result = runCatching { reader.decodeWithState(BinaryBitmap(HybridBinarizer(source))) }.getOrNull()
                            reader.reset()
                            if (result != null) {
                                done = true
                                ContextCompat.getMainExecutor(ctx).execute { onCode(result.text) }
                            }
                        }
                    } finally {
                        image.close()
                    }
                }
                provider.unbindAll()
                provider.bindToLifecycle(owner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis)
            }, ContextCompat.getMainExecutor(ctx))
        }
    })
}
