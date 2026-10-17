using System.Text.Json;
using Echoplex.Models;

namespace Echoplex.Services;

public sealed class CachedMeta
{
    public string? T { get; set; }   // título
    public string? A { get; set; }   // artista
    public string? B { get; set; }   // álbum
    public uint N { get; set; }      // pista
    public long Ms { get; set; }     // duración
    public string? AA { get; set; }  // artista del álbum
    public uint D { get; set; }      // disco
    public uint Y { get; set; }      // año
    public string? G { get; set; }   // género
    // ReplayGain de las etiquetas (dB y picos lineales); no se escriben si no hay
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? RT { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? RA { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? PT { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? PA { get; set; }
    /// <summary>
    /// Con qué versión de la lectura se guardó. Las de antes de <see cref="Current"/> se usan igual (la biblioteca sale al
    /// momento) pero se vuelven a leer en segundo plano para completar lo que no guardaban.
    /// </summary>
    public int V { get; set; }
    public const int Current = 2;
    public long Len { get; set; }
    public long Ticks { get; set; }
}

/// <summary>Caché en disco de etiquetas leídas, para no releer miles de archivos en cada arranque.</summary>
public sealed class MetadataCache
{
    private readonly string _file;
    private readonly object _lock = new();
    private Dictionary<string, CachedMeta> _map = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public MetadataCache(string file) => _file = file;

    public void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            using var fs = File.OpenRead(_file);
            var data = JsonSerializer.Deserialize<Dictionary<string, CachedMeta>>(fs);
            if (data != null)
                lock (_lock) _map = new Dictionary<string, CachedMeta>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // caché corrupta: se regenera
        }
    }

    public bool TryGet(Song s, out CachedMeta meta)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(s.Path, out var m) && m.Len == s.Length && m.Ticks == s.LastWriteTicks)
            {
                meta = m;
                return true;
            }
        }
        meta = null!;
        return false;
    }

    public void Put(Song s, CachedMeta meta)
    {
        meta.Len = s.Length;
        meta.Ticks = s.LastWriteTicks;
        lock (_lock)
        {
            _map[s.Path] = meta;
            _dirty = true;
        }
    }

    private readonly object _writeLock = new();

    public void Save()
    {
        // de uno en uno: el guardado al cerrar espera al periódico si está en marcha (comparten el .tmp)
        lock (_writeLock)
        {
            Dictionary<string, CachedMeta> copy;
            lock (_lock)
            {
                if (!_dirty) return;
                copy = new Dictionary<string, CachedMeta>(_map);
                _dirty = false;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(copy));
                File.Move(tmp, _file, true);
            }
            catch
            {
                lock (_lock) _dirty = true;
            }
        }
    }
}
