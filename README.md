<p align="center"><img src="Assets/echoplex.png" width="120" alt="Echoplex"></p>

<h1 align="center">Echoplex</h1>

<p align="center">Reproductor de música local para Windows con la estructura de Spotify y un estilo minimalista.</p>

## Características

- **Tu música, por carpetas**: árbol de carpetas en el panel lateral, con varias carpetas de música a la vez (cada una es un bloque propio) y miniaturas de portada.
- **Páginas de carpeta por discos**: al abrir una carpeta con varios discos, cada disco aparece como un módulo con su portada y su título.
- **Portadas mosaico**: una carpeta sin portada propia combina las de sus discos (2 o 3 en diagonal, 4 en cuadrícula).
- **Lo de Spotify que tiene sentido en local**: inicio con recientes y más escuchadas, favoritas, historial, añadidas recientemente, playlists, cola, radio automática, aleatorio y repetición, búsqueda, letras (`.lrc`), mini reproductor, temporizador de apagado y resumen de escucha.
- **Fiel al archivo**: modo *Bit a bit* con WASAPI en exclusivo (sin mezclador de Windows ni remuestreo) y salto exacto a la muestra.
- **OneDrive**: las canciones que solo están en la nube se descargan al reproducirlas y se puede elegir qué dejar en el dispositivo.
- **Temas**: Claro, Oscuro, TRUE dark, Neon night y 80's retro, con animaciones que se pueden desactivar.
- **Actualizaciones automáticas** desde las releases de este repositorio.
- **Controles multimedia de Windows** (teclas de medios, panel de volumen), atajos de teclado (F1) y zoom de la interfaz (`Ctrl +` / `Ctrl −`).

Todas las opciones están en **Ajustes** (engranaje abajo a la izquierda, menú *Echoplex* o `Ctrl+,`): tema, animaciones, carpetas de música y actualizaciones.

## Instalación

1. Instala el [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) si no lo tienes.
2. Descarga `Echoplex-<versión>-win-x64.zip` de [Releases](../../releases), descomprímelo y abre `Echoplex.exe`.
3. Windows SmartScreen puede avisar la primera vez porque el ejecutable no está firmado: **Más información → Ejecutar de todas formas**.
4. La primera vez usa tu carpeta **Música** de Windows; añade otras desde Ajustes o con el botón **+** junto a «Carpetas».

Los ajustes, playlists e historial se guardan en `%LocalAppData%\Echoplex`; la carpeta del programa no guarda nada tuyo.

**Actualizaciones automáticas**: al abrirse, Echoplex mira si hay una release nueva en este repositorio, la descarga y la instala en segundo plano; basta con reiniciar cuando lo avise. Se puede desactivar en Ajustes.

## Compilar

Requiere el SDK de .NET 8.

```powershell
dotnet build -c Release
.\publish.ps1          # release en app\Echoplex-<versión>\
.\publish.ps1 -Zip     # además, el .zip para GitHub Releases
```

## Publicar una versión

1. Sube `<Version>` en `Echoplex.csproj` (p. ej. `1.0.5`).
2. `.\publish.ps1 -Zip`
3. En GitHub, crea una release con la etiqueta **`v1.0.5`** y adjunta **`Echoplex-1.0.5-win-x64.zip`** (el nombre importa: es lo que busca el actualizador).

Las instalaciones existentes (desde la 1.0.1) la encontrarán la próxima vez que se abran.

## Versiones

### 1.0.8
- **Nombres de fichero por carpeta**: además del ajuste general, cada carpeta puede usar nombres de fichero o metadatos. Clic derecho en la carpeta del árbol, o el interruptor **Nombres fichero** junto a `…` en su página. Lo que se fija en una carpeta lo siguen todas sus subcarpetas; *Títulos: seguir el ajuste general* quita la excepción. Se guarda por ruta completa.
- **Logo de Echoplex** arriba a la izquierda (invertido en los temas oscuros).
- **Detalles** con el panel derecho estrecho: cada dato en una sola columna (etiqueta arriba, valor debajo).
- **Carátulas a mano**: las carpetas y álbumes sin carátula muestran un recuadro con «+» para elegir una imagen; clic derecho en la carátula o en la carpeta del árbol para cambiarla o quitarla. Se guardan en `%LocalAppData%\Echoplex\covers`, sin tocar las carpetas de música.

### 1.0.7
- **Títulos por nombre de archivo**: nueva opción en Ajustes para mostrar el nombre del archivo (sin extensión) en lugar del título de los metadatos y ordenar por él; los álbumes siguen entonces el orden de los archivos.
- **Portada en grande con un clic**: clic en la portada de la cabecera para verla en grande a la derecha; se queda en todas las páginas hasta otro clic.
- **Panel lateral**: *Inicio* arriba del todo, logo y nombre más grandes, y un menú fijo de iconos que aparece al bajar (al pasar el ratón muestra el nombre).
- **Interruptores** con nuevo diseño en Ajustes y en la cola.
- **Estructura** (Ajustes): elige qué columnas se ven en las listas de canciones (#, Título, Artista, Álbum, Tipo, Favorita, Duración).
- **Árbol de carpetas**: se ilumina el camino completo hasta la carpeta de la canción que suena (todas las carpetas padre), con el altavoz en la carpeta de la canción.

### 1.0.6
- **Columnas de la tabla de canciones**: arrastra el borde entre dos cabeceras para cambiar su ancho y arrastra una cabecera para cambiarla de sitio. Clic derecho en la cabecera → *Restablecer columnas*. Se recuerda entre sesiones.
- El **panel derecho** (cola, letra, detalles) se puede **ensanchar o estrechar** arrastrando su borde izquierdo (de 180 a 640 px); doble clic lo devuelve a su ancho. Se recuerda entre sesiones.
- **Pantallas pequeñas**: si los iconos de la barra de reproducción no caben, el temporizador, la letra, la cola y los detalles pasan a un menú con flecha (↑). *Bit a bit*, el mini reproductor y el volumen siguen siempre a la vista.

### 1.0.5
- **Vista previa de la portada**: al pasar el ratón por la portada de la cabecera (carpetas, álbumes, playlists…), se pone en blanco y negro, aparece una flecha y la portada se muestra en grande y a color a la derecha.
- Portadas de cabecera con más resolución.

### 1.0.4
- **Zoom** de toda la interfaz con `Ctrl +` / `Ctrl −` (o `Ctrl` + rueda del ratón), `Ctrl+0` para volver al 100 %. Se recuerda entre sesiones.
- Las **miniaturas del árbol de carpetas** siguen la misma regla de mosaico que las portadas (diagonal para 2 o 3 discos, cuadrícula para 4).
- Ventana de **Ajustes** más grande y con letra más grande.

### 1.0.3
- **Ajustes** también en el menú *Echoplex* de arriba a la izquierda, debajo de *Atajos de teclado*.

### 1.0.2
- **Portadas mosaico** para carpetas sin portada propia con varios discos: 2 o 3 carátulas en franjas diagonales separadas por una línea, 4 o más en cuadrícula 2×2. Las carátulas repetidas (Disc 1 / Disc 2) cuentan como una.

### 1.0.1
- **Actualizaciones automáticas**: al abrirse busca una versión nueva en las releases, la descarga (comprobando su huella SHA-256) y la instala en segundo plano; avisa para reiniciar. En Ajustes: versión actual, *Buscar ahora* y opción para desactivarlo.
- Si las carpetas de música no tienen canciones, aparece la pantalla para añadir una carpeta en vez de una biblioteca vacía.

### 1.0.0
- Primera versión pública.
