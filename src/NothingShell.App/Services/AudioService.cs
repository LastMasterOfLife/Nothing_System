using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace NothingShell.App.Services;

/// <summary>
/// Volume dell'uscita audio predefinita con Core Audio (docs/10-live-activities.md): Windows avvisa di ogni
/// cambio (tasti, cursore delle impostazioni rapide, app), e i tasti del volume intercettati dalla shell
/// passano da qui. Se cambia il dispositivo predefinito (cuffie collegate) si segue il nuovo.
/// Con <c>capture</c> è il microfono predefinito (pannello rapido, docs/11-agenda-pannello-rapido.md).
/// </summary>
public sealed class AudioService : IDisposable
{
    private const int ERender = 0;
    private const int ECapture = 1;
    private const int EMultimedia = 1;
    private const int ClsCtxAll = 0x17;

    private static readonly Guid EndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly VolumeCallback _volumeCallback;
    private readonly DeviceCallback _deviceCallback;
    private readonly int _flow;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _volume;
    private Guid _context = Guid.Empty;

    public AudioService(bool capture = false)
    {
        _flow = capture ? ECapture : ERender;
        _volumeCallback = new VolumeCallback(this);
        _deviceCallback = new DeviceCallback(this);
    }

    /// <summary>Volume (0–1) e muto cambiati, sul thread UI.</summary>
    public event Action<double, bool>? Changed;

    public void Start()
    {
        if (_enumerator is not null)
            return;
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _enumerator.RegisterEndpointNotificationCallback(_deviceCallback);
            Bind();
        }
        catch (COMException)
        {
            _enumerator = null; // servizio audio non disponibile
        }
    }

    public void Stop()
    {
        Unbind();
        if (_enumerator is not null)
        {
            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
            }
            catch (COMException)
            {
                // il servizio audio è già fermo
            }
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    public void Dispose() => Stop();

    /// <summary>Volume e muto attuali; null senza uscita audio.</summary>
    public (double Level, bool Muted)? Read()
    {
        if (_volume is null)
            return null;
        try
        {
            _volume.GetMasterVolumeLevelScalar(out var level);
            _volume.GetMute(out var muted);
            return (level, muted);
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Come i tasti di Windows: un passo (di solito 2%) e, se era muto, l'audio torna.</summary>
    public void StepUp() => Change(v =>
    {
        v.GetMute(out var muted);
        if (muted)
            v.SetMute(false, ref _context);
        v.VolumeStepUp(ref _context);
    });

    public void StepDown() => Change(v => v.VolumeStepDown(ref _context));

    public void ToggleMute() => Change(v =>
    {
        v.GetMute(out var muted);
        v.SetMute(!muted, ref _context);
    });

    /// <summary>Cursore del pannello rapido: alzando il livello l'audio muto torna.</summary>
    public void SetLevel(double level) => Change(v =>
    {
        v.GetMute(out var muted);
        if (muted && level > 0)
            v.SetMute(false, ref _context);
        v.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), ref _context);
    });

    public void SetMute(bool mute) => Change(v => v.SetMute(mute, ref _context));

    private void Change(Action<IAudioEndpointVolume> action)
    {
        if (_volume is null)
            Bind();
        if (_volume is null)
            return;
        try
        {
            action(_volume);
        }
        catch (COMException)
        {
            Bind(); // il dispositivo è sparito: si riprova col nuovo predefinito al prossimo tasto
        }
    }

    private void Bind()
    {
        Unbind();
        if (_enumerator is null || _enumerator.GetDefaultAudioEndpoint(_flow, EMultimedia, out var device) != 0 || device is null)
            return;
        try
        {
            var iid = EndpointVolumeId;
            if (device.Activate(ref iid, ClsCtxAll, 0, out var endpoint) != 0)
                return;
            _volume = (IAudioEndpointVolume)endpoint;
            _volume.RegisterControlChangeNotify(_volumeCallback);
        }
        catch (COMException)
        {
            _volume = null;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void Unbind()
    {
        if (_volume is null)
            return;
        try
        {
            _volume.UnregisterControlChangeNotify(_volumeCallback);
        }
        catch (COMException)
        {
            // dispositivo già rimosso
        }
        Marshal.ReleaseComObject(_volume);
        _volume = null;
    }

    internal sealed class VolumeCallback(AudioService owner) : IAudioEndpointVolumeCallback
    {
        /// <summary>Su un thread del servizio audio: AUDIO_VOLUME_NOTIFICATION_DATA = Guid, BOOL muto, float volume…</summary>
        public int OnNotify(nint data)
        {
            var muted = Marshal.ReadInt32(data, 16) != 0;
            var level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
            owner._dispatcher.BeginInvoke(() => owner.Changed?.Invoke(Math.Clamp(level, 0, 1), muted));
            return 0;
        }
    }

    internal sealed class DeviceCallback(AudioService owner) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, int newState) => 0;

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
        {
            if (flow == owner._flow && role == EMultimedia)
                owner._dispatcher.BeginInvoke(owner.Bind);
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);

        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        void RegisterEndpointNotificationCallback(IMMNotificationClient client);

        void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

        void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

        void GetChannelCount(out uint count);

        void SetMasterVolumeLevel(float levelDb, ref Guid context);

        void SetMasterVolumeLevelScalar(float level, ref Guid context);

        void GetMasterVolumeLevel(out float levelDb);

        void GetMasterVolumeLevelScalar(out float level);

        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);

        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);

        void GetChannelVolumeLevel(uint channel, out float levelDb);

        void GetChannelVolumeLevelScalar(uint channel, out float level);

        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

        void GetVolumeStepInfo(out uint step, out uint stepCount);

        void VolumeStepUp(ref Guid context);

        void VolumeStepDown(ref Guid context);
    }

    [ComImport]
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolumeCallback
    {
        [PreserveSig]
        int OnNotify(nint data);
    }

    [ComImport]
    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);

        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }
}
