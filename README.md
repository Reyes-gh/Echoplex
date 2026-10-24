<p align="center"><img src="Assets/echoplex.png" width="120" alt="Echoplex"></p>

<h1 align="center">Echoplex</h1>

<p align="center">Reproductor de música local u online mediante URL para Windows con la estructura de un servicio de streaming y un estilo minimalista.</p>

## Características

- **Tu música, por carpetas**: árbol de carpetas en el panel lateral, con varias carpetas de música a la vez (cada una es un bloque propio, también una unidad entera como `E:\`) y miniaturas de portada.
- **Páginas de carpeta por discos**: al abrir una carpeta con varios discos, cada disco aparece como un módulo con su portada y su título.
- **Portadas mosaico**: una carpeta sin portada propia combina las de sus discos (2 o 3 en diagonal, 4 en cuadrícula), o las incrustadas en sus canciones si son de álbumes distintos.
- **Lo de los servicios de streaming que tiene sentido en local**: inicio con recientes y más escuchadas, favoritas, historial, añadidas recientemente, playlists, cola, radio automática, aleatorio y repetición, búsqueda, letras (`.lrc`, incrustadas o, si lo activas, de LRCLIB), mini reproductor, temporizador de apagado y resumen de escucha.
- **Pantalla completa**: la canción con su portada y controles a un lado y la letra pasando sola al otro, sobre la portada difuminada con luces de sus colores; ocupando toda la app o toda la pantalla (`F11`).
- **Ecualizador de 15 bandas**: en Ajustes, de 25 Hz a 16 kHz (±12 dB) con preamplificador y preajustes (Graves, Voces, Rock, Electrónica…), con la curva de las bandas dibujada y los valores también a mano. Cada cambio se oye al momento, sin transiciones.
- **Fiel al archivo**: modo *Bit a bit* con WASAPI en exclusivo (sin mezclador de Windows ni remuestreo) y salto exacto a la muestra.
- **OneDrive**: las canciones que solo están en la nube se descargan al reproducirlas y se puede elegir qué dejar en el dispositivo. En el árbol, las carpetas enteras en la nube llevan una nube, y **Mostrar solo locales** oculta lo que está entero en la nube, en el árbol y en Inicio.
- **Carpetas en línea**: carpetas compartidas de Google Drive u OneDrive, o de un servidor web, que se escuchan directamente desde la nube sin copiar nada al PC.
- **En el móvil**: la app Echoplex para Android escucha tu biblioteca del PC en streaming (se conecta con un QR), con tus favoritas, playlists, letras y escuchas compartidas con el PC, en casa y fuera de casa con Tailscale integrado (en el PC, el paquete fuera de casa que se descarga desde Ajustes; en el móvil va dentro de la app), sin instalar nada más. Como Echoplex hace de servidor compatible con Subsonic, también sirven apps como Tempus o Symfonium.
- **Temas**: Claro, Oscuro, TRUE dark, Neon night y 80's retro, y 79 más en *Más temas*, por claros y oscuros y por familias que se pliegan (Dracula, Nord, Solarized, Gruvbox, Catppuccin, Game Boy, Windows XP, MS-DOS, Matrix…), con animaciones que se pueden desactivar.
- **Discord**: si lo activas, tu perfil muestra lo que escuchas con la carátula, la canción, el artista, el álbum y la barra de progreso. Las carátulas se buscan solas en internet o, si lo prefieres, se suben con tu propio programa, igual que con foo_discord_rich.
- **Aviso de actualizaciones** desde las releases de este repositorio: tú decides si actualizar, omitir esa versión o dejarlo para luego.
- **Controles multimedia de Windows** (teclas de medios, panel de volumen), atajos de teclado (F1) y zoom de la interfaz (`Ctrl +` / `Ctrl −`).
- **Cerrar o esconder en la bandeja**: tú eliges en Ajustes qué hace la X. Escondido, Echoplex sigue sonando junto al reloj: clic en su icono para volver; con el botón derecho, reproducir o pausar, siguiente, anterior y *Salir*.

Todas las opciones están en **Ajustes** (engranaje abajo a la izquierda, menú *Echoplex* o `Ctrl+,`): tema, animaciones, carpetas de música, ecualizador, letras, Discord y actualizaciones.

## Instalación

1. Instala el [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) si no lo tienes.
2. Descarga `Echoplex-<versión>-win-x64.zip` de [Releases](../../releases), descomprímelo y abre `Echoplex.exe`.
3. Windows SmartScreen puede avisar la primera vez porque el ejecutable no está firmado: **Más información → Ejecutar de todas formas**.
4. La primera vez usa tu carpeta **Música** de Windows; añade otras desde Ajustes o con el botón **+** junto a «Carpetas».
5. En el móvil (Android 8 o posterior, opcional): descarga `Echoplex-<versión>-android.apk` de la misma release, instálalo y escanea el QR de *Ajustes → Móvil* en Echoplex del PC.

Los ajustes, playlists e historial se guardan en `%LocalAppData%\Echoplex`; la carpeta del programa no guarda nada tuyo.

**Actualizaciones**: al abrirse, Echoplex mira si hay una release nueva en este repositorio y, si la hay, avisa en la barra lateral: *Actualizar* (la descarga, comprueba su huella SHA-256, la instala y reinicia), *Omitir esta versión* o *Ahora no*. Nunca se instala nada sin preguntar. La comprobación se puede desactivar en Ajustes.

## Compilar

Requiere el SDK de .NET 8.

```powershell
dotnet build -c Release
.\publish.ps1          # release en app\Echoplex-<versión>\
.\publish.ps1 -Zip     # además, el .zip para GitHub Releases
```

`publish.ps1` recorta con ILLink las librerías de terceros (NAudio, TagLib# y CommunityToolkit.Mvvm) para dejar solo lo que usa Echoplex; lo que hay que conservar aunque no se vea usado está en `trim.xml`. Con `-NoTrim` van enteras.

## Publicar una versión

1. Sube `<Version>` en `Echoplex.csproj` (p. ej. `2.0.1`).
2. Escribe las notas en `.github/notas/2.0.1.md` (si no, se usa la sección `### 2.0.1` de este README).
3. Haz push a `main`. GitHub Actions ve que esa versión no tiene release, compila con `publish.ps1 -Zip` y publica la release **`v2.0.1`** con **`Echoplex-2.0.1-win-x64.zip`** (el nombre que busca el actualizador). Los push que no cambian la versión no publican nada.

Si hiciera falta a mano: `.\publish.ps1 -Zip` y crear la release con esa etiqueta y ese zip.

El paquete fuera de casa (`tailscale/`, en Go con tsnet de Tailscale) se compila en el mismo paso y se adjunta como **`Echoplex-2.0.1-fuera-de-casa-windows.zip`**: no va en el zip de Echoplex; Echoplex lo descarga desde *Ajustes → Móvil* y comprueba su huella SHA-256. El mismo programa, para Android, va dentro de la app.

La app de Android (`android/`) sale con la misma versión: tras publicar la release, otro paso compila **`Echoplex-2.0.1-android.apk`** y lo añade. Se firma con la clave del proyecto, que va en los secrets del repositorio `ANDROID_KEYSTORE_BASE64` (el `.jks` en base64) y `ANDROID_KEYSTORE_PASSWORD`; sin ellos la release sale sin APK. Para compilarla en local con la clave: `echoplexStoreFile` y `echoplexStorePassword` en el `gradle.properties` de tu `GRADLE_USER_HOME` (nunca en el repo).

Las instalaciones existentes (desde la 1.0.1) la encontrarán la próxima vez que se abran.

## Versiones

### 2.0.2
- **Listas por discos: las columnas encima de cada disco**: en una carpeta con varios discos o subcarpetas, y en las páginas de artista y de género, la fila de columnas (título, artista, álbum…) salía una sola vez, arriba del todo. Ahora sale debajo de la portada y el nombre de cada disco, justo encima de su primera canción, y desde cualquiera de ellas se ordena, se cambia el ancho y se mueven las columnas. Al ordenar por una columna la lista deja de ir por discos y la fila vuelve arriba.
- **Modo ligero cuando Echoplex no se ve**: minimizado, en la bandeja o el mini reproductor, en otro escritorio virtual, con la sesión bloqueada o con la pantalla apagada, deja de animar y de dibujar lo que no se ve: el fondo, las luces, las barritas, la letra y la barra de progreso. Hasta ahora, aunque estuviera minimizado, el fondo en pausa hacía que Windows siguiera preparando unos 50 fotogramas por segundo. Con una canción sonando, minimizado pasa de un 3,7 % a un 1,5 % de un núcleo y en la bandeja de un 4 % a un 1,1 %; lo que queda es casi todo la propia reproducción. Al volver, la barra, el tiempo, la letra y el fondo están donde estarían si no hubiera parado. La música va primero: el hilo que entrega el sonido va siempre con la prioridad de audio de Windows (MMCSS), y mientras no se ve, los análisis en segundo plano (etiquetas, BPM, forma de onda, sonoridad) pasan al modo de fondo de Windows. Se puede apagar en *Ajustes → Apariencia → Animaciones*.

### 2.0.1
- **Columna BPM**: la tabla de canciones puede enseñar el BPM de cada canción junto al tipo (FLAC, MP3…), sacado de las etiquetas del archivo (también el «BPM» de FLAC y OGG que escriben Mp3tag, Picard o Mixed In Key), y se puede ordenar por él. Viene oculta: se enciende en *Ajustes → Biblioteca → Columnas de las listas*, y mientras está oculta no se busca ni se calcula nada. Si las etiquetas no lo traen, se busca en Deezer (se puede apagar en esa misma sección): en segundo plano y sin prisa, solo con el artista y el título, y guardado en el PC. Las etiquetas mandan siempre. Y lo que Deezer tampoco tiene lo calcula Echoplex escuchando el audio (también se puede apagar ahí): las del PC en segundo plano y con la prioridad más baja; las de carpetas en línea, nada más darles a play, con la copia que Echoplex ya baja entera a memoria para reproducirlas: se va analizando según llega y, en cuanto está entera, sale el BPM (sin descargar nada aparte; mientras, un círculo gira en su casilla); y las que están solo en OneDrive, cuando suenan enteras, con el mismo audio que se reproduce (sin decodificarlas otra vez). Con 261 canciones de BPM conocido, cuando el cálculo sale seguro acierta 9 de cada 10 contando las que salen al doble o a la mitad, y unas 8 de cada 10 exactas; si no sale seguro, el número lleva un aviso pequeño que lo explica al pasar el ratón. Y al pasar el ratón por cualquier BPM se ve de dónde sale (etiquetas, Deezer o calculado).
- **Letras de LRCLIB: búsqueda más completa**: algunas canciones que LRCLIB sí tiene (sincronizadas incluso) salían como «No se pudo conectar con LRCLIB», también al pulsar *Reintentar búsqueda*, porque su búsqueda exacta falla en algunas y Echoplex se rendía ahí. Ahora pregunta por la exacta y además por canción + álbum + artista, canción + álbum, canción + artista y solo la canción (hay letras subidas con el artista en japonés o con el nombre del compositor), y elige entre todo lo que salga. Las preguntas van de 3 en 3 y se reintentan si LRCLIB está saturado; si aun así alguna no responde, la letra se enseña pero se vuelve a buscar la próxima vez. Y mientras suena una canción se busca ya la letra de la siguiente, para que salga al momento.
- **Letras de LRCLIB de la versión equivocada**: con dos canciones del mismo título (la versión corta de una banda sonora y la larga de otro álbum), salía la letra de la otra o una que saltaba de golpe a mitad de canción. En LRCLIB la misma letra está subida con duraciones de 78 a 322 s, y hay letras con la segunda mitad metida en 6 segundos. Ahora solo valen las del mismo título y la misma duración (±2 s, como el propio LRCLIB). Siempre sale sincronizada si hay alguna: primero la que no tiene líneas imposibles de cantar (pasado el final de la canción o más deprisa que el rap más rápido grabado), luego la del mismo álbum y luego la que llega más lejos en la canción. Las letras ya guardadas se vuelven a buscar una vez, y *Reintentar búsqueda* aparece también cuando la letra viene de LRCLIB.
- **Menos CPU en reposo con las animaciones puestas**: el sol o la luna del saludo de Inicio hacían redibujar la ventana sin parar a 60 fotogramas por segundo (un 5 % de un núcleo sin que sonara nada). Ahora van a 10, que con un movimiento tan lento se ve igual, y gastan unas cinco veces menos.
- **App de Android: la carátula en la notificación**: la notificación de lo que suena, la pantalla de bloqueo y Android Auto enseñan la carátula del álbum, también fuera de casa por Tailscale, al pasar del wifi a los datos y con lo descargado sin conexión.

### 2.0.0
- **Echoplex en el móvil** (apagado de serie): en *Ajustes → Móvil*, Echoplex hace de servidor compatible con Subsonic y desde el móvil, con la app Echoplex para Android (abajo) o con otra compatible como Tempus (gratis y de código abierto) o Symfonium, escuchas tu biblioteca en streaming sin copiarla: por artistas, álbumes, géneros o tus carpetas, con búsqueda, carátulas, tus favoritas y tus playlists (lo que cambies en el móvil se ve en el PC). Lo que escuches en el móvil cuenta en *Tu resumen* y en tu historial, y se envía a Last.fm o ListenBrainz si los tienes conectados. Las apps que lo usan reciben además las letras (sincronizadas si las hay), el volumen de cada canción para normalizar, mezclas de canciones parecidas hechas con la radio de Echoplex y la cola para seguir donde lo dejaste (también lo que sonaba en el PC). Funciona en el wifi de casa con la dirección, el usuario y la contraseña que salen en Ajustes, y fuera de casa con el **paquete fuera de casa**: Tailscale (gratis) dentro de Echoplex, sin instalar nada más ni abrir nada en el router. Se descarga desde *Ajustes → Móvil* (no va en el zip), inicias sesión en el navegador y listo; la app Echoplex del móvil lo lleva dentro. Fuera de casa, Echoplex convierte las canciones a MP3 sobre la marcha (192 kbps de serie, o la calidad que elijas o pida la app) para gastar una fracción de los datos sin esperas. En casa van tal cual. Echoplex no abre nada a internet por su cuenta.
- **App Echoplex para Android**: descarga **Echoplex-2.0.0-android.apk** de la release, instálala y conéctala escaneando el QR de *Ajustes → Móvil → Mostrar QR para la app Echoplex*, sin escribir nada. Inicio con lo reciente y lo que más escuchas, búsqueda, tu biblioteca (artistas, álbumes, playlists, géneros y carpetas) y tus favoritas; pantalla de reproducción con las luces de colores de la carátula (deslízala para pasar de canción), la letra sincronizada (toca una línea para saltar allí) y la cola, que se reordena arrastrando; playlists que se crean, renombran y borran, y canciones que se añaden o quitan desde su menú; normalización de volumen como en el PC; temporizador de apagado; descargas de álbumes, playlists o canciones para escuchar sin conexión; seguir donde lo dejaste (también lo que sonaba en el PC); Android Auto; y los mismos temas que en el PC. Sigue sonando con la pantalla apagada, con los controles en la notificación. En el wifi de casa va directa al PC y fuera, por el Tailscale que lleva dentro (inicias sesión en *Ajustes → Fuera de casa*, o antes de escanear el QR si estás fuera); al pasar del wifi a los datos cambia sola, también a mitad de canción. Se actualiza desde la propia app: cuando hay versión nueva lo avisa en Inicio y se instala encima con un toque (Android pide confirmar), sin perder nada. Pesa unos 11 MB.
- **Portadas de playlists en mosaico**: una playlist con canciones de varios álbumes lleva un mosaico con sus carátulas distintas (2 o 3 en franjas diagonales, 4 en cuadrícula), como las carpetas; con un solo álbum, su carátula.

### 1.1.25
- **Ecualizador rehecho por dentro**: ahora suena lo que marcan los deslizadores. Antes cada banda empujaba a sus vecinas y la suma se pasaba (todo a +6 sonaba a +9, todo a +12 a casi +20, y el preajuste Graves subía +8,6 en vez de +6); ahora cada filtro se calcula teniendo en cuenta a los demás (el método del ecualizador gráfico preciso de la Universidad Aalto) y los preajustes suenan con menos de 0,2 dB de diferencia. La banda de 16 kHz da brillo de verdad en vez de un pitido estrecho. Cuando algo se pasaría de 0 dB, un limitador baja solo esos picos en vez de deformar la onda, y en la salida normal todo va en coma flotante de 32 bits (sin volver a redondear a 16 bits). Al mover una banda o cambiar de preajuste se oye al momento, pero sin chasquidos. Debajo del ecualizador hay una gráfica nueva, **Respuesta real**, con lo que sale de verdad por los altavoces, y un interruptor opcional, **Margen automático contra la saturación**, que baja el volumen lo justo para que nunca haga falta limitar.
- **Recopilatorios, discos dobles y géneros**: Echoplex lee ahora también el artista del álbum, el número de disco, el año y el género de las etiquetas. En *Artistas*, un recopilatorio o un disco con invitados ya no crea un artista por cada canción suelta (las canciones siguen saliendo en la página de cada artista que aparece). Un álbum doble, en la misma carpeta o en subcarpetas «CD 1», «CD 2»…, es un solo álbum y suena en orden: el disco 1 entero y luego el 2, sin mezclar sus pistas. Y en *Explorar* hay una sección nueva, **Géneros**. La primera vez, Echoplex vuelve a leer las etiquetas en segundo plano para completar estos datos (las de carpetas en línea tardan más).
- **Normalización de volumen**: en *Ajustes → Sonido*, que todas las canciones suenen igual de fuertes (apagada de serie). Por canción, por álbum (respeta las diferencias entre las canciones de un disco) o inteligente (por álbum si escuchas un álbum o una carpeta en orden, por canción en aleatorio, listas o radio). Usa las etiquetas ReplayGain de cada archivo y, si no las tiene, lo que mide Echoplex la primera vez que una canción suena entera, con la misma norma (EBU R128). Con **Medir la biblioteca** se miden ya las del PC, de una en una y sin quitarle CPU a nada. Nunca sube tanto que sature, y en *Bit a bit* no se aplica.
- **Búsqueda que perdona**: encuentra lo que buscas aunque escribas con alguna errata («beatels», «metalica», «coldpay»), sin acentos ni signos («acdc» encuentra «AC/DC»), y ordena los resultados por lo bien que encajan: primero lo que es exactamente eso, luego lo que empieza así, lo que lo contiene y, al final, lo que se parece. También busca por género.
- **Arrastrar y soltar**: arrastra canciones (todas las seleccionadas) o una carpeta del panel lateral a la cola de *A continuación* (justo donde la sueltes, o al final) o a una playlist del panel lateral, que se marca con un + al pasar por encima. La cola también se reordena arrastrando sus canciones.
- **Página de artista e inicio**: la página de un artista ordena su discografía por años, de la más antigua a la más nueva (cada disco con su año), y enseña sus años y sus géneros principales. En el inicio, el saludo lleva un sol de día o una luna de noche, en el color del tema.
- **Playlists inteligentes y M3U**: con el **+** de *Playlists* puedes crear una playlist inteligente, que se rellena sola con lo que cumpla sus reglas (de toda tu música, un artista, un género o una carpeta; solo favoritas, sin escuchar en tantos días, escuchadas al menos tantas veces, añadidas hace poco o de tales años), en el orden que elijas y hasta las canciones que quieras; se pone al día cada vez que la abres o suena, y lleva un rayo en el panel lateral. Y puedes **importar** playlists M3U de otros reproductores (cada canción se busca en tu biblioteca, también por el nombre del archivo) y **exportar** las tuyas a M3U con el clic derecho.
- **Forma de onda en la barra de progreso** (opcional, apagada de serie): en *Ajustes → Apariencia → Barra de progreso*, la barra enseña la forma de la canción, con las partes flojas bajas y las fuertes altas, y lo ya escuchado en el color del tema. Se calcula la primera vez que suena cada canción del PC, sin quitarle CPU a nada, y se guarda para la siguiente.
- **Fundido entre canciones** (apagado de serie): en *Ajustes → Sonido*, la que acaba se va apagando mientras entra la siguiente, en 3, 6 o 9 segundos o los que elijas (de 1 a 10), sin bajón de volumen a mitad. Si prefieres que un álbum escuchado en orden suene enlazado, sin fundidos, como se grabó (directos, álbumes conceptuales), apaga **Fundir también las pistas seguidas de un mismo disco**. Con la normalización activa, la que entra ya lo hace con su volumen. En *Bit a bit* no se aplica.
- **Scrobbling en Last.fm y ListenBrainz** (apagado hasta que conectes una cuenta): en *Ajustes → Scrobbling*, cada canción que escuchas al menos la mitad (o 4 minutos) se apunta en tu perfil, y mientras suena sale como «Escuchando ahora». Para Last.fm, pega la API key y el Shared secret de tu cuenta de API (gratis) y da permiso en el navegador; para ListenBrainz, tu token de usuario. Se guardan cifrados con tu cuenta de Windows. Sin conexión no se pierde nada: las escuchas esperan y se envían en cuanto se puede. Solo se envían el artista, la canción, el álbum y la duración.
- **Atajos globales** (apagados de serie): en *Ajustes → General*, **Ctrl + Alt + Espacio** reproduce o pausa, **Ctrl + Alt + → / ←** pasa de canción, **Ctrl + Alt + ↑ / ↓** sube o baja el volumen y **Ctrl + Alt + L** marca la que suena como favorita, desde cualquier programa, con Echoplex en segundo plano o en la bandeja. Si otro programa ya usa alguna combinación, Echoplex lo dice, y puedes pasarlas a Ctrl + Alt + Mayús.
- **Salud de la biblioteca**: en *Tu resumen* (o *Ajustes → Biblioteca*), una revisión de tu música: canciones repetidas, álbumes sin carátula, canciones sin título, artista, álbum, número de pista, año o género, archivos que no se pueden leer y carpetas vacías. Cada apartado se abre como una lista normal para escucharlas o ir a su carpeta. Solo lo enseña: Echoplex no cambia ni borra nada.

### 1.1.24
- **Ajustes, renovado**: a la izquierda hay una barra lateral con todas las secciones (Apariencia, Biblioteca, Sonido, Letras, Discord, General y Actualizaciones) y sus apartados debajo; un clic te lleva directo a cada uno y, al desplazarte, marca dónde estás. Cada sección empieza con un título grande y una frase que la explica, y cada apartado agrupa sus opciones en una tarjeta propia, así se ve de un vistazo qué va con qué. Arriba se indica en qué sección y apartado estás y, a la derecha, va la firma del autor, que lleva a su GitHub.
- **Abrir Echoplex con carpetas en línea, mucho más rápido**: comprobar si una carpeta de OneDrive ha cambiado ya es una sola petición (medio segundo) en vez de volver a recorrer todas sus subcarpetas cada vez (con una biblioteca grande, unos 40 segundos ocupando la conexión mientras cargaban las carátulas). Las carátulas de las carpetas en línea se guardan en pequeño en tu PC la primera vez y ya no se vuelven a bajar en cada arranque; la música sigue sin guardarse. Además, un corte de conexión suelto ya no hace fallar la comprobación, y las canciones cuyas etiquetas no se pueden leer ya no se descargan enteras una y otra vez buscando su carátula. Y las carátulas de tus carpetas del PC ya no esperan detrás de las que se están bajando de la nube: van por su propia cola y salen al momento.
- **Conectar y desconectar carpetas en línea**: cada una tiene su interruptor en *Ajustes → Carpetas en línea* y, en su página, un mundo al lado del nombre que la conecta o la desconecta con un clic. Conectada, el mundo brilla con el color del tema. Desconectada sigue en el panel lateral, atenuada y con el mundo tachado, pero sus canciones no se ven ni suenan y no se le pide nada a la nube; al volver a conectarla aparecen al momento. Además, la portada de una carpeta en línea es ahora un mosaico con las carátulas de sus álbumes (antes salía una imagen suelta que hubiera en ella), y en su página la disponibilidad dice que suena desde la nube en vez de «Todo en este dispositivo».

### 1.1.23
- **Carpetas en línea, escuchadas desde la nube**: en *Ajustes → Carpetas en línea* puedes añadir una carpeta compartida de **Google Drive** o de **OneDrive** («Cualquier persona con el enlace»), el listado de carpetas de un servidor web o el enlace a un archivo de audio, con el nombre que quieras. Sale en el panel lateral con un mundo en vez de la nota y se usa como cualquier otra carpeta (portadas, letras `.lrc`, favoritas, playlists…), pero las canciones no se copian a tu PC: suenan directamente desde la nube en cuanto llega el primer trozo (en OneDrive, en menos de medio segundo) y se puede saltar a cualquier punto. Mientras se añade o se comprueba si hay cambios, un indicador cuenta las canciones que va encontrando. Con OneDrive no hace falta iniciar sesión.
- **Los álbumes se abren siempre por arriba**: al ir a un álbum desde una canción estando ya en ese álbum (o en una página casi igual), la página nueva se quedaba bajada y la portada, el título y los botones (*Reproducir*, *Aleatorio*, *Radio*…) quedaban fuera de la vista. Ahora cualquier página que abras empieza arriba del todo.

### 1.1.22
- **Reintentar la búsqueda de una letra**: cuando LRCLIB no tiene la letra de una canción, Echoplex lo apunta y no vuelve a preguntar por ella en 7 días. Ahora, en la pestaña *Letra*, encima de «Buscar letras en internet», sale **Reintentar búsqueda** para preguntar ya sin esperar, y a su lado un botón de información (ⓘ) que lo explica, al pasar el ratón y al pulsarlo: a veces, al buscar la letra y no encontrarla, se guarda vacía y no se vuelve a intentar hasta dentro de 7 días; con el botón se vuelve a buscar al momento. También aparece si no se pudo conectar con LRCLIB.
- **El ecualizador ya no se mueve sin querer**: apagado no responde a nada (solo al interruptor que lo activa), y activado la rueda del ratón no mueve ninguna banda hasta que hayas hecho clic dentro del ecualizador; si no, la rueda desplaza Ajustes como en cualquier otro sitio. Los clics funcionan como siempre.
- **El desfase de la letra no se mueve**: el botón para quitar el desfase sale ahora a la izquierda del −, así que al aparecer ya no empuja el − y el +, que se quedan siempre en el mismo sitio.

### 1.1.21
- **La X cierra Echoplex, o lo esconde si lo prefieres**: en *Ajustes → Otros*, «Al pulsar la X» deja elegir entre **Cerrar Echoplex** (de serie) y **Esconderlo en la bandeja**, junto al reloj y con la música sonando. El icono de la bandeja sigue ahí mientras Echoplex está abierto.
- **Ecualizador: valores a mano y la curva a la vista**: el número de cada banda se puede escribir (Enter para aplicarlo, Esc para dejarlo, ↑ ↓ medio dB), y una línea une las 15 bandas (sin el preamplificador) para ver la forma del sonido.
- **Barra de desplazamiento en las carpetas grandes**: en las listas de miles de canciones el tirador de la barra quedaba en unos pocos píxeles y parecía que no había barra. Ahora tiene siempre un tamaño mínimo que se puede agarrar, para bajar del tirón.

### 1.1.20
- **Ecualizador de 15 bandas**: en *Ajustes → Ecualizador*, de 25 Hz a 16 kHz (±12 dB por banda), con preamplificador y preajustes (Plano, Graves, Agudos, Voces, Rock, Pop, Electrónica, Acústica, Clásica y Sonoridad). Cada cambio se oye al momento, sin transiciones. Doble clic en una banda la deja a 0 dB y la rueda del ratón la mueve medio dB. Con *Bit a bit* no se aplica, para que la señal llegue intacta.

### 1.1.19
- **La X deja Echoplex en la bandeja**: al cerrar la ventana, Echoplex sigue sonando escondido junto al reloj (en la flecha de los iconos ocultos). Clic en su icono para volver; con el botón derecho, reproducir o pausar, siguiente, anterior y **Salir**, que lo cierra del todo. Si abres Echoplex otra vez mientras está escondido, vuelve la ventana que ya tenías.
- **Solo favoritas, dentro de las carpetas**: el interruptor **♥ Solo favoritas** de la página de una carpeta deja solo sus favoritas (y las de sus subcarpetas) para verlas y reproducirlas, sin irte a Favoritas; sigue puesto en todas las carpetas hasta que lo apagues.
- **Mostrar solo locales, también en Inicio**: con el interruptor encendido, Inicio deja de enseñar las canciones, discos, carpetas y artistas que están enteros en la nube; cada sección se rellena con lo que sí tienes descargado.
- **GitHub en Ajustes**: en *Ajustes → Otros*, un botón con el logo de GitHub abre la página de Echoplex.

### 1.1.18
- **Carpetas en la nube, en el árbol**: las carpetas que están enteras en OneDrive (sin descargar) llevan una nube a la derecha, y el interruptor **Mostrar solo locales**, encima del árbol, las oculta. La nube se pone o se quita sola al descargar o liberar espacio, también desde el Explorador.
- **El árbol ya no se recarga ni salta arriba**: cuando cambia algo en las carpetas de música, se actualiza sobre el que ya había (lo desplegado sigue desplegado y el panel lateral no se mueve); si lo que cambió no es música, no se toca nada, y la página que estás viendo sigue por donde ibas.
- **Una unidad entera como carpeta de música** (`E:\`): el árbol no marcaba la carpeta de lo que suena, la propia unidad salía vacía y su menú no encontraba canciones. Lo que pasa en sus carpetas de sistema (*System Volume Information*) ya no provoca reescaneos.

### 1.1.17
- **Más temas, por claros y oscuros**: los cinco de siempre siguen arriba; *Más temas* va en *Claros* y *Oscuros* y, dentro, por familias, todo desplegado al abrirlo y plegable, con cuántos temas tiene cada grupo y el que usas si está dentro.
- **29 temas nuevos** (84 en total): Catppuccin Frappé, Rosé Pine Moon, Poimandres, Darcula, Shades of Purple, Vesper, One Light, Tokyo Night Day, Night Owl Light, Vitesse Light, Noche estrellada, Tormenta, Lago, Cielo, Pradera, Niebla, Otoño, una familia nueva *suaves y pastel* (Chicle, Melocotón, Vainilla, Pistacho, Nube, Malvavisco), Virtual Boy, Super Nintendo, Windows XP, Mac OS 9, Ultravioleta y Tinta roja.

### 1.1.16
- **Carátula en Discord de canciones que estaban en OneDrive**: si la canción estaba solo en la nube y su carátula va incrustada en el archivo, Discord enseñaba el logo toda la canción; ahora, en cuanto el archivo termina de bajar, sale la carátula.

### 1.1.15
- **Discord se arregla solo**: mientras suena una canción, el estado se vuelve a mandar cada minuto con la carátula recién comprobada; si Discord enseñaba una interrogación en su lugar, como mucho al minuto sale bien. Las carátulas recién subidas se comprueban antes de mandarlas (mientras no están en internet, sale el logo) y las que subió foo_discord_rich en BMP o TIFF, que Discord no enseña, se sustituyen por una subida en PNG.

### 1.1.14
- **Echoplex pesa casi la mitad**: el programa pasa de 2,2 MB a 1,3 MB y la descarga, de 813 KB a unos 530 KB, sin cambiar nada de cómo funciona. De NAudio, TagLib# y CommunityToolkit.Mvvm solo queda lo que usa Echoplex, el icono va una sola vez y comprimido (antes iba tres veces) y dentro del programa solo van las notas de la versión instalada.
- **Carátulas manuales en Discord a calidad original**: se suben tal cual, la misma imagen de la carpeta o la incrustada, sin reducirla ni recomprimirla (antes, JPEG de 512 px). Las que ya se habían subido reducidas se vuelven a subir en original la próxima vez que suenen.

### 1.1.13
- **Carátulas en Discord sin programa**: de serie, Echoplex busca la carátula de cada álbum en internet (Deezer e iTunes, gratis y sin cuentas; solo se envían el artista y el álbum). Solo vale la del álbum exacto de ese artista; si no la encuentra, sale el logo de Echoplex. Las canciones sueltas sin álbum se buscan por su título. Lo encontrado se guarda en tu PC para no volver a buscarlo.
- **Carátulas manuales**: subirlas con tu propio programa, igual que con foo_discord_rich, pasa a ser una opción en *Ajustes → Discord*. Solo en ese modo se puede usar «Actualizar carátula en Discord». Si ya tenías un programa puesto, sigue activado.

### 1.1.12
- **Discord**: tu perfil puede mostrar lo que escuchas con la carátula, la canción, el artista, el álbum y la barra de progreso. Se activa en *Ajustes → Discord* (viene desactivado) y no hace falta iniciar sesión: usa la app de Discord abierta en el PC. Tú eliges qué sale en la lista de miembros tras «Escuchando a» (el artista, la canción o Echoplex). Un ▶ o ⏸ en la esquina de la carátula indica si suena o está en pausa, y se puede quitar para verla entera. En pausa se mantiene un minuto y luego se quita. Al cambiar de canción, el estado cambia de una vez con su carátula, y si hubo varios cambios seguidos se vuelve a mandar al poco por si Discord se saltó alguno.
- **Carátulas en Discord**: Discord solo enseña imágenes de internet, así que Echoplex las sube con el programa que le digas, igual que foo_discord_rich: le pasa la ruta de la imagen y espera su URL. Cada portada se sube una sola vez, en JPEG de 512 px, y la URL se guarda en un JSON local junto al nombre del álbum. Si usas foo_discord_rich, se aprovechan las que ya subiste con él. Sin programa sale el logo de Echoplex. Si cambias una carátula, **Actualizar carátula en Discord** (clic derecho en cualquier carátula, o en el menú **⋯** del álbum) la vuelve a mandar a tu programa y cambia la URL guardada.
- **Correcciones**: favoritas y playlists se guardan al momento, y también al apagar o cerrar la sesión de Windows. Antes, si Echoplex no se cerraba bien, se perdía lo marcado en esa sesión. Además, cada día se hace una copia de seguridad de tus datos en `%LocalAppData%\Echoplex\backups`, con las de las dos últimas semanas.

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
