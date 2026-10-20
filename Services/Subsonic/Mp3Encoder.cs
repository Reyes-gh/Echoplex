using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Echoplex.Services.Subsonic;

/// <summary>
/// El codificador MP3 que trae Windows (Media Foundation), usado a trozos: entra audio PCM de 16 bits y salen tramas MP3
/// según se van haciendo, sin esperar a tener la canción entera. Así el móvil empieza a escuchar al momento y fuera de
/// casa gasta una fracción de lo que ocupa un FLAC. Sin programas aparte (nada de ffmpeg).
/// </summary>
public sealed class Mp3Encoder : IDisposable
{
    /// <summary>CLSID_MP3ACMCodecWrapper: el codificador MP3 de Windows 8 en adelante.</summary>
    private static readonly Guid Mp3EncoderClsid = new("11103421-354C-4CCA-A7A3-1AFF9A5B6701");
    private const int NeedMoreInput = unchecked((int)0xC00D6D72), NotAccepting = unchecked((int)0xC00D36B5);

    /// <summary>Las tasas de bits que admite MP3 (MPEG-1 capa III), en kbps.</summary>
    public static readonly int[] Bitrates = { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };

    private readonly IMFTransform _mft;
    private readonly WaveFormat _input;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private long _time;

    public int Kbps { get; }

    /// <param name="sampleRate">32000, 44100 o 48000.</param>
    /// <param name="channels">1 o 2.</param>
    public Mp3Encoder(int sampleRate, int channels, int kbps)
    {
        MediaFoundationApi.Startup();
        Kbps = Bitrates.LastOrDefault(b => b <= kbps, Bitrates[0]);
        _input = new WaveFormat(sampleRate, 16, channels);
        var type = Type.GetTypeFromCLSID(Mp3EncoderClsid) ?? throw new NotSupportedException("Este Windows no trae el codificador MP3");
        _mft = (IMFTransform)(Activator.CreateInstance(type) ?? throw new NotSupportedException("No se pudo crear el codificador MP3"));
        // en los codificadores, primero la salida y luego la entrada
        _mft.SetOutputType(0, OutputType(sampleRate, channels, Kbps), _MFT_SET_TYPE_FLAGS.None);
        _mft.SetInputType(0, MediaFoundationApi.CreateMediaTypeFromWaveFormat(_input), _MFT_SET_TYPE_FLAGS.None);
        _mft.GetOutputStreamInfo(0, out var info);
        _providesSamples = (info.dwFlags & (_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | _MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        _outputSize = Math.Max(info.cbSize, 64 * 1024);
        _mft.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        _mft.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
    }

    /// <summary>De los tipos de salida que ofrece el codificador, el de esa tasa (o se pide a mano si no lo lista).</summary>
    private IMFMediaType OutputType(int sampleRate, int channels, int kbps)
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType candidate;
            try
            {
                _mft.GetOutputAvailableType(0, i, out candidate);
            }
            catch (COMException)
            {
                break; // no hay más
            }
            var mt = new MediaType(candidate);
            if (mt.SubType == AudioSubtypes.MFAudioFormat_MP3 && mt.SampleRate == sampleRate && mt.ChannelCount == channels && mt.AverageBytesPerSecond == kbps * 125)
                return candidate;
            Marshal.ReleaseComObject(candidate);
        }
        var type = MediaFoundationApi.CreateMediaType();
        type.SetGUID(MediaFoundationAttributes.MF_MT_MAJOR_TYPE, MediaTypes.MFMediaType_Audio);
        type.SetGUID(MediaFoundationAttributes.MF_MT_SUBTYPE, AudioSubtypes.MFAudioFormat_MP3);
        type.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate);
        type.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_NUM_CHANNELS, channels);
        type.SetUINT32(MediaFoundationAttributes.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, kbps * 125);
        return type;
    }

    /// <summary>Codifica <paramref name="count"/> bytes de PCM; lo que ya salió en MP3 va a <paramref name="output"/>.</summary>
    public void Encode(byte[] pcm, int count, Action<byte[], int> output)
    {
        if (count <= 0) return;
        var buffer = MediaFoundationApi.CreateMemoryBuffer(count);
        buffer.Lock(out var ptr, out _, out _);
        Marshal.Copy(pcm, 0, ptr, count);
        buffer.Unlock();
        buffer.SetCurrentLength(count);
        var sample = MediaFoundationApi.CreateSample();
        sample.AddBuffer(buffer);
        long duration = count * 10_000_000L / _input.AverageBytesPerSecond;
        sample.SetSampleTime(_time);
        sample.SetSampleDuration(duration);
        _time += duration;
        try
        {
            while (true)
            {
                try
                {
                    _mft.ProcessInput(0, sample, 0);
                    break;
                }
                catch (COMException e) when (e.HResult == NotAccepting)
                {
                    Drain(output); // lleno: primero hay que sacar lo que tiene
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
            Marshal.ReleaseComObject(buffer);
        }
        Drain(output);
    }

    /// <summary>Fin de la canción: saca lo que queda dentro del codificador.</summary>
    public void Finish(Action<byte[], int> output)
    {
        _mft.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
        _mft.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, IntPtr.Zero);
        Drain(output);
    }

    private void Drain(Action<byte[], int> output)
    {
        while (true)
        {
            IMFSample? sample = null;
            IMFMediaBuffer? buffer = null;
            if (!_providesSamples)
            {
                sample = MediaFoundationApi.CreateSample();
                buffer = MediaFoundationApi.CreateMemoryBuffer(_outputSize);
                sample.AddBuffer(buffer);
            }
            var data = new[] { new MFT_OUTPUT_DATA_BUFFER { dwStreamID = 0, pSample = sample! } };
            int hr = _mft.ProcessOutput(_MFT_PROCESS_OUTPUT_FLAGS.None, 1, data, out _);
            try
            {
                if (hr == NeedMoreInput) return;
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                var outSample = data[0].pSample;
                if (outSample == null) return;
                outSample.ConvertToContiguousBuffer(out var contiguous);
                try
                {
                    contiguous.Lock(out var ptr, out _, out var length);
                    try
                    {
                        if (length > 0)
                        {
                            var bytes = new byte[length];
                            Marshal.Copy(ptr, bytes, 0, length);
                            output(bytes, length);
                        }
                    }
                    finally
                    {
                        contiguous.Unlock();
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(contiguous);
                }
            }
            finally
            {
                if (buffer != null) Marshal.ReleaseComObject(buffer);
                if (data[0].pSample != null) Marshal.ReleaseComObject(data[0].pSample);
            }
        }
    }

    public void Dispose()
    {
        try { _mft.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero); } catch { }
        Marshal.ReleaseComObject(_mft);
    }
}
