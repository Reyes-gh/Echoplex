using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Echoplex.Services;

/// <summary>
/// Controles multimedia del sistema (SystemMediaTransportControls) llamados directamente por su ABI
/// de WinRT, sin la proyección completa del SDK de Windows (~24 MB). Da el recuadro de Windows con la
/// canción y la portada, las teclas multimedia y los botones de auriculares Bluetooth.
/// </summary>
public sealed unsafe class MediaControls : IDisposable
{
    public const int ButtonPlay = 0, ButtonPause = 1, ButtonStop = 2, ButtonNext = 6, ButtonPrevious = 7;
    public const int StatusStopped = 2, StatusPlaying = 3, StatusPaused = 4, StatusChanging = 1;

    // Posiciones en la vtable: IUnknown (3) + IInspectable (3) = 6 antes de los métodos propios.
    private const int SmtcPutPlaybackStatus = 7, SmtcGetDisplayUpdater = 8, SmtcPutIsEnabled = 11,
        SmtcPutIsPlayEnabled = 13, SmtcPutIsStopEnabled = 15, SmtcPutIsPauseEnabled = 17,
        SmtcPutIsPreviousEnabled = 25, SmtcPutIsNextEnabled = 27, SmtcAddButtonPressed = 32, SmtcRemoveButtonPressed = 33;
    private const int UpdPutType = 7, UpdPutThumbnail = 11, UpdGetMusicProperties = 12, UpdClearAll = 16, UpdUpdate = 17;
    private const int MusicPutTitle = 7, MusicPutArtist = 11, Music2PutAlbumTitle = 7;
    private const int ArgsGetButton = 6;
    private const int StreamRefCreateFromStream = 8;

    private static readonly Guid IidInterop = new("DDB0472D-C911-4A1F-86D9-DC3D71A95F5A");
    private static readonly Guid IidSmtc = new("99FA3FF4-1742-42A6-902E-087D41F965EC");
    private static readonly Guid IidMusic2 = new("00368462-97D3-44B9-B00F-008AFCEFAF18");
    private static readonly Guid IidStreamRefStatics = new("857309DC-3FBF-4E7D-986F-EF3B1A07A964");
    private static readonly Guid IidRandomAccessStream = new("905A0FE1-BC53-11DF-8C49-001E4FC686DA");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidAgile = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");

    /// <summary>IID de TypedEventHandler&lt;SystemMediaTransportControls, …ButtonPressedEventArgs&gt;.</summary>
    public static readonly Guid IidButtonHandler = ParameterizedIid(
        "pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};" +
        "rc(Windows.Media.SystemMediaTransportControls;{99fa3ff4-1742-42a6-902e-087d41f965ec});" +
        "rc(Windows.Media.SystemMediaTransportControlsButtonPressedEventArgs;{b7f47116-a56f-4dc8-9e11-92031f4a87c2}))");

    private static MediaControls? s_instance;
    private static IntPtr s_handler;

    private IntPtr _smtc;
    private long _token;

    public event Action<int>? ButtonPressed;

    public bool IsAvailable => _smtc != IntPtr.Zero;

    /// <summary>Último HRESULT de error (para diagnóstico).</summary>
    public int LastError { get; private set; }

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("shlwapi.dll")]
    private static extern IntPtr SHCreateMemStream(byte[] data, uint size);

    [DllImport("shcore.dll")]
    private static extern int CreateRandomAccessStreamOverStream(IntPtr stream, int options, in Guid riid, out IntPtr ras);

    /// <summary>Engancha los controles a la ventana principal. Llamar desde el hilo de la interfaz.</summary>
    public bool Attach(IntPtr hwnd)
    {
        if (_smtc != IntPtr.Zero || hwnd == IntPtr.Zero) return IsAvailable;
        IntPtr factory = IntPtr.Zero;
        try
        {
            factory = Activate("Windows.Media.SystemMediaTransportControls", IidInterop);
            if (factory == IntPtr.Zero) return false;
            // ISystemMediaTransportControlsInterop::GetForWindow(HWND, REFIID, void**)
            var getForWindow = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(factory, 6);
            IntPtr smtc;
            Guid iid = IidSmtc;
            if (!Ok(getForWindow(factory, hwnd, &iid, &smtc))) return false;
            _smtc = smtc;

            PutBool(_smtc, SmtcPutIsEnabled, true);
            PutBool(_smtc, SmtcPutIsPlayEnabled, true);
            PutBool(_smtc, SmtcPutIsPauseEnabled, true);
            PutBool(_smtc, SmtcPutIsStopEnabled, true);
            PutBool(_smtc, SmtcPutIsNextEnabled, true);
            PutBool(_smtc, SmtcPutIsPreviousEnabled, true);

            s_instance = this;
            if (s_handler == IntPtr.Zero) s_handler = CreateHandler();
            long token;
            var add = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(_smtc, SmtcAddButtonPressed);
            if (Ok(add(_smtc, s_handler, &token))) _token = token;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseCom(factory);
        }
    }

    public void SetStatus(int status)
    {
        if (_smtc == IntPtr.Zero) return;
        var put = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(_smtc, SmtcPutPlaybackStatus);
        Ok(put(_smtc, status));
    }

    /// <summary>Título, artista, álbum y portada (bytes de imagen, opcional).</summary>
    public void SetTrack(string title, string artist, string album, byte[]? cover)
    {
        if (_smtc == IntPtr.Zero) return;
        IntPtr updater = IntPtr.Zero, music = IntPtr.Zero, music2 = IntPtr.Zero;
        try
        {
            var getUpd = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(_smtc, SmtcGetDisplayUpdater);
            IntPtr u;
            if (!Ok(getUpd(_smtc, &u))) return;
            updater = u;

            Call(updater, UpdClearAll);
            var putType = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(updater, UpdPutType);
            Ok(putType(updater, 1)); // MediaPlaybackType.Music

            var getMusic = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(updater, UpdGetMusicProperties);
            IntPtr m;
            if (Ok(getMusic(updater, &m)))
            {
                music = m;
                PutString(music, MusicPutTitle, title);
                PutString(music, MusicPutArtist, artist);
                if (Marshal.QueryInterface(music, in IidMusic2, out var m2) == 0)
                {
                    music2 = m2;
                    PutString(music2, Music2PutAlbumTitle, album);
                }
            }

            if (cover is { Length: > 0 }) SetThumbnail(updater, cover);
            Call(updater, UpdUpdate);
        }
        catch
        {
            // los controles del sistema son opcionales
        }
        finally
        {
            ReleaseCom(music2);
            ReleaseCom(music);
            ReleaseCom(updater);
        }
    }

    private void SetThumbnail(IntPtr updater, byte[] bytes)
    {
        IntPtr stream = IntPtr.Zero, ras = IntPtr.Zero, statics = IntPtr.Zero, reference = IntPtr.Zero;
        try
        {
            stream = SHCreateMemStream(bytes, (uint)bytes.Length);
            if (stream == IntPtr.Zero) return;
            if (!Ok(CreateRandomAccessStreamOverStream(stream, 0, in IidRandomAccessStream, out ras))) return;
            statics = Activate("Windows.Storage.Streams.RandomAccessStreamReference", IidStreamRefStatics);
            if (statics == IntPtr.Zero) return;
            var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, StreamRefCreateFromStream);
            IntPtr r;
            if (!Ok(create(statics, ras, &r))) return;
            reference = r;
            var put = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(updater, UpdPutThumbnail);
            Ok(put(updater, reference));
        }
        finally
        {
            ReleaseCom(reference);
            ReleaseCom(statics);
            ReleaseCom(ras);
            ReleaseCom(stream);
        }
    }

    public void Dispose()
    {
        if (_smtc == IntPtr.Zero) return;
        try
        {
            if (_token != 0)
            {
                var remove = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(_smtc, SmtcRemoveButtonPressed);
                remove(_smtc, _token);
            }
        }
        catch
        {
            // cerrando
        }
        ReleaseCom(_smtc);
        _smtc = IntPtr.Zero;
        if (s_instance == this) s_instance = null;
    }

    // ---------- utilidades ABI ----------

    private bool Ok(int hr)
    {
        if (hr < 0) LastError = hr;
        return hr >= 0;
    }

    private static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    private void Call(IntPtr obj, int slot)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(obj, slot);
        Ok(fn(obj));
    }

    private void PutBool(IntPtr obj, int slot, bool value)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Slot(obj, slot);
        Ok(fn(obj, value ? (byte)1 : (byte)0));
    }

    private void PutString(IntPtr obj, int slot, string value)
    {
        if (!Ok(WindowsCreateString(value, value.Length, out var h))) return;
        try
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(obj, slot);
            Ok(fn(obj, h));
        }
        finally
        {
            WindowsDeleteString(h);
        }
    }

    private static void ReleaseCom(IntPtr obj)
    {
        if (obj != IntPtr.Zero) Marshal.Release(obj);
    }

    private IntPtr Activate(string className, Guid iid)
    {
        if (!Ok(WindowsCreateString(className, className.Length, out var h))) return IntPtr.Zero;
        try
        {
            return Ok(RoGetActivationFactory(h, in iid, out var factory)) ? factory : IntPtr.Zero;
        }
        finally
        {
            WindowsDeleteString(h);
        }
    }

    /// <summary>IID de un tipo genérico de WinRT (UUID v5 sobre la firma, según la especificación de WinRT).</summary>
    public static Guid ParameterizedIid(string signature)
    {
        static byte[] BigEndian(Guid g)
        {
            var b = g.ToByteArray();
            Array.Reverse(b, 0, 4);
            Array.Reverse(b, 4, 2);
            Array.Reverse(b, 6, 2);
            return b;
        }

        var ns = BigEndian(new Guid("11f47ad5-7b73-42c0-abae-878b1e16adee"));
        var data = ns.Concat(Encoding.UTF8.GetBytes(signature)).ToArray();
        var hash = SHA1.HashData(data);
        var g = new byte[16];
        Array.Copy(hash, g, 16);
        g[6] = (byte)((g[6] & 0x0F) | 0x50);
        g[8] = (byte)((g[8] & 0x3F) | 0x80);
        Array.Reverse(g, 0, 4);
        Array.Reverse(g, 4, 2);
        Array.Reverse(g, 6, 2);
        return new Guid(g);
    }

    // ---------- objeto COM mínimo para el evento ButtonPressed ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct HandlerObject
    {
        public IntPtr Vtbl;
        public int RefCount;
    }

    private static IntPtr CreateHandler()
    {
        var vtbl = (IntPtr*)Marshal.AllocHGlobal(sizeof(IntPtr) * 4);
        vtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&HandlerQueryInterface;
        vtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&HandlerAddRef;
        vtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&HandlerRelease;
        vtbl[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&HandlerInvoke;
        var obj = (HandlerObject*)Marshal.AllocHGlobal(sizeof(HandlerObject));
        obj->Vtbl = (IntPtr)vtbl;
        obj->RefCount = 1; // vive mientras dure la aplicación
        return (IntPtr)obj;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int HandlerQueryInterface(IntPtr self, Guid* iid, IntPtr* ppv)
    {
        if (*iid == IidUnknown || *iid == IidAgile || *iid == IidButtonHandler)
        {
            *ppv = self;
            Interlocked.Increment(ref ((HandlerObject*)self)->RefCount);
            return 0;
        }
        *ppv = IntPtr.Zero;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint HandlerAddRef(IntPtr self) => (uint)Interlocked.Increment(ref ((HandlerObject*)self)->RefCount);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint HandlerRelease(IntPtr self)
    {
        var n = Interlocked.Decrement(ref ((HandlerObject*)self)->RefCount);
        return (uint)Math.Max(n, 1);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int HandlerInvoke(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            int button;
            var get = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(args, ArgsGetButton);
            if (get(args, &button) >= 0) s_instance?.ButtonPressed?.Invoke(button);
        }
        catch
        {
            // nunca propagar excepciones a Windows
        }
        return 0;
    }
}
