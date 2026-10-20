using System.Globalization;
using Echoplex.Models;
using NAudio.Wave;

namespace Echoplex.Services.Subsonic;

/// <summary>
/// Canciones convertidas a MP3 para el móvil: cuando la app lo pide (formato mp3 o un máximo de kbps, como hacen Tempus y
/// compañía con los datos móviles) o, si no dice nada y se conecta desde fuera de casa (por Tailscale), con la calidad de
/// Ajustes. En casa, el archivo tal cual.
/// </summary>
public sealed partial class SubsonicServer
{
    /// <summary>Calidad fuera de casa cuando la app no pide nada (kbps; 0 = el archivo tal cual).</summary>
    public int AwayKbps { get; set; } = 192;

    /// <summary>A cuántos kbps convertir esta petición, o 0 para mandar el archivo tal cual.</summary>
    private int TranscodeKbps(HttpLite.Request req, Song song, bool download)
    {
        if (download) return 0;
        var format = req["format"]?.Trim().ToLowerInvariant();
        if (format == "raw") return 0;
        var ext = Path.GetExtension(song.Path).TrimStart('.').ToLowerInvariant();
        int max = int.TryParse(req["maxBitRate"], out var m) ? m : 0;
        if (max <= 0 && string.IsNullOrEmpty(format) && Tailscale.IsTailscaleAddress(req.Remote)) max = AwayKbps; // fuera de casa
        bool otherFormat = !string.IsNullOrEmpty(format) && format != ext;
        if (max <= 0 && !otherFormat) return 0;
        int target = max > 0 ? Math.Min(max, 320) : 192;
        double seconds = song.Duration?.TotalSeconds ?? 0;
        int sourceKbps = seconds > 0 ? (int)(song.Length * 8 / seconds / 1000) : 0;
        if (!otherFormat && sourceKbps > 0 && sourceKbps <= target + 8) return 0; // ya es así de ligera
        if (format == "mp3" && ext == "mp3" && sourceKbps > 0 && sourceKbps <= target + 8) return 0;
        return target;
    }

    /// <summary>
    /// La canción en MP3 a medida que se convierte. Se anuncia un tamaño (el de su duración a esa tasa, con un poco de margen
    /// que se rellena al final) para que la app pueda saltar: un salto llega como un rango de bytes y se convierte desde
    /// el segundo que le corresponde. También vale timeOffset (segundos), como en Subsonic.
    /// </summary>
    private async Task<HttpLite.Response> TranscodeAsync(HttpLite.Request req, Song song, int kbps)
    {
        WaveStream reader;
        try
        {
            reader = await Task.Run(() => AudioOutput.OpenForAnalysis(song.Path));
        }
        catch (Exception ex)
        {
            Log($"{req.Remote} convertir {Path.GetFileName(song.Path)}: no se pudo abrir ({ex.Message})");
            return HttpLite.Response.Text(404, "No se pudo abrir la canción");
        }
        kbps = Mp3Encoder.Bitrates.LastOrDefault(b => b <= kbps, Mp3Encoder.Bitrates[0]);
        long bytesPerSecond = kbps * 125L;
        double total = reader.TotalTime.TotalSeconds;
        long length = (long)(total * bytesPerSecond) + bytesPerSecond / 2 + 4096; // + el retardo del codificador

        double offset = double.TryParse(req["timeOffset"], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? Math.Max(0, t) : 0;
        long from = 0;
        int status = 200;
        var response = new HttpLite.Response { ContentType = "audio/mpeg", Done = reader.Dispose };
        response.Headers["Accept-Ranges"] = "bytes";
        if (offset > 0)
        {
            length = Math.Max(0, (long)((total - offset) * bytesPerSecond)) + bytesPerSecond / 2 + 4096;
        }
        else if (req.Headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
                 && long.TryParse(range[6..].Split(',')[0].Split('-')[0], out var a) && a > 0)
        {
            if (a >= length)
            {
                reader.Dispose();
                var bad = HttpLite.Response.Text(416, "");
                bad.Headers["Content-Range"] = $"bytes */{length}";
                return bad;
            }
            from = a;
            offset = (double)from / bytesPerSecond;
            status = 206;
            response.Headers["Content-Range"] = $"bytes {from}-{length - 1}/{length}";
        }
        response.Status = status;
        response.Length = length - from;
        Log($"{req.Remote} {req["c"]}: {Path.GetFileName(song.Path)} en MP3 de {kbps} kbps desde el segundo {offset:0}");

        response.Writer = async (net, ct) =>
        {
            try
            {
                await WriteMp3Async(net, ct);
            }
            catch (Exception ex) when (ex is not (IOException or OperationCanceledException))
            {
                Log($"{req.Remote} convertir {Path.GetFileName(song.Path)} ({reader.WaveFormat}): {ex.GetType().Name} {ex.Message} · {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
                throw;
            }
        };
        return response;

        async Task WriteMp3Async(Stream net, CancellationToken ct)
        {
            int rate = reader.WaveFormat.SampleRate % 48000 == 0 ? 48000 : 44100;
            int channels = Math.Min(2, Math.Max(1, reader.WaveFormat.Channels));
            var pcmFormat = new WaveFormat(rate, 16, channels);
            if (offset > 0) reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(offset, Math.Max(0, total - 0.5)));
            using var resampler = new MediaFoundationResampler(reader, pcmFormat) { ResamplerQuality = 60 };
            using var encoder = new Mp3Encoder(rate, channels, kbps);
            var pcm = new byte[pcmFormat.AverageBytesPerSecond / 2 / pcmFormat.BlockAlign * pcmFormat.BlockAlign];
            long left = length - from;
            var pending = new List<(byte[] data, int count)>();
            void Collect(byte[] data, int count) => pending.Add((data, count));
            async Task Flush()
            {
                foreach (var (data, count) in pending)
                {
                    int n = (int)Math.Min(count, left);
                    if (n <= 0) break;
                    await net.WriteAsync(data.AsMemory(0, n), ct);
                    left -= n;
                }
                pending.Clear();
            }
            while (left > 0)
            {
                int n = resampler.Read(pcm, 0, pcm.Length);
                if (n <= 0)
                {
                    encoder.Finish(Collect);
                    await Flush();
                    break;
                }
                encoder.Encode(pcm, n, Collect);
                await Flush();
            }
            // el tamaño se anunció antes de convertir: lo que falte se rellena con ceros (los decodificadores los ignoran)
            var zeros = new byte[8192];
            while (left > 0)
            {
                int n = (int)Math.Min(zeros.Length, left);
                await net.WriteAsync(zeros.AsMemory(0, n), ct);
                left -= n;
            }
        }
    }
}
