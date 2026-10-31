using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Pve.Api;

/// <summary>
/// Tells when the system goes to sleep and wakes up (docs/decisions/0038, the human's answer on minimizing): a
/// callback the system calls, <c>PowerRegisterSuspendResumeNotification</c> with <c>DEVICE_NOTIFY_CALLBACK</c>, so
/// nothing polls and no window message is needed. The callback given to <see cref="TryStart"/> runs on a system
/// thread with true before a suspend (<c>PBT_APMSUSPEND</c>) and false after a resume (<c>PBT_APMRESUMEAUTOMATIC</c>,
/// then <c>PBT_APMRESUMESUSPEND</c> when a user is present); the receiver queues it where it belongs.
/// </summary>
public sealed unsafe class SuspendResumeWatcher : IDisposable
{
    readonly Action<bool> _changed;
    GCHandle _self;
    DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS* _parameters;
    void* _registration;

    SuspendResumeWatcher(Action<bool> changed) => _changed = changed;

    /// <summary>Registers <paramref name="changed"/> (true: going to sleep; false: awake again), or returns null
    /// with the Win32 error in <paramref name="error"/>.</summary>
    public static SuspendResumeWatcher? TryStart(Action<bool> changed, out int error)
    {
        var w = new SuspendResumeWatcher(changed);
        w._self = GCHandle.Alloc(w);
        // Kept until Dispose: the documentation doesn't say the system copies it.
        w._parameters = (DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS*)NativeMemory.AllocZeroed((nuint)sizeof(DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS));
        w._parameters->Callback = &OnNotify;
        w._parameters->Context = (void*)GCHandle.ToIntPtr(w._self);
        void* registration = null;
        var result = PInvoke.PowerRegisterSuspendResumeNotification(REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_CALLBACK,
            new HANDLE(w._parameters), &registration);
        error = (int)result;
        if (result != WIN32_ERROR.ERROR_SUCCESS)
        {
            w.Free();
            return null;
        }
        w._registration = registration;
        return w;
    }

    /// <summary>What a notification type means: true going to sleep, false awake again, null for the others (power
    /// source and battery changes, which don't concern the refresh).</summary>
    internal static bool? Map(uint type) => type switch
    {
        PInvoke.PBT_APMSUSPEND => true,
        PInvoke.PBT_APMRESUMEAUTOMATIC or PInvoke.PBT_APMRESUMESUSPEND => false,
        _ => null,
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint OnNotify(void* context, uint type, void* setting)
    {
        try
        {
            if (Map(type) is bool suspending && GCHandle.FromIntPtr((nint)context).Target is SuspendResumeWatcher w) w._changed(suspending);
        }
        catch (Exception)
        {
            // Nothing may escape into the system's thread; the receiver only queues work.
        }
        return (uint)WIN32_ERROR.ERROR_SUCCESS;
    }

    public void Dispose()
    {
        if (_registration is not null)
        {
            PInvoke.PowerUnregisterSuspendResumeNotification(new HPOWERNOTIFY((nint)_registration));
            _registration = null;
        }
        Free();
    }

    void Free()
    {
        if (_parameters is not null)
        {
            NativeMemory.Free(_parameters);
            _parameters = null;
        }
        if (_self.IsAllocated) _self.Free();
    }
}
