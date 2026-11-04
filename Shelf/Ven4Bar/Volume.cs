using System;
using System.Runtime.InteropServices;

namespace Ven4Bar;

/// <summary>
/// Громкость основного устройства вывода через Core Audio (IMMDeviceEnumerator → IAudioEndpointVolume).
/// Изменения громкости приходят уведомлением IAudioEndpointVolumeCallback — без опроса.
/// </summary>
internal sealed class Volume : IDisposable
{
    private IAudioEndpointVolume? _ep;
    private Callback? _cb;

    /// <summary>Громкость 0..1 и признак «без звука». Может прийти из потока Core Audio.</summary>
    public event Action<float, bool>? Changed;

    public bool Init()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var device));
            var iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out var obj));
            _ep = (IAudioEndpointVolume)obj;
            _cb = new Callback(this);
            _ep.RegisterControlChangeNotify(_cb);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("громкость недоступна (нет устройства вывода звука?)", ex);
            _ep = null;
            return false;
        }
    }

    public float Level
    {
        get
        {
            if (_ep == null) return 0;
            _ep.GetMasterVolumeLevelScalar(out float v);
            return v;
        }
        set
        {
            if (_ep == null) return;
            var g = Guid.Empty;
            _ep.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref g);
        }
    }

    public bool Muted
    {
        get
        {
            if (_ep == null) return false;
            _ep.GetMute(out bool m);
            return m;
        }
        set
        {
            if (_ep == null) return;
            var g = Guid.Empty;
            _ep.SetMute(value, ref g);
        }
    }

    public bool Available => _ep != null;

    public void Dispose()
    {
        if (_ep != null && _cb != null)
        {
            try { _ep.UnregisterControlChangeNotify(_cb); } catch (Exception ex) { CrashLog.Write("отписка от громкости", ex); }
        }
        _ep = null;
    }

    [ComVisible(true)]
    private sealed class Callback : IAudioEndpointVolumeCallback
    {
        private readonly Volume _owner;
        public Callback(Volume owner) => _owner = owner;

        public int OnNotify(IntPtr pNotify)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: GUID guidEventContext; BOOL bMuted; float fMasterVolume; ...
            bool muted = Marshal.ReadInt32(pNotify, 16) != 0;
            float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pNotify, 20));
            _owner.Changed?.Invoke(level, muted);
            return 0;
        }
    }

    // ---------- COM-интерфейсы из mmdeviceapi.h / endpointvolume.h ----------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComVisible(true)]
    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr pNotify);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid ctx);
        [PreserveSig] int VolumeStepDown(ref Guid ctx);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incDb);
    }
}
