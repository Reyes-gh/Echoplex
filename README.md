<p align="center"><img src="Assets/echoplex.png" width="120" alt="Echoplex"></p>

<h1 align="center">Echoplex</h1>

<p align="center">Reproductor de música local para Windows con la estructura de Spotify y un estilo minimalista.</p>

## Características

- **Tu música, por carpetas**: árbol de carpetas en el panel lateral, con varias carpetas de música a la vez (cada una es un bloque propio) y miniaturas de portada.
- **Páginas de carpeta por discos**: al abrir una carpeta con varios discos, cada disco aparece como un módulo con su portada y su título.
- **Lo de Spotify que tiene sentido en local**: inicio con recientes y más escuchadas, favoritas, historial, añadidas recientemente, playlists, cola, radio automática, aleatorio y repetición, búsqueda, letras (`.lrc`), mini reproductor, temporizador de apagado y resumen de escucha.
- **Fiel al archivo**: modo *Bit a bit* con WASAPI en exclusivo (sin mezclador de Windows ni remuestreo) y salto exacto a la muestra.
- **OneDrive**: las canciones que solo están en la nube se descargan al reproducirlas y se puede elegir qué dejar en el dispositivo.
- **Temas**: Claro, Oscuro, TRUE dark, Neon night y 80's retro, con animaciones que se pueden desactivar.
- **Controles multimedia de Windows** (teclas de medios, panel de volumen) y atajos de teclado (F1).

## Instalación

1. Instala el [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) si no lo tienes.
2. Descarga `Echoplex-<versión>-win-x64.zip` de [Releases](../../releases), descomprímelo y abre `Echoplex.exe`.
3. Windows SmartScreen puede avisar la primera vez porque el ejecutable no está firmado: **Más información → Ejecutar de todas formas**.

Los ajustes, playlists e historial se guardan en `%LocalAppData%\Echoplex`; la carpeta del programa no guarda nada tuyo.

## Compilar

Requiere el SDK de .NET 8.

```powershell
dotnet build -c Release
.\publish.ps1          # release en app\Echoplex-<versión>\
.\publish.ps1 -Zip     # además, el .zip para GitHub Releases
```
