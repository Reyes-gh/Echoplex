<p align="center"><img src="Assets/echoplex.png" width="120" alt="Echoplex"></p>

<h1 align="center">Echoplex</h1>

<p align="center">Reproductor de música local para Windows con la estructura de Spotify y un estilo minimalista.</p>

## Características

- **Tu música, por carpetas**: árbol de carpetas en el panel lateral, con varias carpetas de música a la vez (cada una es un bloque propio) y miniaturas de portada.
- **Páginas de carpeta por discos**: al abrir una carpeta con varios discos, cada disco aparece como un módulo con su portada y su título.
- **Portadas mosaico**: una carpeta sin portada propia combina las de sus discos (2 o 3 en diagonal, 4 en cuadrícula), o las incrustadas en sus canciones si son de álbumes distintos.
- **Lo de Spotify que tiene sentido en local**: inicio con recientes y más escuchadas, favoritas, historial, añadidas recientemente, playlists, cola, radio automática, aleatorio y repetición, búsqueda, letras (`.lrc`, incrustadas o, si lo activas, de LRCLIB), mini reproductor, temporizador de apagado y resumen de escucha.
- **Pantalla completa**: la canción con su portada y controles a un lado y la letra pasando sola al otro, sobre la portada difuminada con luces de sus colores; ocupando toda la app o toda la pantalla (`F11`).
- **Fiel al archivo**: modo *Bit a bit* con WASAPI en exclusivo (sin mezclador de Windows ni remuestreo) y salto exacto a la muestra.
- **OneDrive**: las canciones que solo están en la nube se descargan al reproducirlas y se puede elegir qué dejar en el dispositivo.
- **Temas**: Claro, Oscuro, TRUE dark, Neon night y 80's retro, y 50 más en *Más temas* (Dracula, Nord, Solarized, Gruvbox, Catppuccin, Game Boy, MS-DOS, Matrix…), con animaciones que se pueden desactivar.
- **Aviso de actualizaciones** desde las releases de este repositorio: tú decides si actualizar, omitir esa versión o dejarlo para luego.
- **Controles multimedia de Windows** (teclas de medios, panel de volumen), atajos de teclado (F1) y zoom de la interfaz (`Ctrl +` / `Ctrl −`).

Todas las opciones están en **Ajustes** (engranaje abajo a la izquierda, menú *Echoplex* o `Ctrl+,`): tema, animaciones, carpetas de música y actualizaciones.

## Instalación

1. Instala el [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) si no lo tienes.
2. Descarga `Echoplex-<versión>-win-x64.zip` de [Releases](../../releases), descomprímelo y abre `Echoplex.exe`.
3. Windows SmartScreen puede avisar la primera vez porque el ejecutable no está firmado: **Más información → Ejecutar de todas formas**.
4. La primera vez usa tu carpeta **Música** de Windows; añade otras desde Ajustes o con el botón **+** junto a «Carpetas».

Los ajustes, playlists e historial se guardan en `%LocalAppData%\Echoplex`; la carpeta del programa no guarda nada tuyo.

**Actualizaciones**: al abrirse, Echoplex mira si hay una release nueva en este repositorio y, si la hay, avisa en la barra lateral: *Actualizar* (la descarga, comprueba su huella SHA-256, la instala y reinicia), *Omitir esta versión* o *Ahora no*. Nunca se instala nada sin preguntar. La comprobación se puede desactivar en Ajustes.

## Compilar

Requiere el SDK de .NET 8.

```powershell
dotnet build -c Release
.\publish.ps1          # release en app\Echoplex-<versión>\
.\publish.ps1 -Zip     # además, el .zip para GitHub Releases
```

## Publicar una versión

1. Sube `<Version>` en `Echoplex.csproj` (p. ej. `1.1.12`).
2. Escribe las notas en `.github/notas/1.1.12.md` (si no, se usa la sección `### 1.1.12` de este README).
3. Haz push a `main`. GitHub Actions ve que esa versión no tiene release, compila con `publish.ps1 -Zip` y publica la release **`v1.1.12`** con **`Echoplex-1.1.12-win-x64.zip`** (el nombre que busca el actualizador). Los push que no cambian la versión no publican nada.

Si hiciera falta a mano: `.\publish.ps1 -Zip` y crear la release con esa etiqueta y ese zip.

Las instalaciones existentes (desde la 1.0.1) la encontrarán la próxima vez que se abran.

## Versiones

### 1.1.11
- **Ajustes más claros**: cada sección (Tema, Carpetas de música, Títulos de las canciones, Letras, Actualizaciones, Otros y Estructura) tiene un título grande con su icono en el color de acento y una línea que la separa de la anterior.

### 1.1.10
- **Rendimiento** (todo se ve igual): transiciones entre páginas y redimensionado de la ventana mucho más ligeros. Los desenfoques de las transiciones se calculan a media resolución mientras son fuertes; la cabecera difuminada de las páginas y el destello de la portada se desenfocan una sola vez en lugar de en cada fotograma; el fondo animado se detiene mientras arrastras el borde de la ventana y va a 20 fotogramas por segundo; en el inicio solo se crean las tarjetas que caben en cada fila.
- **Galerías**: al cambiar el ancho de la ventana, las tarjetas se recolocan sin volver a entrar volando.
- **Correcciones**: el fondo de colores ya no se ve cortado en recto (con un corte que se movía) al estrechar la ventana o con el panel derecho abierto; en el inicio ya no asoma una línea fina bajo cada fila de tarjetas.

### 1.1.9
- **Pantalla completa**: micro junto al botón de pantalla completa para poner y quitar la letra (se recuerda). Sin letra, la portada y los controles se centran. Título, artista y álbum centrados, y la favorita junto al aleatorio.

### 1.1.8
- **Pantalla completa**: el botón de pantalla completa se queda abajo a la derecha, junto al volumen, en el mismo sitio que en la barra del reproductor (antes saltaba arriba al abrir la vista).

### 1.1.7
- **Pantalla completa** (botón a la izquierda del volumen): portada, título, progreso y controles a la izquierda y la letra sincronizada a la derecha, sobre la portada difuminada con luces de sus colores. Dentro de la app o a toda la pantalla (clic derecho en el botón o `F11`; `Esc` para salir).
- **Micrófono de cantar** (de mano, como el de karaoke) como icono del botón de la letra en la barra del reproductor.

### 1.1.6
- **Actualizaciones a tu elección**: Echoplex ya no instala versiones nuevas por su cuenta; avisa con *Actualizar*, *Omitir esta versión* y *Ahora no*.
- **Carpetas de canciones sueltas**: sin imagen propia y con canciones de varios álbumes, muestran un mosaico de sus carátulas incrustadas, y cada canción lleva la suya al sonar. Los álbumes siguen igual.
- **Controles multimedia de Windows** con la carátula incrustada cuando la carpeta no tiene imagen.
- **Correcciones**: abrir y cerrar Ajustes muy rápido ya no deja la app bloqueada tras el fondo oscurecido; el panel *Detalles* ya no puede quedarse saltando con la barra de desplazamiento; los botones de la miniatura de la barra de tareas (*Anterior*, *Siguiente*…) se ven nítidos.

### 1.1.5
- **Rendimiento** (todo se ve igual): páginas más rápidas, pausa de las animaciones de fondo con la ventana minimizada u oculta, portadas grandes con límite de memoria y guardado de estadísticas en segundo plano.
- **Letras de LRCLIB**: si la coincidencia exacta no trae tiempos, se busca una versión sincronizada de la misma canción (mismo título y duración); las guardadas sin tiempos se vuelven a buscar.
- **Correcciones**: saltar rápido entre canciones de OneDrive ya no deja la canción equivocada como actual; algunos FLAC ya no dejan su archivo abierto; los ajustes se guardan de forma segura.

### 1.1.4
- **Texto nítido** en el volumen avanzado, *Ajustes*, los menús emergentes, los cuadros de diálogo (confirmar, renombrar…) y el mini reproductor (antes se veían algo borrosos).

### 1.1.3
- **Árbol de carpetas**: el clic en una carpeta vuelve a abrir solo su página; desplegar y plegar, con la flecha.

### 1.1.2
- **Árbol de carpetas**: el camino hasta la canción que suena, iluminado en negrita, con barritas de ecualizador en la carpeta más honda del camino que se ve (bajan al desplegar); clic en una carpeta con subcarpetas para desplegarla.
- **Ajustes** se abre con una animación suave sobre la app ligeramente oscurecida y se cierra con un fundido: con ✕, Esc, clic fuera o `Ctrl+,`.
- **Clic en una línea de la letra**: salta a ella y empieza a sonar, aunque estuviera en pausa o sin cargar.
- **Ajustes** se abre siempre con los desplegables plegados, aunque uses un tema de *Más temas*.

### 1.1.1
- **Sin pausas entre canciones** (gapless): la siguiente se abre unos segundos antes y la salida enlaza una con otra sin detenerse, si comparten formato (lo normal en un álbum). También en *Bit a bit*.
- **Letra más suave**: se desliza hasta la línea nueva y la que suena se ilumina con un fundido.
- **Desfase de la letra por canción** (pestaña *Letra*, abajo): adelantar o retrasar la letra en pasos de 0,1 s; se guarda para cada canción.
- **Flechas del árbol de carpetas** más visibles.

### 1.1.0
- **Más temas**: debajo de los cinco de siempre, un desplegable con 50 temas por grupos: editores de código (Dracula, Nord, One Dark, Monokai, Solarized, Gruvbox, Tokyo Night, Catppuccin, Rosé Pine, GitHub…), naturaleza y ambientes (Bosque, Océano, Sakura, Café…), retro y videojuegos (Synthwave '84, Matrix, MS-DOS, Commodore 64, Game Boy, Windows 95…) e intensos y alto contraste.
- **Volumen avanzado** con clic derecho en el botón o la barra de volumen: lectura con decimales y en dB, tramos (`0–5 | 0–10 | 0–25 | 0–50 | 0–100 %`) para afinar en volúmenes bajos, pasos de 0,1 y 1 puntos, valor exacto, y rueda o flechas con paso proporcional al rango. La rueda sobre la barra pequeña también va más fina por debajo del 25 %.
- **Ver cambios de la versión** en *Ajustes → Actualizaciones*: las notas de la versión instalada (las de `.github/notas`, incrustadas al compilar).
- El interruptor claro/oscuro (`Ctrl+D`) vuelve también al último tema **claro** elegido, no siempre al Claro.

### 1.0.9
- **Letras de internet (LRCLIB)**, desactivadas por defecto: si una canción no tiene `.lrc` ni letra incrustada, se busca en [lrclib.net](https://lrclib.net), con tiempos por línea para saltar con un clic. Se activa en *Ajustes → Letras* o abajo en la pestaña *Letra* del panel derecho. Solo se envían artista, título, álbum y duración; lo encontrado se guarda en `%LocalAppData%\Echoplex\lyrics` (nunca en la carpeta de música).
- **Botones laterales del ratón** para ir atrás y adelante entre páginas, como en el navegador (además de las flechas y `Alt+←` / `Alt+→`).
- **Explorar → Carpetas**: galería de carátulas como la de álbumes, con las carpetas de primer nivel ordenadas por nombre (las de varios discos, con su mosaico).
- **Más carátulas encontradas**: si un álbum no tiene imagen y su primera canción está solo en OneDrive, se usa la portada incrustada de otra canción del álbum que esté en el PC (tarjetas de álbum, canción y playlist, y reproductor).

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
