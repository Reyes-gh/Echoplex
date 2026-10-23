using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using Echoplex.Models;

namespace Echoplex.Services;

/// <summary>
/// El BPM de Deezer (api.deezer.com, gratis y sin cuenta) para las canciones que no lo traen en sus etiquetas
/// (encendido de serie; se apaga en Ajustes). Se pregunta en segundo plano y sin prisa (Deezer admite unas 50 peticiones cada 5 s; aquí van
/// muy por debajo), primero las de la lista que se está viendo, y lo encontrado se guarda en bpm-online.json para no
/// volver a preguntar. Lo que Deezer no tiene se vuelve a mirar pasado un mes. No toca los archivos.
/// </summary>
public sealed class OnlineBpmService
{
    private sealed class Entry
    {
        public uint B { get; set; }  // BPM (0 = Deezer no lo tiene)
        public long T { get; set; }  // cuándo se preguntó (ticks UTC)
    }

    private static readonly TimeSpan RetryNotFoundAfter = TimeSpan.FromDays(30);
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(400);

    private readonly Dispatcher _ui;
    private readonly string _file = Path.Combine(SettingsStore.DataDirectory, "bpm-online.json");
    private readonly Dictionary<string, Entry> _cache;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private List<Song> _all = new();
    private int _index;
    private Queue<Song> _priority = new();
    private volatile bool _enabled;
    private bool _started;
    private int _unsaved;

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Echoplex/{UpdateService.CurrentVersion} (+https://github.com/{UpdateService.Repository})");
        return http;
    });

    public OnlineBpmService(Dispatcher ui)
    {
        _ui = ui;
        try { _cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file)) ?? new(); }
        catch { _cache = new(); }
    }

    private static string Key(Song s) => s.Path.ToLowerInvariant();

    /// <summary>Encendido o apagado (Ajustes). Apagado no pregunta nada y quita de las canciones lo que vino de Deezer.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Apply(_all);
            if (value) _signal.Release();
        }
    }

    /// <summary>Pone a las canciones el BPM de Deezer ya guardado (en el hilo de la interfaz).</summary>
    private void Apply(IEnumerable<Song> songs)
    {
        lock (_lock)
            foreach (var s in songs)
                s.OnlineBpm = _enabled && _cache.TryGetValue(Key(s), out var e) ? e.B : 0;
    }

    public void Start(List<Song> all, bool enabled)
    {
        lock (_lock) { _all = all; _index = 0; _priority = new(); }
        _enabled = enabled;
        Apply(all);
        if (_started) { _signal.Release(); return; }
        _started = true;
        _ = Task.Run(LoopAsync);
        System.Windows.Application.Current.Exit += (_, _) => Save();
    }

    /// <summary>Primero estas (la lista que se está viendo).</summary>
    public void Prioritize(IEnumerable<Song> songs)
    {
        var q = new Queue<Song>(songs);
        lock (_lock) _priority = q;
        _signal.Release();
    }

    /// <summary>Han llegado etiquetas nuevas: puede que ahora se sepa que alguna no trae el BPM.</summary>
    public void Wake()
    {
        lock (_lock) _index = 0;
        _signal.Release();
    }

    /// <summary>¿Hay que preguntar por ella? Con las etiquetas ya leídas y sin BPM en ellas, y sin respuesta reciente.</summary>
    private bool Needs(Song s)
    {
        if (s.TagBpm > 0 || !s.MetadataLoaded || s.MetaTitle.Trim().Length == 0) return false;
        lock (_lock)
            return !_cache.TryGetValue(Key(s), out var e)
                   || (e.B == 0 && DateTime.UtcNow - new DateTime(e.T, DateTimeKind.Utc) > RetryNotFoundAfter);
    }

    private Song? Next()
    {
        lock (_lock)
        {
            while (_priority.Count > 0)
            {
                var c = _priority.Dequeue();
                if (Needs(c)) return c;
            }
            while (_index < _all.Count)
            {
                var c = _all[_index++];
                if (Needs(c)) return c;
            }
        }
        return null;
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            var song = _enabled ? Next() : null;
            if (song == null)
            {
                Save();
                await _signal.WaitAsync();
                continue;
            }
            uint? bpm;
            try
            {
                bpm = await LookupAsync(song.MetaTitle.Trim(), song.PrimaryArtist.Trim(), song.Duration?.TotalSeconds);
            }
            catch
            {
                // sin conexión o Deezer saturado: no se apunta nada y se espera un rato antes de seguir
                lock (_lock) _index = Math.Max(0, _index - 1);
                await Task.Delay(TimeSpan.FromMinutes(1));
                continue;
            }
            if (bpm is { } b)
            {
                lock (_lock) _cache[Key(song)] = new Entry { B = b, T = DateTime.UtcNow.Ticks };
                if (b > 0 && _enabled) _ui.BeginInvoke(DispatcherPriority.Background, () => song.OnlineBpm = b);
                if (++_unsaved >= 25) Save();
            }
            await Task.Delay(Pace);
        }
    }

    /// <summary>
    /// Busca en Deezer por artista y título y, entre los resultados del mismo título y artista (y duración parecida,
    /// ±15 s, si se sabe), pide el BPM de los primeros hasta dar con uno. 0 si Deezer no lo tiene.
    /// </summary>
    private static async Task<uint?> LookupAsync(string title, string artist, double? duration)
    {
        var wantTitle = Norm(Simplify(title));
        var wantArtist = Norm(artist);
        using var search = await GetJsonAsync($"https://api.deezer.com/search?limit=15&q={Uri.EscapeDataString($"{artist} {Simplify(title)}")}");
        if (!search.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return 0;
        var candidates = data.EnumerateArray()
            .Select(t => (
                id: t.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                title: Norm(Str(t, "title_short") ?? Str(t, "title") ?? ""),
                artist: t.TryGetProperty("artist", out var a) ? Norm(Str(a, "name") ?? "") : "",
                dur: t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : (double?)null))
            .Where(c => c.id != 0 && c.title.Length > 0 && (c.title == wantTitle || c.title.Contains(wantTitle) || wantTitle.Contains(c.title)))
            .Where(c => wantArtist.Length == 0 || c.artist.Contains(wantArtist) || wantArtist.Contains(c.artist))
            .Select(c => (c.id, diff: duration is { } want && c.dur is { } got ? Math.Abs(want - got) : 0))
            .Where(c => c.diff <= 15)
            .OrderBy(c => c.diff)
            .Take(3)
            .ToList();
        foreach (var (id, _) in candidates)
        {
            using var track = await GetJsonAsync($"https://api.deezer.com/track/{id}");
            if (track.RootElement.TryGetProperty("bpm", out var bpm) && bpm.ValueKind == JsonValueKind.Number && bpm.GetDouble() is var v && v is > 20 and < 400)
                return (uint)Math.Round(v);
        }
        return 0;
    }

    /// <summary>GET a Deezer. Su error de cuota (código 4) llega con 200: se trata como fallo para no apuntar «no lo tiene».</summary>
    private static async Task<JsonDocument> GetJsonAsync(string url)
    {
        using var resp = await Http.Value.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err)
            && err.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() is 4 or 700)
        {
            doc.Dispose();
            throw new HttpRequestException("Deezer está saturado");
        }
        return doc;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Solo letras y números, en minúsculas.</summary>
    private static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>El título sin lo que va entre paréntesis o corchetes («(Remastered)», «[Live]»…).</summary>
    private static string Simplify(string title)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(title, @"\([^)]*\)|\[[^\]]*\]", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 0 ? s : title;
    }

    private void Save()
    {
        string json;
        lock (_lock)
        {
            if (_unsaved == 0) return;
            _unsaved = 0;
            json = JsonSerializer.Serialize(_cache);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
        catch
        {
            // se guardará la próxima vez
        }
    }
}
