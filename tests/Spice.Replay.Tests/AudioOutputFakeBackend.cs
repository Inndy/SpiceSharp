// Test-only COM stand-ins for the linked, unmodified AudioOutput source. No Win32 audio call or GUI is made.
// Empty structs have stable unmanaged addresses; their managed methods dispatch to this deterministic fake backend.
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;

namespace Spice.Replay.Tests
{
    static unsafe class AudioOutputFakeBackend
    {
        internal static readonly IMMDeviceEnumerator* Enumerator = (IMMDeviceEnumerator*)NativeMemory.AllocZeroed(1);
        // Two endpoints; GetDefaultAudioEndpoint returns Devices[DefaultDevice], each with its own GetId.
        internal static readonly IMMDevice*[] Devices =
            [(IMMDevice*)NativeMemory.AllocZeroed(1), (IMMDevice*)NativeMemory.AllocZeroed(1)];
        internal static readonly IAudioClient* Client = (IAudioClient*)NativeMemory.AllocZeroed(1);
        internal static readonly IAudioRenderClient* Render = (IAudioRenderClient*)NativeMemory.AllocZeroed(1);
        internal static readonly byte* Buffer = (byte*)NativeMemory.AllocZeroed(1 << 20);
        internal static int Padding, Rate, Channels, Starts, Opens, FailNextPadding, LastReleasedFrames, LastReleasedFlags;
        // Attempts: GetDefaultAudioEndpoint calls. NoDevice: it fails with E_NOTFOUND. FailOpens: that many next
        // Activate calls fail with OpenError.
        internal static int Attempts, DefaultDevice, NoDevice, FailOpens, OpenError;
        internal static Action? BeforeGetBuffer;
        internal static TaskCompletionSource<bool> FirstStart = Signal(), SecondStart = Signal(), SecondOpen = Signal();
        internal static TaskCompletionSource<bool>? NextWrite;

        static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal static void Reset()
        {
            Padding = Rate = Channels = Starts = Opens = FailNextPadding = 0;
            Attempts = DefaultDevice = NoDevice = FailOpens = OpenError = 0;
            LastReleasedFrames = LastReleasedFlags = 0;
            BeforeGetBuffer = null;
            FirstStart = Signal(); SecondStart = Signal(); SecondOpen = Signal();
            NextWrite = null;
        }

        internal static Task ExpectWrite()
        {
            var done = Signal();
            Volatile.Write(ref NextWrite, done);
            return done.Task;
        }

        internal static void Initialize(int rate, int channels)
        {
            Rate = rate; Channels = channels;
            Volatile.Write(ref Padding, 0);
            if (Interlocked.Increment(ref Opens) == 2) SecondOpen.TrySetResult(true);
        }

        internal static void Start()
        {
            int n = Interlocked.Increment(ref Starts);
            if (n == 1) FirstStart.TrySetResult(true);
            if (n == 2) SecondStart.TrySetResult(true);
        }

        internal static uint GetPadding()
        {
            if (Interlocked.Exchange(ref FailNextPadding, 0) != 0)
                throw new COMException("injected unrecoverable endpoint failure", unchecked((int)0x80004005));
            return (uint)Volatile.Read(ref Padding);
        }

        internal static IMMDevice* DefaultEndpoint()
        {
            Interlocked.Increment(ref Attempts);
            if (Volatile.Read(ref NoDevice) != 0) throw new COMException("no endpoint", unchecked((int)0x80070490));
            return Devices[Volatile.Read(ref DefaultDevice)];
        }

        internal static void Activate()
        {
            if (Volatile.Read(ref FailOpens) > 0 && Interlocked.Decrement(ref FailOpens) >= 0)
                throw new COMException("injected open failure", Volatile.Read(ref OpenError));
        }

        internal static char* Id(IMMDevice* device) => (char*)Marshal.StringToCoTaskMemUni(device == Devices[0] ? "dev0" : "dev1");

        internal static short[] CapturedSamples() => new ReadOnlySpan<short>(Buffer,
            Volatile.Read(ref LastReleasedFrames) * Channels).ToArray();

        internal static void Released(uint frames, uint flags)
        {
            if ((flags & 2) != 0) new Span<short>(Buffer, (int)frames * Channels).Clear();
            Volatile.Write(ref LastReleasedFlags, (int)flags);
            Volatile.Write(ref LastReleasedFrames, (int)frames);
            Volatile.Write(ref Padding, checked((int)frames));
            Volatile.Read(ref NextWrite)?.TrySetResult(true);
        }
    }
}

namespace Windows.Win32.Foundation
{
    struct HANDLE
    {
        public nint Value;
        public static explicit operator HANDLE(nint value) => new() { Value = value };
    }

    unsafe struct PWSTR
    {
        public char* Value;
    }

    struct HRESULT
    {
        public int Value;
        public void ThrowOnFailure()
        {
            if (Value < 0) throw new COMException("fake COM error", Value);
        }
    }
}

namespace Windows.Win32.System.Com
{
    enum COINIT { COINIT_MULTITHREADED }
    enum CLSCTX { CLSCTX_INPROC_SERVER }
}

namespace Windows.Win32
{
    static unsafe class PInvoke
    {
        public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 1, AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 2,
            AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 4;
        public static HRESULT CoInitializeEx(global::Windows.Win32.System.Com.COINIT flags) => new() { Value = 0 };
        public static void CoUninitialize() { }
        public static HRESULT CoCreateInstance(Guid* clsid, void* outer, global::Windows.Win32.System.Com.CLSCTX flags,
            Guid* iid, void** result)
        {
            *result = Spice.Replay.Tests.AudioOutputFakeBackend.Enumerator;
            return new() { Value = 0 };
        }
    }
}

namespace Windows.Win32.Media.Audio
{
    enum EDataFlow { eRender }
    enum ERole { eConsole }
    enum AUDCLNT_SHAREMODE { AUDCLNT_SHAREMODE_SHARED }

    struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    struct IMMNotificationClient { public static readonly Guid IID_Guid = Guid.Empty; }

    unsafe struct IMMDeviceEnumerator
    {
        public static readonly Guid IID_Guid = Guid.Empty;
        public void RegisterEndpointNotificationCallback(IMMNotificationClient* callback) { }
        public void UnregisterEndpointNotificationCallback(IMMNotificationClient* callback) { }
        public void GetDefaultAudioEndpoint(EDataFlow flow, ERole role, IMMDevice** device)
            => *device = Spice.Replay.Tests.AudioOutputFakeBackend.DefaultEndpoint();
        public void Release() { }
    }

    unsafe struct IMMDevice
    {
        public void Activate(Guid* iid, global::Windows.Win32.System.Com.CLSCTX flags, void* parameters, void** client)
        {
            Spice.Replay.Tests.AudioOutputFakeBackend.Activate();
            *client = Spice.Replay.Tests.AudioOutputFakeBackend.Client;
        }
        public void GetId(global::Windows.Win32.Foundation.PWSTR* id)
            => id->Value = Spice.Replay.Tests.AudioOutputFakeBackend.Id((IMMDevice*)global::System.Runtime.CompilerServices.Unsafe.AsPointer(ref this));
        public void Release() { }
    }

    unsafe struct IAudioClient
    {
        public static readonly Guid IID_Guid = Guid.Empty;
        public void Initialize(AUDCLNT_SHAREMODE mode, uint flags, long duration, long period, WAVEFORMATEX* format, void* session)
            => Spice.Replay.Tests.AudioOutputFakeBackend.Initialize((int)format->nSamplesPerSec, format->nChannels);
        public void SetEventHandle(HANDLE handle) { }
        public void GetService(Guid* iid, void** render) => *render = Spice.Replay.Tests.AudioOutputFakeBackend.Render;
        public void GetBufferSize(uint* size) => *size = (uint)(Spice.Replay.Tests.AudioOutputFakeBackend.Rate / 25);
        public void GetStreamLatency(long* latency) => *latency = 0;
        public void GetCurrentPadding(uint* padding) => *padding = Spice.Replay.Tests.AudioOutputFakeBackend.GetPadding();
        public void Start() => Spice.Replay.Tests.AudioOutputFakeBackend.Start();
        public void Stop() { }
        public void Release() { }
    }

    unsafe struct IAudioRenderClient
    {
        public static readonly Guid IID_Guid = Guid.Empty;
        public void GetBuffer(uint frames, byte** data)
        {
            Volatile.Read(ref Spice.Replay.Tests.AudioOutputFakeBackend.BeforeGetBuffer)?.Invoke();
            if ((long)frames * Spice.Replay.Tests.AudioOutputFakeBackend.Channels * sizeof(short) > 1 << 20)
                throw new ArgumentOutOfRangeException(nameof(frames));
            *data = Spice.Replay.Tests.AudioOutputFakeBackend.Buffer;
        }
        public void ReleaseBuffer(uint frames, uint flags) => Spice.Replay.Tests.AudioOutputFakeBackend.Released(frames, flags);
        public void Release() { }
    }
}
