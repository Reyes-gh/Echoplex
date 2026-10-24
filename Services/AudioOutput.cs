using System.Runtime.InteropServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Echoplex.Services;

public sealed record OutputDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Salida de audio con WASAPI (NAudio + decodificadores de Media Foundation).
/// • Modo normal: salida compartida por el dispositivo predeterminado; Windows mezcla y convierte.
/// • Modo bit a bit: WASAPI exclusivo por el dispositivo elegido, sin mezclar, sin remuestrear y sin
///   volumen digital. Si el dispositivo solo acepta contenedores más anchos (24/32 bits) las muestras
///   se alinean a la izquierda rellenando con ceros, lo que no altera la señal.
/// </summary>
public sealed class AudioOutput : IDisposable, IMMNotificationClient
{
    private const int ExclusiveNotAllowed = unchecked((int)0x8889000E);
    private const int DeviceInUse = unchecked((int)0x8889000A);
    private const int DeviceInvalidated = unchecked((int)0x88890004);

    private readonly Dispatcher _ui;
    private readonly object _lock = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private WaveStream? _reader;
    private PcmProvider? _provider;
    private MMDevice? _device;
    private WasapiOut? _out;
    private bool _exclusive;
    private bool _stopRequested;
    private float _volume = 1f;
    private bool _muted;
    private string _deviceName = "";
    private Func<WaveStream>? _open;
    private volatile bool _seeking;
    private TimeSpan _seekTarget;
    private int _seekVersion;
    // siguiente pista ya abierta (sin pausas entre canciones): la lectura de audio salta a ella al acabar la actual
    private WaveStream? _nextReader;
    private Func<WaveStream>? _nextOpen;
    private object? _nextTag;
    private string? _nextPath;
    private double _nextGain;
    private double _nextFade;      // segundos de fundido con la preparada (0 = enlace sin pausa)
    private double _currentGain;   // normalización de la que suena (para igualar la que entra en el fundido)
    // la que suena (para la medida de volumen y tempo) y su medida, si la está sonando entera desde el principio
    private string? _path;
    private SongMeters? _meter;

    /// <summary>
    /// ¿Medir también el tempo de esta canción mientras suena? (se pregunta al empezar cada una; null = nunca). Solo las que
    /// lo necesitan: columna BPM a la vista, su ajuste encendido y aún sin calcular.
    /// </summary>
    public Func<string, bool>? WantsBpm { get; set; }

    /// <summary>Lo que se mide de la canción mientras suena entera: su volumen y, si se pide, su tempo.</summary>
    private sealed class SongMeters(int sampleRate, int channels, bool bpm)
    {
        public LoudnessMeter Loudness { get; } = new(sampleRate, channels);
        public BpmEstimator? Bpm { get; } = bpm ? new(sampleRate, channels) : null;

        public void Add(ReadOnlySpan<float> samples)
        {
            Loudness.Add(samples);
            Bpm?.Add(samples);
        }
    }

    public AudioOutput(Dispatcher ui)
    {
        _ui = ui;
        try { _enumerator.RegisterEndpointNotificationCallback(this); } catch { }
    }

    public event Action? Ended;
    /// <summary>La salida enlazó sin pausa con la pista preparada (su etiqueta); la anterior ya terminó.</summary>
    public event Action<object>? Advanced;
    public event Action<string>? Failed;
    public event Action<bool>? PlayingChanged;
    /// <summary>Una canción sonó entera de principio a fin en la salida normal: su sonoridad (LUFS), pico y duración.</summary>
    public event Action<string, double, double, double>? Measured;
    /// <summary>Una canción sonó entera de principio a fin en la salida normal: su tempo calculado.</summary>
    public event Action<string, BpmEstimate>? BpmMeasured;
    /// <summary>El dispositivo desapareció (p. ej. se apagaron los cascos Bluetooth).</summary>
    public event Action? DeviceLost;
    /// <summary>Windows cambió la salida predeterminada.</summary>
    public event Action? DefaultDeviceChanged;

    public bool IsLoaded => _reader != null;
    public bool IsExclusive => _exclusive && IsLoaded;
    public bool IsPlaying { get; private set; }
    public string DeviceName => _deviceName;
    public string FormatText { get; private set; } = "";

    public TimeSpan Position
    {
        get
        {
            if (_seeking) return _seekTarget; // mientras salta, no bloquear la interfaz ni volver atrás
            lock (_lock) return _reader?.CurrentTime ?? TimeSpan.Zero;
        }
    }

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    public static List<OutputDevice> ListDevices()
    {
        var en = new MMDeviceEnumerator();
        string? def = null;
        try { def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { }
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new OutputDevice(d.ID, d.FriendlyName, d.ID == def))
            .ToList();
    }

    /// <summary>
    /// Prepara el archivo (abrirlo es lo que hace que OneDrive lo descargue si está en la nube).
    /// Devuelve null si está listo, o el motivo por el que no se puede.
    /// </summary>
    /// <param name="gainDb">Normalización de volumen de esta canción (solo en la salida normal).</param>
    public Task<string?> LoadAsync(string path, string? deviceId, bool exclusive, double gainDb = 0)
    {
        // cada carga lleva su número: si mientras abre el archivo (p. ej. bajando de OneDrive) se pide otra,
        // esta ya no debe instalarse ni cerrar lo que haya cargado la nueva
        int ticket = Interlocked.Increment(ref _loadTicket);
        _loadGain = gainDb;
        return Task.Run(() => Load(path, deviceId, exclusive, ticket));
    }

    private double _loadGain;

    /// <summary>Cambia la normalización de la que suena (al cambiar de modo en Ajustes): se oye en unos milisegundos.</summary>
    public void SetGain(double gainDb)
    {
        lock (_lock) _provider?.Eq?.SetGain(gainDb, immediate: false);
    }

    /// <summary>Abre un archivo para medirlo (decodificado como al reproducirlo).</summary>
    public static WaveStream OpenForAnalysis(string path) => OpenFile(path);

    /// <summary>Abre un flujo para medirlo (decodificado como al reproducir las de carpetas en línea; lo cierra al terminar).</summary>
    public static WaveStream OpenForAnalysis(Stream stream) => new OwningStreamReader(stream);

    private int _loadTicket;
    private const string Superseded = "sustituida por otra carga";

    private string? Load(string path, string? deviceId, bool exclusive, int ticket)
    {
        if (ticket != Volatile.Read(ref _loadTicket)) return Superseded;
        Unload();
        MMDevice device;
        try
        {
            // Los objetos de audio de Windows se usan siempre desde hilos de fondo (MTA).
            var en = new MMDeviceEnumerator();
            device = exclusive && deviceId != null
                ? en.GetDevice(deviceId)
                : en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device.State != DeviceState.Active) return $"«{device.FriendlyName}» no está conectado";
        }
        catch
        {
            return exclusive ? "el dispositivo elegido ya no está disponible" : "no hay ninguna salida de audio activa";
        }

        WaveStream Open() => OpenFile(path);

        WaveStream reader;
        try
        {
            reader = Open();
            _open = Open;
            if (OnlineLibrary.IsOnline(path)) OnlineLibrary.Focus(path); // la que suena: las demás en línea dejan de bajar
        }
        catch (Exception ex)
        {
            return "no se pudo abrir el archivo (" + ex.Message + ")";
        }

        var src = reader.WaveFormat;
        var srcText = $"{src.BitsPerSample} bits / {src.SampleRate / 1000.0:0.#} kHz";

        if (!exclusive)
        {
            if (!Commit(reader, device, reader.WaveFormat, false, ticket, path)) return Superseded;
            FormatText = srcText;
            return null;
        }

        if (src.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.Extensible) || src.BitsPerSample is not (16 or 24 or 32))
        {
            reader.Dispose();
            return $"formato decodificado no compatible ({src.Encoding} {src.BitsPerSample} bits)";
        }

        // Formatos sin pérdida, del más fiel al más ancho. Muchas tarjetas (Realtek) solo aceptan 24 bits
        // dentro de un contenedor de 32 ("24 en 32"), no empaquetados en 3 bytes ni 32 bits completos.
        var candidates = new List<WaveFormat> { new WaveFormatExtensible(src.SampleRate, src.BitsPerSample, src.Channels) };
        if (src.BitsPerSample == 16) candidates.Add(new WaveFormatExtensible(src.SampleRate, 24, src.Channels));
        if (src.BitsPerSample <= 24) candidates.Add(new Pcm24In32(src.SampleRate, src.Channels));
        if (src.BitsPerSample < 32) candidates.Add(new WaveFormatExtensible(src.SampleRate, 32, src.Channels));
        foreach (var fmt in candidates)
        {
            bool ok;
            try
            {
                ok = device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, fmt);
            }
            catch (COMException ex) when (ex.HResult == ExclusiveNotAllowed)
            {
                reader.Dispose();
                return $"el modo exclusivo está desactivado para «{device.FriendlyName}» en Windows";
            }
            catch (COMException ex) when (ex.HResult == DeviceInUse)
            {
                reader.Dispose();
                return $"otro programa está usando «{device.FriendlyName}» en exclusivo";
            }
            if (!ok) continue;

            if (!Commit(reader, device, fmt, true, ticket)) return Superseded;
            FormatText = fmt.BitsPerSample == src.BitsPerSample ? srcText : $"{srcText} en contenedor de {fmt.BitsPerSample} bits";
            return null;
        }

        reader.Dispose();
        return $"«{device.FriendlyName}» no admite {srcText} en exclusivo";
    }

    private static WaveStream OpenFile(string path)
    {
        // carpeta en línea: se lee de la nube mientras suena (en memoria; nada se guarda en el disco)
        if (OnlineLibrary.IsOnline(path)) return new OwningStreamReader(OnlineLibrary.OpenStream(path));
        var clean = path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ? FlacSanitizer.TryOpen(path) : null;
        return clean != null ? new OwningStreamReader(clean) : new MediaFoundationReader(path);
    }

    /// <summary>Lector de un flujo que además lo cierra al terminar (el de NAudio deja abierto el archivo).</summary>
    private sealed class OwningStreamReader : StreamMediaFoundationReader
    {
        private readonly Stream _stream;

        public OwningStreamReader(Stream stream) : base(stream) => _stream = stream;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _stream.Dispose();
        }
    }

    private static bool SameFormat(WaveFormat a, WaveFormat b) =>
        a.Encoding == b.Encoding && a.SampleRate == b.SampleRate && a.Channels == b.Channels && a.BitsPerSample == b.BitsPerSample;

    /// <summary>
    /// Abre ya la siguiente pista para enlazarla sin pausa cuando acabe la actual. Solo si decodifica al mismo
    /// formato (frecuencia, bits, canales): la salida sigue abierta con el formato de la primera y en bit a bit no
    /// se puede convertir sin perder la fidelidad. Devuelve false si no se puede (se cambiará de pista como siempre).
    /// </summary>
    /// <param name="crossfadeSeconds">Fundido con la que acaba (0 = enlazada sin pausa, como dentro de un disco).</param>
    public Task<bool> PrepareNextAsync(string path, object tag, double gainDb = 0, double crossfadeSeconds = 0) => Task.Run(() =>
    {
        WaveFormat? current;
        lock (_lock) current = _reader?.WaveFormat;
        if (current == null) return false;
        WaveStream next;
        try
        {
            next = OpenFile(path);
        }
        catch
        {
            return false;
        }
        lock (_lock)
        {
            if (_reader == null || !SameFormat(next.WaveFormat, _reader.WaveFormat))
            {
                next.Dispose();
                return false;
            }
            _nextReader?.Dispose();
            _nextReader = next;
            _nextOpen = () => OpenFile(path);
            _nextTag = tag;
            _nextPath = path;
            _nextGain = gainDb;
            _nextFade = _exclusive ? 0 : crossfadeSeconds;
        }
        return true;
    });

    /// <summary>Descarta la pista preparada (cambió la cola, el orden, la repetición…).</summary>
    public void CancelNext()
    {
        lock (_lock)
        {
            _provider?.CancelFade();
            _nextReader?.Dispose();
            _nextReader = null;
            _nextOpen = null;
            _nextTag = null;
        }
    }

    /// <summary>
    /// Desde la lectura de audio, con el cerrojo tomado: la que sonaba llegó al final. Si sonó entera desde el principio,
    /// su medida de volumen se avisa (se guarda para normalizarla la próxima vez), y su tempo.
    /// </summary>
    private void SourceEnded()
    {
        if (_meter is { } m && _path is { } path && m.Loudness.Seconds >= 5)
        {
            double lufs = m.Loudness.Integrated(), peak = m.Loudness.Peak, seconds = m.Loudness.Seconds;
            if (!double.IsNaN(lufs)) _ui.BeginInvoke(() => Measured?.Invoke(path, lufs, peak, seconds));
            // el tempo, fuera del hilo de audio
            if (m.Bpm is { } bpm)
                _ = Task.Run(() =>
                {
                    if (bpm.Estimate() is { } est) _ui.BeginInvoke(() => BpmMeasured?.Invoke(path, est));
                });
        }
        _meter = null;
    }

    /// <summary>Desde la lectura de audio, con el cerrojo tomado: la actual se acabó; pasa a la preparada si la hay.</summary>
    private WaveStream? TakeNext()
    {
        if (_nextReader == null) return null;
        var old = _reader;
        var tag = _nextTag!;
        _reader = _nextReader;
        _open = _nextOpen;
        if (_nextPath != null && OnlineLibrary.IsOnline(_nextPath)) OnlineLibrary.Focus(_nextPath);
        // la siguiente entra con su normalización y empieza a medirse desde su principio
        _provider?.Eq?.SetGain(_nextGain, immediate: false);
        _currentGain = _nextGain;
        _path = _nextPath;
        _meter = _provider?.Eq != null
            ? new SongMeters(_reader.WaveFormat.SampleRate, _reader.WaveFormat.Channels, _path != null && WantsBpm?.Invoke(_path) == true)
            : null;
        _nextPath = null;
        _nextReader = null;
        _nextOpen = null;
        _nextTag = null;
        Interlocked.Increment(ref _seekVersion);
        _seeking = false;
        old?.Dispose();
        _ui.BeginInvoke(() => Advanced?.Invoke(tag));
        return _reader;
    }

    /// <summary>Instala lo cargado, salvo que entretanto se haya pedido otra carga (entonces lo cierra).</summary>
    private bool Commit(WaveStream reader, MMDevice device, WaveFormat format, bool exclusive, int ticket, string? path = null)
    {
        lock (_lock)
        {
            if (ticket != Volatile.Read(ref _loadTicket))
            {
                reader.Dispose();
                return false;
            }
            _reader = reader;
            _device = device;
            _exclusive = exclusive;
            // el ecualizador solo en la salida normal: en bit a bit la señal llega intacta. En la normal se entrega en coma
            // flotante de 32 bits (como mezcla Windows): sin volver a redondear a 16 bits lo que sale del ecualizador
            _provider = exclusive
                ? new PcmProvider(reader, format, _lock, TakeNext, null)
                : new PcmProvider(reader, WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels), _lock, TakeNext,
                    new EqualizerProcessor(format.SampleRate, format.Channels), () => _meter, SourceEnded)
                {
                    // el fundido con la preparada: cuántas tramas y cuánto subir o bajar la que entra para igualarla
                    Crossfade = () => _nextReader is { } next && _nextFade > 0 ? (next, (int)(_nextFade * format.SampleRate), _nextGain - _currentGain) : null,
                    Crossfaded = () => _meter = null, // la que entró con fundido no ha sonado entera: no vale como medida
                };
            _currentGain = exclusive ? 0 : _loadGain;
            _provider.Eq?.SetGain(exclusive ? 0 : _loadGain, immediate: true);
            _path = path;
            _meter = exclusive ? null : new SongMeters(format.SampleRate, format.Channels, path != null && WantsBpm?.Invoke(path) == true);
        }
        try { _deviceName = device.FriendlyName; } catch { _deviceName = "dispositivo de audio"; }
        return true;
    }

    /// <summary>Empieza o reanuda la reproducción (en exclusivo, toma el dispositivo).</summary>
    public Task PlayAsync() => Task.Run(() =>
    {
        if (_provider == null || _device == null) return;
        if (_out != null)
        {
            _out.Play();
            SetPlaying(true);
            return;
        }
        // búfer de 100 ms en la salida normal: lo que cambia (ecualizador, saltos) se oye casi al momento
        var output = new WasapiOut(_device, _exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, true, _exclusive ? 120 : 100);
        output.PlaybackStopped += OnStopped;
        output.Init(_provider);
        _stopRequested = false;
        _out = output;
        ApplyVolume();
        output.Play();
        SetPlaying(true);
    });

    /// <summary>Pausa. En exclusivo además suelta el dispositivo para que el resto de programas vuelvan a sonar.</summary>
    public void Pause()
    {
        if (_exclusive)
        {
            Task.Run(ReleaseDevice);
            return;
        }
        var output = _out;
        Task.Run(() => { try { output?.Pause(); } catch { } });
        SetPlaying(false);
    }

    /// <summary>
    /// Salto exacto a la muestra. El salto de Media Foundation no es fiable (en FLAC cae lejos o vuelve
    /// al principio sin avisar), así que se decodifica y descarta hasta el punto pedido: hacia delante
    /// desde donde está, hacia atrás reabriendo el archivo. Decodificar es muy rápido (~50 ms por minuto).
    /// </summary>
    public void Seek(TimeSpan position)
    {
        _meter = null; // con saltos no ha sonado entera: no vale como medida
        lock (_lock) _provider?.CancelFade();
        var total = Duration;
        if (total > TimeSpan.Zero && position > total - TimeSpan.FromMilliseconds(250)) position = total - TimeSpan.FromMilliseconds(250);
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        int version = Interlocked.Increment(ref _seekVersion);
        _seekTarget = position;
        _seeking = true;

        Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    if (version != _seekVersion || _reader == null || _open == null) return;
                    var reader = _reader;
                    var fmt = reader.WaveFormat;
                    long target = (long)(position.TotalSeconds * fmt.SampleRate) * fmt.BlockAlign;
                    if (target < reader.Position)
                    {
                        var fresh = _open();
                        reader.Dispose();
                        reader = fresh;
                        _reader = fresh;
                        _provider?.SetSource(fresh);
                    }
                    var buffer = new byte[fmt.BlockAlign * 16384];
                    long skip = target - reader.Position;
                    while (skip > 0 && version == _seekVersion)
                    {
                        int n = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, skip));
                        if (n <= 0) break;
                        skip -= n;
                    }
                }
                catch
                {
                    // si falla, sigue sonando desde donde esté
                }
                finally
                {
                    if (version == _seekVersion) _seeking = false;
                }
            }
        });
    }

    /// <summary>Modo normal: volumen de la propia sesión. Bit a bit: volumen del dispositivo (no toca las muestras).</summary>
    public void SetVolume(double volume, bool muted)
    {
        _volume = (float)Math.Clamp(volume, 0, 1);
        _muted = muted;
        Task.Run(ApplyVolume);
    }

    private void ApplyVolume()
    {
        try
        {
            if (_exclusive)
            {
                if (_device == null) return;
                _device.AudioEndpointVolume.MasterVolumeLevelScalar = _volume;
                _device.AudioEndpointVolume.Mute = _muted;
            }
            else if (_out != null)
            {
                var sv = _out.AudioStreamVolume;
                var v = _muted ? 0f : _volume;
                for (int ch = 0; ch < sv.ChannelCount; ch++) sv.SetChannelVolume(ch, v);
            }
        }
        catch
        {
            // sin control de volumen
        }
    }

    public void Unload()
    {
        ReleaseDevice();
        Interlocked.Increment(ref _seekVersion);
        _seeking = false;
        lock (_lock)
        {
            _reader?.Dispose();
            _reader = null;
            _provider = null;
            _open = null;
            _nextReader?.Dispose();
            _nextReader = null;
            _nextOpen = null;
            _nextTag = null;
        }
        FormatText = "";
    }

    private void ReleaseDevice()
    {
        var output = _out;
        _out = null;
        if (output != null)
        {
            _stopRequested = true;
            try { output.Stop(); } catch { }
            try { output.Dispose(); } catch { }
        }
        SetPlaying(false);
    }

    private void SetPlaying(bool playing)
    {
        if (IsPlaying == playing) return;
        IsPlaying = playing;
        _ui.BeginInvoke(() => PlayingChanged?.Invoke(playing));
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopRequested) return;
        var ex = e.Exception;
        _ui.BeginInvoke(() =>
        {
            ReleaseDevice();
            if (ex == null) Ended?.Invoke();
            else if (ex is COMException { HResult: DeviceInvalidated }) DeviceLost?.Invoke();
            else Failed?.Invoke(ex.Message);
        });
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        Unload();
    }

    // ---------- avisos de Windows sobre dispositivos ----------

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) _ui.BeginInvoke(() => DefaultDeviceChanged?.Invoke());
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) { }
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) { }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    /// <summary>PCM de 24 bits válidos en muestras de 32 (WAVEFORMATEXTENSIBLE con wValidBitsPerSample = 24).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private sealed class Pcm24In32 : WaveFormat
    {
        private readonly short _validBits;
        private readonly int _channelMask;
        private readonly Guid _subFormat;

        public Pcm24In32(int sampleRate, int channels) : base(sampleRate, 32, channels)
        {
            waveFormatTag = WaveFormatEncoding.Extensible;
            extraSize = 22;
            _validBits = 24;
            _channelMask = channels switch { 1 => 0x4, 2 => 0x3, _ => (1 << channels) - 1 };
            _subFormat = new Guid("00000001-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_PCM
        }
    }

    /// <summary>
    /// Entrega el PCM tal cual, alineado a un contenedor más ancho (relleno con ceros) o, en la salida normal, en coma
    /// flotante de 32 bits (sin pérdida: 16 y 24 bits caben enteros). Al acabarse el lector, sigue en la misma lectura con
    /// la pista preparada: la salida no se entera y no hay pausa entre canciones.
    /// </summary>
    private sealed class PcmProvider : IWaveProvider
    {
        private WaveStream _source;
        private readonly object _lock;
        private readonly Func<WaveStream?> _takeNext;
        private readonly EqualizerProcessor? _eq;
        private readonly int _inBytes;
        private readonly int _outBytes;
        private readonly bool _inFloat;
        private readonly bool _toFloat;
        private byte[] _buffer = Array.Empty<byte>();

        private readonly Func<SongMeters?>? _meter;
        private readonly Action? _ended;

        /// <summary>La preparada si hay que fundirse con ella: su lector, las tramas del fundido y la diferencia de normalización.</summary>
        public Func<(WaveStream next, int frames, double gainDb)?>? Crossfade { get; init; }
        /// <summary>Se pasó a la siguiente con fundido.</summary>
        public Action? Crossfaded { get; init; }

        private WaveStream? _fadeNext;
        private int _fadeLen, _fadePos;
        private float _fadeScale = 1;
        private byte[] _fadeBuffer = Array.Empty<byte>();

        /// <summary>Un salto o un cambio de la siguiente cortan el fundido; la siguiente vuelve a su principio.</summary>
        public void CancelFade()
        {
            if (_fadeNext == null) return;
            try { _fadeNext.Position = 0; } catch { }
            _fadeNext = null;
        }

        public PcmProvider(WaveStream source, WaveFormat target, object sync, Func<WaveStream?> takeNext, EqualizerProcessor? eq,
            Func<SongMeters?>? meter = null, Action? ended = null)
        {
            _meter = meter;
            _ended = ended;
            _source = source;
            _lock = sync;
            _takeNext = takeNext;
            _eq = eq;
            WaveFormat = target;
            _inBytes = source.WaveFormat.BitsPerSample / 8;
            _outBytes = target.BitsPerSample / 8;
            _inFloat = IsFloat(source.WaveFormat);
            _toFloat = IsFloat(target) && !_inFloat;
        }

        private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

        private static bool IsFloat(WaveFormat f) =>
            f.Encoding == WaveFormatEncoding.IeeeFloat || (f is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);

        public WaveFormat WaveFormat { get; }

        public EqualizerProcessor? Eq => _eq;

        /// <summary>Cambia el lector (tras reabrir el archivo para saltar hacia atrás). Llamar con el cerrojo tomado.</summary>
        public void SetSource(WaveStream source)
        {
            _source = source;
            _eq?.Reset();
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            AudioThread.Register();
            lock (_lock)
            {
                if (_toFloat || _inFloat) StartFadeIfDue();
                int n = ReadSource(_source, buffer, offset, count);
                if (_fadeNext != null)
                {
                    // fundido: la que acaba baja y la siguiente sube (curvas de igual potencia: el volumen no se hunde a la mitad)
                    if (_meter?.Invoke() is { } cm && n > 0)
                        cm.Add(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, n - n % 4)));
                    if (_fadeBuffer.Length < count) _fadeBuffer = new byte[count];
                    int nextBytes = ReadSource(_fadeNext, _fadeBuffer, 0, count);
                    var cur = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count - count % 4));
                    var nxt = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_fadeBuffer.AsSpan(0, nextBytes - nextBytes % 4));
                    int ch = WaveFormat.Channels;
                    int total = Math.Max(n, nextBytes) / 4;
                    for (int i = 0; i < total; i++)
                    {
                        double t = Math.Min(1, (_fadePos + i / ch) / (double)_fadeLen);
                        float a = i < n / 4 ? cur[i] : 0, b = i < nxt.Length ? nxt[i] * _fadeScale : 0;
                        cur[i] = (float)(a * Math.Cos(t * Math.PI / 2) + b * Math.Sin(t * Math.PI / 2));
                    }
                    _fadePos += total / ch;
                    if (n < count || _fadePos >= _fadeLen)
                    {
                        // la que acababa terminó: la siguiente pasa a ser la que suena (ya va por donde iba el fundido)
                        _ended?.Invoke();
                        if (_takeNext() is { } next) _source = next;
                        _fadeNext = null;
                        Crossfaded?.Invoke();
                    }
                    n = total * 4;
                    if (n > 0) _eq?.Process(buffer, offset, n);
                    return n;
                }
                if (n == 0)
                {
                    _ended?.Invoke(); // la que sonaba llegó al final
                    if (_takeNext() is { } next)
                    {
                        _source = next;
                        n = ReadSource(_source, buffer, offset, count);
                    }
                }
                // la medida de volumen y tempo, de lo que trae el archivo (antes del ecualizador y la normalización)
                if (n > 0 && _eq != null && _meter?.Invoke() is { } m)
                    m.Add(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, n - n % 4)));
                // con los ajustes que haya justo ahora: el cambio se oye en este mismo bloque, sin transición
                if (n > 0) _eq?.Process(buffer, offset, n);
                return n;
            }
        }

        /// <summary>¿Quedan ya solo los segundos del fundido? Entonces empieza (con la siguiente ya abierta).</summary>
        private void StartFadeIfDue()
        {
            if (_fadeNext != null || Crossfade?.Invoke() is not { } f || f.frames <= 0) return;
            long left = (_source.Length - _source.Position) / Math.Max(1, _source.WaveFormat.BlockAlign);
            if (left > f.frames || left <= 0) return;
            _fadeNext = f.next;
            _fadeLen = (int)left;
            _fadePos = 0;
            _fadeScale = (float)Math.Pow(10, f.gainDb / 20);
        }

        private int ReadSource(WaveStream source, byte[] buffer, int offset, int count)
        {
            {
                if (_toFloat) return ReadAsFloat(source, buffer, offset, count);
                if (_inBytes == _outBytes) return source.Read(buffer, offset, count);

                int samples = count / _outBytes;
                int need = samples * _inBytes;
                if (_buffer.Length < need) _buffer = new byte[need];
                int read = source.Read(_buffer, 0, need);
                int got = read / _inBytes;
                int pad = _outBytes - _inBytes;
                for (int i = 0; i < got; i++)
                {
                    int o = offset + i * _outBytes;
                    for (int z = 0; z < pad; z++) buffer[o + z] = 0;
                    Buffer.BlockCopy(_buffer, i * _inBytes, buffer, o + pad, _inBytes);
                }
                return got * _outBytes;
            }
        }

        /// <summary>PCM entero (16, 24 o 32 bits) a coma flotante de −1 a 1.</summary>
        private int ReadAsFloat(WaveStream source, byte[] buffer, int offset, int count)
        {
            int samples = count / 4;
            int need = samples * _inBytes;
            if (_buffer.Length < need) _buffer = new byte[need];
            int got = source.Read(_buffer, 0, need) / _inBytes;
            var dst = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, got * 4));
            var src = _buffer;
            switch (_inBytes)
            {
                case 2:
                    for (int i = 0, p = 0; i < got; i++, p += 2) dst[i] = (short)(src[p] | src[p + 1] << 8) / 32768f;
                    break;
                case 3:
                    for (int i = 0, p = 0; i < got; i++, p += 3) dst[i] = ((src[p] | src[p + 1] << 8 | src[p + 2] << 16) << 8 >> 8) / 8388608f;
                    break;
                default:
                    for (int i = 0, p = 0; i < got; i++, p += 4) dst[i] = (float)(BitConverter.ToInt32(src, p) / 2147483648.0);
                    break;
            }
            return got * 4;
        }
    }
}

/// <summary>
/// El hilo que entrega el sonido a la tarjeta (el de NAudio, que lee de PcmProvider) se apunta al servicio de audio de
/// Windows (MMCSS, tarea «Pro Audio»): así va por delante de todo lo demás del equipo y no se queda sin turno aunque el
/// procesador esté ocupado. NAudio no lo hace por su cuenta.
/// </summary>
internal static class AudioThread
{
    [ThreadStatic] private static bool _registered;

    public static void Register()
    {
        // una vez por hilo, y nunca uno del pool (se quedaría con la prioridad para otras tareas)
        if (_registered || Thread.CurrentThread.IsThreadPoolThread) return;
        _registered = true;
        try
        {
            int task = 0;
            AvSetMmThreadCharacteristics("Pro Audio", ref task);
        }
        catch { /* sin el servicio (Windows sin audio): sigue con la prioridad normal */ }
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);
}
