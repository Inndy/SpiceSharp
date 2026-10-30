using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32.Media.Audio;

namespace SpiceViewer;

/// <summary>
/// Unmanaged IMMNotificationClient. No RCW, COM marshalling, or callback polling. The callbacks only set events (no
/// allocation) and never let an exception cross the COM boundary.
/// </summary>
static unsafe class DeviceNotifications
{
    internal struct Native
    {
        public void** Vtable;
        public nint DefaultChanged, DevicesChanged; // GCHandles of the events
        public int References;
        public int DefaultChanges; // console-role render default changes announced, for the stream-close log
    }
    const uint DEVICE_STATE_ACTIVE = 1;
    static readonly void** vtable = MakeVtable();
    static void** MakeVtable()
    {
        var table = (void**)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(DeviceNotifications), IntPtr.Size * 8);
        table[0] = (delegate* unmanaged[Stdcall]<Native*, Guid*, void**, int>)&Query;
        table[1] = (delegate* unmanaged[Stdcall]<Native*, uint>)&AddRef;
        table[2] = (delegate* unmanaged[Stdcall]<Native*, uint>)&Release;
        table[3] = (delegate* unmanaged[Stdcall]<Native*, char*, uint, int>)&StateChanged;
        table[4] = (delegate* unmanaged[Stdcall]<Native*, char*, int>)&Added;
        table[5] = (delegate* unmanaged[Stdcall]<Native*, char*, int>)&Removed;
        table[6] = (delegate* unmanaged[Stdcall]<Native*, int, int, char*, int>)&DefaultChanged;
        table[7] = (delegate* unmanaged[Stdcall]<Native*, char*, PropertyKey, int>)&PropertyChanged;
        return table;
    }
    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public uint Id; }

    /// <summary>
    /// <paramref name="defaultChanged"/> is set when the default render device for the console role changes;
    /// <paramref name="devicesChanged"/> when a device is added or becomes active (a detached stream may reattach).
    /// </summary>
    internal static Native* Create(EventWaitHandle defaultChanged, EventWaitHandle devicesChanged)
    {
        var native = (Native*)NativeMemory.AllocZeroed((nuint)sizeof(Native));
        if (native == null) throw new OutOfMemoryException();
        try
        {
            native->DefaultChanged = GCHandle.ToIntPtr(GCHandle.Alloc(defaultChanged));
            native->DevicesChanged = GCHandle.ToIntPtr(GCHandle.Alloc(devicesChanged));
        }
        catch
        {
            if (native->DefaultChanged != 0) GCHandle.FromIntPtr(native->DefaultChanged).Free();
            NativeMemory.Free(native);
            throw;
        }
        native->Vtable = vtable; native->References = 1; return native;
    }
    /// <summary>Console-role render default changes announced so far.</summary>
    internal static int DefaultChanges(Native* native) => Volatile.Read(ref native->DefaultChanges);
    internal static void ReleaseOwner(Native* native) => ReleaseCore(native);
    static uint ReleaseCore(Native* self)
    {
        int refs = Interlocked.Decrement(ref self->References);
        if (refs == 0)
        {
            GCHandle.FromIntPtr(self->DefaultChanged).Free();
            GCHandle.FromIntPtr(self->DevicesChanged).Free();
            NativeMemory.Free(self);
        }
        return (uint)refs;
    }
    static void Signal(nint handle)
    {
        try { ((EventWaitHandle)GCHandle.FromIntPtr(handle).Target!).Set(); }
        catch { /* Never propagate an exception across the COM callback boundary. */ }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Query(Native* self, Guid* iid, void** value)
    {
        if (value == null || iid == null) return unchecked((int)0x80004003);
        *value = null;
        if (*iid != IMMNotificationClient.IID_Guid && *iid != new Guid("00000000-0000-0000-C000-000000000046")) return unchecked((int)0x80004002);
        Interlocked.Increment(ref self->References); *value = self; return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] static uint AddRef(Native* self) => (uint)Interlocked.Increment(ref self->References);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] static uint Release(Native* self) => ReleaseCore(self);
    // Any device (render or capture: the id doesn't say without a call) becoming active may be one to play on.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int StateChanged(Native* self, char* id, uint state)
    {
        if (state == DEVICE_STATE_ACTIVE) Signal(self->DevicesChanged);
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Added(Native* self, char* id)
    {
        Signal(self->DevicesChanged);
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] static int Removed(Native* self, char* id) => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] static int PropertyChanged(Native* self, char* id, PropertyKey key) => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int DefaultChanged(Native* self, int flow, int role, char* id)
    {
        if (flow == 0 && role == 0) // eRender, eConsole: the device Play opens
        {
            Interlocked.Increment(ref self->DefaultChanges);
            Signal(self->DefaultChanged);
        }
        return 0;
    }
}
