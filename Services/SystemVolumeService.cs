using System;
using System.Runtime.InteropServices;

namespace Huanying.Services;

/// <summary>
/// 系统主音量（Windows Core Audio / IAudioEndpointVolume）。
///
/// 播放器的音量控件绑定到这里，调的是「系统音量」而不是只改本进程的播放音量：
/// 任务栏音量滑块、键盘音量键、系统音量弹窗读到的都是同一个值，互相跟随。
/// 拿不到音频设备（无声卡 / 远程会话）时 <see cref="IsAvailable"/> 为 false，
/// 播放页会退回 MediaPlayer.Volume，功能不至于不可用。
/// </summary>
internal sealed class SystemVolumeService : IDisposable
{
    private const int EDataFlowRender = 0;      // eRender
    private const int ERoleMultimedia = 1;      // eMultimedia
    private const int ClsCtxAll = 23;           // CLSCTX_ALL

    private static readonly Guid IID_IAudioEndpointVolume =
        new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private static readonly Guid EventContext = Guid.Empty;

    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private VolumeCallback? _callback;
    private IntPtr _callbackPtr;

    /// <summary>系统音量被外部改动（键盘音量键 / 任务栏 / 其它播放器）时触发</summary>
    public event Action? Changed;

    public bool IsAvailable => _endpoint != null;

    public SystemVolumeService()
    {
        try
        {
            Attach();
        }
        catch
        {
            // 没有音频端点或 COM 不可用：静默降级
            _endpoint = null;
        }
    }

    /// <summary>0..1 的系统主音量</summary>
    public float Volume
    {
        get
        {
            if (_endpoint == null) return 1f;

            try
            {
                return _endpoint.GetMasterVolumeLevelScalar(out var level) == 0
                    ? Math.Clamp(level, 0f, 1f)
                    : 1f;
            }
            catch
            {
                return 1f;
            }
        }
        set
        {
            if (_endpoint == null) return;

            try
            {
                _endpoint.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), EventContext);
            }
            catch
            {
                // 忽略瞬时失败（例如设备正在切换）
            }
        }
    }

    public bool Muted
    {
        get
        {
            if (_endpoint == null) return false;

            try
            {
                return _endpoint.GetMute(out var mute) == 0 && mute;
            }
            catch
            {
                return false;
            }
        }
        set
        {
            if (_endpoint == null) return;

            try
            {
                _endpoint.SetMute(value, EventContext);
            }
            catch
            {
                // 忽略
            }
        }
    }

    private void Attach()
    {
        var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!;
        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;

        if (_enumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out var device) != 0 ||
            device == null)
        {
            return;
        }

        var iid = IID_IAudioEndpointVolume;
        if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var activated) != 0 ||
            activated is not IAudioEndpointVolume endpoint)
        {
            return;
        }

        _endpoint = endpoint;

        // 订阅系统音量变化，好让界面上的滑块跟着键盘音量键走
        try
        {
            _callback = new VolumeCallback(this);
            _callbackPtr = Marshal.GetComInterfaceForObject(_callback, typeof(IAudioEndpointVolumeCallback));
            _endpoint.RegisterControlChangeNotify(_callbackPtr);
        }
        catch
        {
            _callbackPtr = IntPtr.Zero;
        }
    }

    internal void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
        if (_endpoint != null && _callbackPtr != IntPtr.Zero)
        {
            try
            {
                _endpoint.UnregisterControlChangeNotify(_callbackPtr);
            }
            catch
            {
                // 忽略
            }
        }

        if (_callbackPtr != IntPtr.Zero)
        {
            try
            {
                Marshal.Release(_callbackPtr);
            }
            catch
            {
                // 忽略
            }
            _callbackPtr = IntPtr.Zero;
        }

        _callback = null;

        if (_endpoint != null)
        {
            try
            {
                Marshal.ReleaseComObject(_endpoint);
            }
            catch
            {
                // 忽略
            }
            _endpoint = null;
        }

        if (_enumerator != null)
        {
            try
            {
                Marshal.ReleaseComObject(_enumerator);
            }
            catch
            {
                // 忽略
            }
            _enumerator = null;
        }
    }

    /* ================= COM 定义 ================= */

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        int RegisterEndpointNotificationCallback(IntPtr client);

        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);

        int UnregisterControlChangeNotify(IntPtr notify);

        int GetChannelCount(out uint channelCount);

        int SetMasterVolumeLevel(float levelDb, Guid eventContext);

        int SetMasterVolumeLevelScalar(float level, Guid eventContext);

        int GetMasterVolumeLevel(out float levelDb);

        int GetMasterVolumeLevelScalar(out float level);

        int SetChannelVolumeLevel(uint channelNumber, float levelDb, Guid eventContext);

        int SetChannelVolumeLevelScalar(uint channelNumber, float level, Guid eventContext);

        int GetChannelVolumeLevel(uint channelNumber, out float levelDb);

        int GetChannelVolumeLevelScalar(uint channelNumber, out float level);

        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid eventContext);

        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        void OnNotify(IntPtr notifyData);
    }

    [ComVisible(true)]
    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly SystemVolumeService _owner;

        public VolumeCallback(SystemVolumeService owner) => _owner = owner;

        public void OnNotify(IntPtr notifyData) => _owner.RaiseChanged();
    }
}
