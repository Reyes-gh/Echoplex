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

    public AudioOutput(Dispatcher ui)
    {
        _ui = ui;
        try { _enumerator.RegisterEndpointNotificationCallback(this); } catch { }
    }

    public event Action? Ended;
    public event Action<string>? Failed;
    public event Action<bool>? PlayingChanged;
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
    public Task<string?> LoadAsync(string path, string? deviceId, bool exclusive) => Task.Run(() =>
    {
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

        WaveStream Open()
        {
            var clean = path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ? FlacSanitizer.TryOpen(path) : null;
            return clean != null ? new StreamMediaFoundationReader(clean) : new MediaFoundationReader(path);
        }

        WaveStream reader;
        try
        {
            reader = Open();
            _open = Open;
        }
        catch (Exception ex)
        {
            return "no se pudo abrir el archivo (" + ex.Message + ")";
        }

        var src = reader.WaveFormat;
        var srcText = $"{src.BitsPerSample} bits / {src.SampleRate / 1000.0:0.#} kHz";

        if (!exclusive)
        {
            Commit(reader, device, reader.WaveFormat, false);
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

            Commit(reader, device, fmt, true);
            FormatText = fmt.BitsPerSample == src.BitsPerSample ? srcText : $"{srcText} en contenedor de {fmt.BitsPerSample} bits";
            return null;
        }

        reader.Dispose();
        return $"«{device.FriendlyName}» no admite {srcText} en exclusivo";
    });

    private void Commit(WaveStream reader, MMDevice device, WaveFormat format, bool exclusive)
    {
        lock (_lock)
        {
            _reader = reader;
            _device = device;
            _exclusive = exclusive;
            _provider = new PcmProvider(reader, format, _lock);
        }
        try { _deviceName = device.FriendlyName; } catch { _deviceName = "dispositivo de audio"; }
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
        var output = new WasapiOut(_device, _exclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, true, _exclusive ? 120 : 200);
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

    /// <summary>Entrega el PCM tal cual o alineado a un contenedor más ancho (relleno con ceros).</summary>
    private sealed class PcmProvider : IWaveProvider
    {
        private WaveStream _source;
        private readonly object _lock;
        private readonly int _inBytes;
        private readonly int _outBytes;
        private byte[] _buffer = Array.Empty<byte>();

        public PcmProvider(WaveStream source, WaveFormat target, object sync)
        {
            _source = source;
            _lock = sync;
            WaveFormat = target;
            _inBytes = source.WaveFormat.BitsPerSample / 8;
            _outBytes = target.BitsPerSample / 8;
        }

        public WaveFormat WaveFormat { get; }

        /// <summary>Cambia el lector (tras reabrir el archivo para saltar hacia atrás). Llamar con el cerrojo tomado.</summary>
        public void SetSource(WaveStream source) => _source = source;

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                if (_inBytes == _outBytes) return _source.Read(buffer, offset, count);

                int samples = count / _outBytes;
                int need = samples * _inBytes;
                if (_buffer.Length < need) _buffer = new byte[need];
                int read = _source.Read(_buffer, 0, need);
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
    }
}
