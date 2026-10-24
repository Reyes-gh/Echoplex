using System.Security.Cryptography;
using System.Text;
using Echoplex.Models;
using NAudio.Wave;

namespace Echoplex.Services;

/// <summary>
/// La forma de onda de una canción (el volumen medio, RMS, de 800 trozos seguidos), para la barra de progreso. El medio
/// y no el pico: en los discos muy comprimidos el pico está casi siempre arriba y saldría plana. Se calcula
/// decodificándola en un hilo de prioridad mínima y se guarda en waveforms\ (800 bytes por canción), así solo se hace
/// una vez. Solo las del PC: las de la nube habría que bajarlas enteras antes de tiempo.
/// </summary>
public static class WaveformService
{
    private const int Buckets = 800;

    private static string Dir => Path.Combine(SettingsStore.DataDirectory, "waveforms");

    public static bool CanCompute(Song s) => !OnlineLibrary.IsOnline(s.Path) && !s.IsCloud;

    private static string FileFor(Song s)
    {
        var key = $"rms|{s.Path}|{s.Length}|{s.LastWriteTicks}";
        return Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant() + ".bin");
    }

    /// <summary>La guardada, si ya se calculó.</summary>
    public static float[]? Cached(Song s)
    {
        try
        {
            var file = FileFor(s);
            if (!File.Exists(file)) return null;
            var bytes = File.ReadAllBytes(file);
            return bytes.Select(b => b / 255f).ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>La calcula (en un hilo de prioridad mínima) y la guarda. null si no se puede o se cancela.</summary>
    public static Task<float[]?> ComputeAsync(Song s, CancellationToken ct)
    {
        var done = new TaskCompletionSource<float[]?>();
        var thread = new Thread(() =>
        {
            try
            {
                done.TrySetResult(Compute(s, ct));
            }
            catch
            {
                done.TrySetResult(null);
            }
        })
        { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Forma de onda" };
        thread.Start();
        return done.Task;
    }

    private static float[]? Compute(Song s, CancellationToken ct)
    {
        using var reader = AudioOutput.OpenForAnalysis(s.Path);
        var samples = reader.ToSampleProvider();
        int channels = reader.WaveFormat.Channels;
        long totalFrames = Math.Max(1, (long)(reader.TotalTime.TotalSeconds * reader.WaveFormat.SampleRate));
        double perBucket = (double)totalFrames / Buckets;
        var sums = new double[Buckets];
        var counts = new int[Buckets];
        var buf = new float[reader.WaveFormat.SampleRate * channels / 4];
        long frame = 0;
        int n;
        while ((n = samples.Read(buf, 0, buf.Length)) > 0)
        {
            if (ct.IsCancellationRequested) return null;
            LightMode.Sync();
            for (int i = 0; i + channels <= n; i += channels, frame++)
            {
                double v = 0;
                for (int c = 0; c < channels; c++) v += buf[i + c] * buf[i + c];
                int b = (int)Math.Min(Buckets - 1, frame / perBucket);
                sums[b] += v / channels;
                counts[b]++;
            }
        }
        var peaks = new float[Buckets];
        for (int b = 0; b < Buckets; b++) peaks[b] = counts[b] > 0 ? (float)Math.Sqrt(sums[b] / counts[b]) : 0;
        float max = peaks.Max();
        if (max > 0) for (int b = 0; b < Buckets; b++) peaks[b] /= max; // de 0 a 1, lo más fuerte de la canción arriba
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(FileFor(s), peaks.Select(p => (byte)Math.Clamp(Math.Round(p * 255), 0, 255)).ToArray());
        }
        catch
        {
            // sin guardar: se recalculará
        }
        return peaks;
    }
}
