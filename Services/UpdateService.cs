using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Echoplex.Services;

/// <summary>Resultado de buscar/instalar una actualización.</summary>
public enum UpdateState { UpToDate, Installed, Failed }

public sealed record UpdateResult(UpdateState State, Version? Version, string Message);

/// <summary>
/// Actualizaciones desde GitHub Releases: mira la última release publicada, descarga su zip
/// (Echoplex-&lt;versión&gt;-win-x64.zip) y cambia los archivos del programa.
/// Windows no deja sobrescribir un .exe/.dll en uso, pero sí renombrarlo: los actuales pasan a *.old
/// y los nuevos ocupan su sitio; la versión nueva arranca al reiniciar y borra los *.old.
/// Si algo falla a medias se deshace todo.
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "Reyes-gh/echoplex";

    private const string OldSuffix = ".old";
    private readonly string _apiUrl;
    private readonly string _appDir;

    /// <param name="apiUrl">URL de la última release (se puede cambiar para pruebas).</param>
    /// <param name="appDir">Carpeta del programa (por defecto, la del ejecutable).</param>
    public UpdateService(string? apiUrl = null, string? appDir = null)
    {
        _apiUrl = apiUrl ?? $"https://api.github.com/repos/{Repository}/releases/latest";
        _appDir = Path.TrimEndingDirectorySeparator(appDir ?? AppContext.BaseDirectory);
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Borra los archivos *.old que dejó la actualización anterior (al arrancar la versión nueva).</summary>
    public static void CleanupOldFiles(string? appDir = null)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(appDir ?? AppContext.BaseDirectory, "*" + OldSuffix, SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch { /* aún en uso: la próxima vez */ }
            }
        }
        catch
        {
            // carpeta sin acceso
        }
    }

    /// <summary>Busca una versión nueva y, si la hay, la descarga e instala (queda lista para el siguiente arranque).</summary>
    public async Task<UpdateResult> CheckAndInstallAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Echoplex/{current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        // 1. ¿hay una release más nueva?
        JsonElement release;
        try
        {
            using var resp = await http.GetAsync(_apiUrl, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new(UpdateState.UpToDate, current, "Aún no hay versiones publicadas");
            resp.EnsureSuccessStatusCode();
            release = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new(UpdateState.Failed, null, "No se pudo consultar GitHub: " + ex.Message);
        }

        var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            return new(UpdateState.Failed, null, $"La última release tiene una etiqueta que no es una versión: «{tag}»");
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        if (latest <= current) return new(UpdateState.UpToDate, current, $"Tienes la última versión ({current})");

        // 2. el zip de la release (el portable solo si esta instalación es autocontenida)
        bool portable = File.Exists(Path.Combine(_appDir, "coreclr.dll"));
        JsonElement? asset = null;
        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("portable", StringComparison.OrdinalIgnoreCase) != portable) continue;
                asset = a;
                break;
            }
        }
        if (asset is not { } zipAsset)
            return new(UpdateState.Failed, latest, $"La versión {latest} no incluye el archivo Echoplex-{latest}-win-x64.zip");

        if (!CanWrite(_appDir))
            return new(UpdateState.Failed, latest, $"Hay una versión nueva ({latest}), pero no se puede escribir en la carpeta del programa. Descárgala desde GitHub.");

        // 3. descarga y comprobación
        var work = Path.Combine(SettingsStore.DataDirectory, "updates");
        var zipPath = Path.Combine(work, $"Echoplex-{latest}.zip");
        var staging = Path.Combine(work, latest.ToString());
        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(staging);
            var url = zipAsset.GetProperty("browser_download_url").GetString()!;
            await using (var src = await http.GetStreamAsync(url, ct))
            await using (var dst = File.Create(zipPath))
                await src.CopyToAsync(dst, ct);

            if (zipAsset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                await using var fs = File.OpenRead(zipPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!hash.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                    return new(UpdateState.Failed, latest, "La descarga no coincide con la huella publicada en GitHub; no se ha instalado");
            }

            ZipFile.ExtractToDirectory(zipPath, staging);
            var exe = Path.Combine(staging, "Echoplex.exe");
            var dll = Path.Combine(staging, "Echoplex.dll");
            if (!File.Exists(exe) || !File.Exists(dll))
                return new(UpdateState.Failed, latest, "El zip de la release no contiene Echoplex.exe y Echoplex.dll");
            var fv = FileVersionInfo.GetVersionInfo(dll);
            if (new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart) != latest)
                return new(UpdateState.Failed, latest, $"El zip dice ser la versión {latest} pero contiene la {fv.FileVersion}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new(UpdateState.Failed, latest, "No se pudo descargar la actualización: " + ex.Message);
        }

        // 4. cambio de archivos (con marcha atrás)
        try
        {
            Swap(staging, _appDir);
        }
        catch (Exception ex)
        {
            return new(UpdateState.Failed, latest, "No se pudo instalar la actualización: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
        return new(UpdateState.Installed, latest, $"Echoplex {latest} instalado: se usará al reiniciar");
    }

    /// <summary>Pone los archivos de <paramref name="staging"/> en <paramref name="appDir"/>; los actuales quedan como *.old.</summary>
    private static void Swap(string staging, string appDir)
    {
        var done = new List<(string target, bool hadOld)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(staging, file);
                var target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var old = target + OldSuffix;
                if (File.Exists(old)) File.Delete(old);
                bool had = File.Exists(target);
                if (had) File.Move(target, old); // permitido aunque esté en uso
                done.Add((target, had));
                File.Copy(file, target);
            }
        }
        catch
        {
            // marcha atrás: se restauran los archivos originales
            foreach (var (target, had) in Enumerable.Reverse(done))
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (had) File.Move(target + OldSuffix, target);
                }
                catch
                {
                    // se intentó
                }
            }
            throw;
        }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".echoplex-write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Arranca la versión instalada y cierra esta.</summary>
    public static void Restart()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Echoplex.exe");
        App.ReleaseSingleInstance();
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }
}
