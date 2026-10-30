using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Spice.Session;
using Spice.Wire;
using SpiceViewer.Video;
using VvFile;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;

namespace Spice.Video.Tests;

sealed class MediaFoundationAvailableFactAttribute : FactAttribute
{
    public MediaFoundationAvailableFactAttribute()
    {
        if (!NativeLibrary.TryLoad("mfplat.dll", out var module)) Skip = "Media Foundation is not installed";
        else NativeLibrary.Free(module);
    }
}

// Review regressions for bcceecd. No window, swap chain, fixture, or real device removal.
#pragma warning disable IL2026, IL2070, IL2075
[Collection("Hardware video")]
public sealed partial class HardwareVideoReviewTests
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [LibraryImport("ole32.dll", EntryPoint = "CoInitializeEx")]
    private static partial int InitializeCom(nint reserved, uint flags);

    [LibraryImport("ole32.dll", EntryPoint = "CoUninitialize")]
    private static partial void UninitializeCom();

    [LibraryImport("mfplat.dll", EntryPoint = "MFShutdown")]
    private static partial int ShutdownMf();

    // CoInitializeEx returns S_OK for the first explicit initialization, S_FALSE for a nested one.
    // Balance this measuring call immediately, so even an implicit process MTA doesn't skew the result.
    static int ComInitializationState()
    {
        int hr = InitializeCom(0, 0);
        if (hr >= 0) UninitializeCom();
        return hr;
    }

    static int ComInitializationDepth()
    {
        int depth = 0;
        // Only used on our dedicated thread, with no live apartment-bound objects. Restore every count measured.
        while (ComInitializationState() == 1 && depth < 32) { UninitializeCom(); depth++; }
        for (int i = 0; i < depth; i++) InitializeCom(0, 0);
        Assert.True(depth < 32, "unexpected COM initialization depth");
        return depth;
    }

    [MediaFoundationAvailableFact]
    public void DecoderEnumerationDoesNotLeaveComInitializationsOnItsCallingThread()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            bool started = false;
            int baseline = 0;
            try
            {
                baseline = ComInitializationDepth();
                PInvoke.MFStartup(Mf.Version, Mf.StartupLite).ThrowOnFailure();
                started = true;
                baseline = ComInitializationDepth();
                EnumerateAbsentDecoder();
                EnumerateAbsentDecoder();
                Assert.Equal(baseline, ComInitializationDepth());
            }
            catch (Exception e) { error = e; }
            finally
            {
                // Unwind leaked initialization counts on this dedicated test thread only.
                int remaining = ComInitializationDepth();
                for (int i = baseline; i < remaining; i++) UninitializeCom();
                if (started) ShutdownMf();
            }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "decoder enumeration did not finish");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    static unsafe void EnumerateAbsentDecoder() =>
        Assert.Null(HardwareVideo.FindDecoder(new Guid("95cb3ea1-1786-46e8-bb14-7b877754f60a"), null));

    static FieldInfo Field(Type type, string name) => type.GetField(name, Private)!;
    static object Stream(SpiceSession session) => ((IDictionary)Field(typeof(SpiceSession), "_streams").GetValue(session)!)[7u]!;
    static object State(SpiceSession session, string name) => Stream(session).GetType().GetField(name)!.GetValue(Stream(session))!;
    static byte[] Bytes(Action<BinaryWriter> write)
    { using var m = new MemoryStream(); using var w = new BinaryWriter(m); write(w); return m.ToArray(); }

    static SpiceSession Session(HardwareVideo factory, int width, int height, bool hidden = false)
    {
        var s = new SpiceSession(VvConnection.Parse("[virt-viewer]\ntype=spice\nhost=offline\nport=5900\n")) { VideoDecoders = factory };
        Field(typeof(SpiceSession), "_videoCodecs").SetValue(s, new[] { VideoCodecType.H264 });
        s.OnDisplayMessage(DisplayMsg.SurfaceCreate, Bytes(w => { w.Write(0u); w.Write(10u); w.Write(8u); w.Write(32u); w.Write(1u); }));
        s.OnDisplayMessage(DisplayMsg.StreamCreate, Bytes(w =>
        {
            w.Write(0u); w.Write(7u); w.Write(StreamCreate.FlagTopDown); w.Write((byte)VideoCodecType.H264); w.Write(0UL);
            w.Write((uint)TestClip.Width); w.Write((uint)TestClip.Height); w.Write((uint)TestClip.Width); w.Write((uint)TestClip.Height);
            w.Write(0); w.Write(0); w.Write(height); w.Write(width);
            w.Write(hidden ? SpiceClip.TypeRects : SpiceClip.TypeNone);
            if (hidden) w.Write(0u);
        }));
        return s;
    }

    static void Frame(SpiceSession s, int index) => s.OnDisplayMessage(DisplayMsg.StreamData, Bytes(w =>
    {
        var data = TestClip.Frames[index];
        w.Write(7u); w.Write((uint)(1000 + index * 33)); w.Write((uint)data.Length); w.Write(data);
    }));

    static unsafe nint PointerField(object instance, string name) =>
        (nint)Pointer.Unbox(Field(instance.GetType(), name).GetValue(instance)!);

    [HardwareCodecFact(VideoCodecType.H264)]
    public async Task AnImpossibleDestinationDropsThePictureWithoutFailingTheDecoder()
    {
        // One row only: no large allocation or GPU stress. The side exceeds both the canvas and D3D11 limit.
        await using var s = Session(Hardware.Factory, Spice.Canvas.Canvas.MaxStreamSide + 1, 1);
        Assert.False((bool)State(s, "Unsupported"));
        var decoder = State(s, "Decoder");
        for (int i = 0; i < 3; i++) Frame(s, i);
        Assert.False((bool)State(s, "Unsupported"));
        Assert.Equal(3u, State(s, "Drops"));
        Assert.Equal(0, PointerField(decoder, "_target"));
        Assert.Equal(0, PointerField(decoder, "_staging"));
        Assert.Null(StateOrNull(s, "Frame"));
    }

    static object? StateOrNull(SpiceSession s, string name) => Stream(s).GetType().GetField(name)!.GetValue(Stream(s));

    [HardwareCodecFact(VideoCodecType.H264)]
    public async Task HiddenDecodedPicturesProveARecreatedDeviceBeforeAnotherLoss()
    {
        var factory = new HardwareVideo();
        Assert.Contains(VideoCodecType.H264, await factory.Codecs.WaitAsync(TimeSpan.FromSeconds(10)));
        try
        {
            await using var s = Session(factory, TestClip.Width, TestClip.Height, hidden: true);
            Assert.False((bool)State(s, "Unsupported"));
            // Same recovery state as CreateSessionDevice after the first loss; use a healthy headless device.
            Field(typeof(HardwareVideo), "_recreated").SetValue(factory, true);
            for (int i = 0; i < 3; i++) Frame(s, i);
            Assert.False((bool)State(s, "Unsupported"));
            Assert.Equal(3u, State(s, "Hidden"));
            Assert.Null(StateOrNull(s, "Frame")); // no readback was requested
            ReleaseFactoryDevice(factory); // signal a loss, without removing the real GPU device
            Assert.False((bool)Field(typeof(HardwareVideo), "_disabled").GetValue(factory)!);
        }
        finally { ReleaseFactoryDevice(factory); ShutdownMf(); }
    }

    static unsafe void ReleaseFactoryDevice(HardwareVideo factory)
    {
        var device = (ID3D11Device*)PointerField(factory, "_device");
        if (device is not null) factory.OnDeviceLost(device, unchecked((HRESULT)(int)0x887A0005));
    }

    [HardwareCodecFact(VideoCodecType.H264)]
    public async Task LastOldGenerationDecoderClosingReleasesAnIdleReplacementDevice()
    {
        var factory = new HardwareVideo();
        Assert.Contains(VideoCodecType.H264, await factory.Codecs.WaitAsync(TimeSpan.FromSeconds(10)));
        IVideoDecoder? oldA = null, oldB = null, replacement = null;
        try
        {
            oldA = factory.Create(VideoCodecType.H264, 320, 240);
            oldB = factory.Create(VideoCodecType.H264, 320, 240);
            Assert.NotNull(oldA); Assert.NotNull(oldB);
            nint oldDevice = PointerField(factory, "_device");
            ReleaseFactoryDevice(factory); // synthetic loss notification; old decoders still own their COM references
            replacement = factory.Create(VideoCodecType.H264, 320, 240);
            Assert.NotNull(replacement);
            nint newDevice = PointerField(factory, "_device");
            Assert.NotEqual(0, newDevice);
            Assert.NotEqual(oldDevice, newDevice);
            replacement.Dispose();
            oldA.Dispose();
            oldB.Dispose(); // last close belongs to the old device, not the retained replacement
            Assert.Equal(0, (int)Field(typeof(HardwareVideo), "_decoders").GetValue(factory)!);
            Assert.Equal(0, PointerField(factory, "_device"));
            Assert.Equal(0, PointerField(factory, "_manager"));
        }
        finally
        {
            replacement?.Dispose(); oldA?.Dispose(); oldB?.Dispose();
            ReleaseFactoryDevice(factory); ShutdownMf();
        }
    }

    [HardwareCodecFact(VideoCodecType.H264)]
    public async Task HiddenDecodedPicturesAllowANewStreamAfterAFirstDeviceLoss()
    {
        var factory = new HardwareVideo();
        Assert.Contains(VideoCodecType.H264, await factory.Codecs.WaitAsync(TimeSpan.FromSeconds(10)));
        try
        {
            await using var s = Session(factory, TestClip.Width, TestClip.Height, hidden: true);
            var decoder = State(s, "Decoder");
            for (int i = 0; i < 3; i++) Frame(s, i);
            Assert.True((bool)Field(decoder.GetType(), "_pictureSeen").GetValue(decoder)!);
            Assert.True((bool)Field(typeof(HardwareVideo), "_proven").GetValue(factory)!);
            Assert.Null(StateOrNull(s, "Frame"));
            ReleaseFactoryDevice(factory); // first loss, eligible for recovery under 0018/0037
            Field(decoder.GetType(), "_failed").SetValue(decoder, true); // no driver reset; next Decode returns Failed
            Frame(s, 3);
            Assert.True((bool)State(s, "Unsupported"));
            Assert.False((bool)Field(typeof(HardwareVideo), "_disabled").GetValue(factory)!);
            s.OnDisplayMessage(DisplayMsg.StreamDestroy, BitConverter.GetBytes(7u));
            s.OnDisplayMessage(DisplayMsg.StreamCreate, Bytes(w =>
            {
                w.Write(0u); w.Write(7u); w.Write(StreamCreate.FlagTopDown); w.Write((byte)VideoCodecType.H264); w.Write(0UL);
                w.Write((uint)TestClip.Width); w.Write((uint)TestClip.Height); w.Write((uint)TestClip.Width); w.Write((uint)TestClip.Height);
                w.Write(0); w.Write(0); w.Write(TestClip.Height); w.Write(TestClip.Width); w.Write(SpiceClip.TypeNone);
            }));
            Assert.False((bool)State(s, "Unsupported")); // successful hidden pictures must not poison this codec
        }
        finally { ReleaseFactoryDevice(factory); ShutdownMf(); }
    }
}
