using System.Diagnostics;
using Spice.Session;
using Spice.Wire;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace SpiceViewer.Video;

/// <summary>
/// Hardware video decoding for SPICE's GStreamer codecs (docs/decisions/0037): H.264, VP9 and HEVC, each through a
/// Media Foundation decoder MFT that decodes with DXVA on a Direct3D 11 device. A codec is offered only when the adapter
/// has a hardware decoder profile for it and Media Foundation has a D3D11-aware decoder MFT; the probe runs once, in the
/// background, from process start. The decoding device is created when a stream needs it and released when its last
/// decoder closes, so an idle session holds none; a lost device is recreated once (as the presenter does,
/// docs/decisions/0018).
/// </summary>
sealed unsafe class HardwareVideo : IVideoDecoderFactory
{
    /// <summary>One codec the probe found: its MF subtype and the D3D11 decoder profile the adapter must have.</summary>
    readonly record struct CodecInfo(VideoCodecType Codec, Guid Subtype, Guid Profile, string Name);

    static readonly CodecInfo[] Known =
    [
        new(VideoCodecType.H264, PInvoke.MFVideoFormat_H264, PInvoke.D3D11_DECODER_PROFILE_H264_VLD_NOFGT, "H.264"),
        new(VideoCodecType.Vp9, PInvoke.MFVideoFormat_VP90, PInvoke.D3D11_DECODER_PROFILE_VP9_VLD_PROFILE0, "VP9"),
        new(VideoCodecType.H265, PInvoke.MFVideoFormat_HEVC, PInvoke.D3D11_DECODER_PROFILE_HEVC_VLD_MAIN, "HEVC"),
    ];

    readonly Lock _lock = new();
    // Under _lock: the decoding device, shared by this session's decoders (each holds its own references).
    ID3D11Device* _device;
    IMFDXGIDeviceManager* _manager;
    bool _recreated, _proven, _disabled;
    int _decoders; // under _lock: open decoders, made or being made

    /// <summary>Starts the probe in the background. One instance per process; the viewer makes it before connecting.</summary>
    public HardwareVideo() : this(softwareAdapter: false) { }

    /// <summary>
    /// <paramref name="softwareAdapter"/>: probe as on a software adapter (as <c>SPICEVIEWER_FORCE_WARP=1</c> does),
    /// so nothing decodes in hardware. For tests, which can't set the process-wide variable safely.
    /// </summary>
    internal HardwareVideo(bool softwareAdapter)
    {
        _softwareAdapter = softwareAdapter;
        Codecs = Task.Run(Probe);
    }

    readonly bool _softwareAdapter;

    public Task<IReadOnlyList<VideoCodecType>> Codecs { get; }

    /// <summary>The adapter the probe used, for logs and About ("" until known).</summary>
    public string AdapterName { get; private set; } = "";

    // The Media Foundation decoder the probe found per codec (indexed by VideoCodecType), for the status overlay.
    readonly string?[] _decoderNames = new string?[8];

    /// <summary>The friendly name of the hardware decoder found for <paramref name="codec"/>, or null. Any thread.</summary>
    public string? DecoderName(VideoCodecType codec) =>
        (int)codec < _decoderNames.Length ? Volatile.Read(ref _decoderNames[(int)codec]) : null;

    /// <summary>
    /// COM (the multithreaded apartment) for the calling thread for one operation, balanced on that thread: S_OK and
    /// S_FALSE are counted initializations and are undone on dispose; RPC_E_CHANGED_MODE (an STA thread) took no count
    /// and is left alone, as the MF calls made here work from either apartment.
    /// </summary>
    readonly ref struct ComScope
    {
        readonly bool _initialized;
        public ComScope() => _initialized = PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED).Succeeded;
        public void Dispose() { if (_initialized) PInvoke.CoUninitialize(); }
    }

    /// <summary>The probe found nothing: software H.264 (VideoDecoderRouter) may still be advertised (decision 0037).</summary>
    const string NoHardware = "no hardware video decoding (H.264 can still be decoded in software)";

    IReadOnlyList<VideoCodecType> Probe()
    {
        var clock = Stopwatch.StartNew();
        if (_softwareAdapter || Environment.GetEnvironmentVariable("SPICEVIEWER_FORCE_WARP") == "1")
        {
            Log.Info($"video: software profile (SPICEVIEWER_FORCE_WARP): {NoHardware}");
            return [];
        }
        using var com = new ComScope();
        try
        {
            var hr = PInvoke.MFStartup(Mf.Version, Mf.StartupLite);
            if (hr.Failed) { Log.Info($"video: Media Foundation is not available ({hr}); {NoHardware}"); return []; }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Windows N and Server without the Media Foundation feature have no mfplat.dll.
            Log.Info($"video: Media Foundation is not installed ({e.GetType().Name}); {NoHardware}");
            return [];
        }
        ID3D11Device* device = null;
        try
        {
            device = CreateDevice(out string adapter, out bool software);
            AdapterName = adapter;
            if (device is null) { Log.Info($"video: no hardware Direct3D 11 device with video support; {NoHardware}"); return []; }
            if (software) { Log.Info($"video: '{adapter}' is a software adapter; {NoHardware}"); return []; }
            var found = new List<VideoCodecType>();
            var profiles = AdapterProfiles(device);
            foreach (var c in Known)
            {
                if (!profiles.Contains(c.Profile) || !SupportsNv12(device, c.Profile))
                {
                    Log.Info($"video: {c.Name}: '{adapter}' has no hardware decoder for it");
                    continue;
                }
                string? mft = FindDecoder(c.Subtype, null);
                if (mft is null)
                {
                    string hint = c.Codec switch
                    {
                        VideoCodecType.H265 => " (Microsoft's \"HEVC Video Extensions\" provide one)",
                        VideoCodecType.Vp9 => " (Microsoft's \"VP9 Video Extensions\" provide one)",
                        _ => "",
                    };
                    Log.Info($"video: {c.Name}: '{adapter}' decodes it, but Media Foundation has no Direct3D 11 decoder for it{hint}");
                    continue;
                }
                Log.Info($"video: {c.Name}: hardware decoding with '{mft}' on '{adapter}'");
                Volatile.Write(ref _decoderNames[(int)c.Codec], mft);
                found.Add(c.Codec);
            }
            Log.Info($"video: probe took {clock.ElapsedMilliseconds} ms");
            return found;
        }
        catch (Exception e)
        {
            Log.Warn($"video: probe failed: {e.Message}; {NoHardware}");
            return [];
        }
        finally { Mf.Release(ref device); }
    }

    /// <summary>A hardware D3D11 device on the default adapter (the one the presenter uses), multithread protected.</summary>
    internal static ID3D11Device* CreateDevice(out string adapterName, out bool software)
    {
        adapterName = "";
        software = false;
        var levels = stackalloc D3D_FEATURE_LEVEL[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0 };
        ID3D11Device* dev = null;
        D3D_FEATURE_LEVEL level = 0;
        var flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        var hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.Null, flags, levels, 4,
            PInvoke.D3D11_SDK_VERSION, &dev, &level, null);
        if (hr.Failed) return null;
        try
        {
            dev->QueryInterface<IDXGIDevice>(out var dxgi).ThrowOnFailure();
            try
            {
                IDXGIAdapter* adapter;
                dxgi->GetAdapter(&adapter);
                if (adapter->QueryInterface<IDXGIAdapter1>(out var adapter1).Succeeded)
                {
                    var desc = adapter1->GetDesc1();
                    adapterName = desc.Description.ToString().TrimEnd('\0');
                    software = ((uint)desc.Flags & (uint)DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || desc.VendorId == 0x1414;
                    adapter1->Release();
                }
                adapter->Release();
            }
            finally { dxgi->Release(); }
            // Media Foundation uses the device from its own threads too.
            dev->QueryInterface<ID3D10Multithread>(out var mt).ThrowOnFailure();
            mt->SetMultithreadProtected(true);
            mt->Release();
            var result = dev;
            dev = null;
            return result;
        }
        finally { Mf.Release(ref dev); }
    }

    static HashSet<Guid> AdapterProfiles(ID3D11Device* device)
    {
        var set = new HashSet<Guid>();
        if (device->QueryInterface<ID3D11VideoDevice>(out var video).Failed) return set;
        try
        {
            uint n = video->GetVideoDecoderProfileCount();
            for (uint i = 0; i < n; i++)
            {
                Guid g;
                video->GetVideoDecoderProfile(i, &g);
                set.Add(g);
            }
        }
        finally { video->Release(); }
        return set;
    }

    static bool SupportsNv12(ID3D11Device* device, Guid profile)
    {
        if (device->QueryInterface<ID3D11VideoDevice>(out var video).Failed) return false;
        try
        {
            BOOL ok;
            video->CheckVideoDecoderFormat(&profile, DXGI_FORMAT.DXGI_FORMAT_NV12, &ok);
            return ok;
        }
        catch (Exception) { return false; }
        finally { video->Release(); }
    }

    /// <summary>
    /// Finds the first (by merit) synchronous decoder MFT for <paramref name="subtype"/> that is Direct3D 11 aware.
    /// Returns its name; with <paramref name="transform"/>, also the activated transform (the caller owns it, and shuts
    /// it down through <paramref name="activate"/>).
    /// </summary>
    internal static string? FindDecoder(Guid subtype, IMFTransform** transform, IMFActivate** activate = null)
    {
        using var com = new ComScope();
        var input = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = subtype };
        IMFActivate** list = null;
        uint count = 0;
        var flags = MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER;
        if (PInvoke.MFTEnumEx(PInvoke.MFT_CATEGORY_VIDEO_DECODER, flags, &input, null, &list, &count).Failed) return null;
        string? found = null;
        try
        {
            for (uint i = 0; i < count; i++)
            {
                var a = list[i];
                if (found is not null) continue;
                IMFTransform* t = null;
                try
                {
                    var iid = typeof(IMFTransform).GUID;
                    t = (IMFTransform*)a->ActivateObject(&iid);
                    if (!IsD3D11Aware(t)) { t->Release(); t = null; a->ShutdownObject(); continue; }
                    found = Mf.FriendlyName(a) ?? "decoder";
                    if (transform is not null)
                    {
                        *transform = t;
                        t = null;
                        if (activate is not null) { a->AddRef(); *activate = a; }
                    }
                    else { t->Release(); t = null; a->ShutdownObject(); }
                }
                catch (Exception e)
                {
                    Log.Debug($"video: decoder MFT {i} for {subtype} failed to load: {e.Message}");
                    if (t is not null) t->Release();
                }
            }
        }
        finally
        {
            for (uint i = 0; i < count; i++) list[i]->Release();
            PInvoke.CoTaskMemFree(list);
        }
        return found;
    }

    static bool IsD3D11Aware(IMFTransform* t)
    {
        IMFAttributes* attributes = null;
        try { t->GetAttributes(&attributes); }
        catch (Exception) { return false; }
        try { return Mf.TryGetUInt32(attributes, PInvoke.MF_SA_D3D11_AWARE, out uint aware) && aware != 0; }
        finally { attributes->Release(); }
    }

    public IVideoDecoder? Create(VideoCodecType codec, int width, int height)
    {
        if (!Codecs.IsCompletedSuccessfully || !Codecs.Result.Contains(codec)) return null;
        var info = Array.Find(Known, c => c.Codec == codec);
        ID3D11Device* device;
        IMFDXGIDeviceManager* manager;
        lock (_lock)
        {
            if (_disabled) return null;
            if (_device is null && !CreateSessionDevice()) return null;
            device = _device; device->AddRef();
            manager = _manager; manager->AddRef();
            _decoders++;
        }
        try { return new MfVideoDecoder(this, codec, info.Subtype, info.Name, device, manager, width, height); }
        catch (Exception e)
        {
            Log.Warn($"video: {info.Name} decoder could not be set up: {e.Message}"); // it closed itself (OnDecoderClosed)
            return null;
        }
        finally
        {
            // The decoder took its own references.
            device->Release();
            manager->Release();
        }
    }

    /// <summary>Under _lock: the session's decoding device and its DXGI device manager.</summary>
    bool CreateSessionDevice()
    {
        // No COM here: D3D11CreateDevice and MFCreateDXGIDeviceManager need no apartment.
        var dev = CreateDevice(out var adapter, out bool software);
        if (dev is null || software)
        {
            Mf.Release(ref dev);
            Log.Warn("video: no hardware Direct3D 11 device for decoding now");
            return false;
        }
        uint token;
        IMFDXGIDeviceManager* manager;
        var hr = PInvoke.MFCreateDXGIDeviceManager(&token, &manager);
        if (hr.Failed) { dev->Release(); Log.Warn($"video: MFCreateDXGIDeviceManager failed ({hr})"); return false; }
        try { manager->ResetDevice((IUnknown*)dev, token); }
        catch (Exception e)
        {
            manager->Release(); dev->Release();
            Log.Warn($"video: the decoding device could not be shared with Media Foundation: {e.Message}");
            return false;
        }
        _device = dev;
        _manager = manager;
        _proven = false;
        Log.Info($"video: decoding device on '{adapter}'");
        return true;
    }

    /// <summary>
    /// The formats the probed DXVA profiles cover: H.264 up to High, 8-bit 4:2:0 (H264_VLD_NOFGT) and VP9 profile 0
    /// (VP9_VLD_PROFILE0). spice-server's x264enc and vp9enc get 4:4:4 from its videoconvert, and no hardware decoder
    /// takes that; spice-server releases newer than 0.15.2 let the host pick 4:2:0 with SPICE_CONVERTER_PREFERRED_FORMAT,
    /// which Proxmox's 0.15.2 ignores. Software decoding takes those streams (SoftwareVideo).
    /// </summary>
    public bool CanDecode(in VideoFormat format) => format.Codec switch
    {
        VideoCodecType.H264 => format.Profile is 66 or 77 or 100 && format.ChromaFormat == 1 && format.BitDepth == 8,
        VideoCodecType.Vp9 => format.Profile == 0,
        _ => true,
    };

    /// <summary>
    /// A decoder closed (or failed to open). When it was the last one, of whichever device generation (decoders of a
    /// lost device can outlive those of its replacement), the factory's current device is idle and released: the GPU
    /// driver keeps tens of MB per device, and the next stream makes a new one in a few milliseconds.
    /// </summary>
    internal void OnDecoderClosed()
    {
        lock (_lock)
        {
            if (--_decoders > 0 || _device is null) return;
            Mf.Release(ref _manager);
            Mf.Release(ref _device);
            Log.Info("video: no stream is decoding; decoding device released");
        }
    }

    /// <summary>A decoder produced a picture on <paramref name="device"/>: it works, so a later loss may recreate it again.</summary>
    internal void OnPicture(ID3D11Device* device)
    {
        lock (_lock)
            if (_device == device && !_proven) { _proven = true; _recreated = false; }
    }

    /// <summary>
    /// A decoder saw <paramref name="device"/> removed (docs/decisions/0018's rule): the next stream gets a new device,
    /// once; a recreated device lost again before it decoded a picture turns hardware decoding off for the session.
    /// </summary>
    internal void OnDeviceLost(ID3D11Device* device, HRESULT reason)
    {
        lock (_lock)
        {
            if (_device != device) return; // already replaced
            Mf.Release(ref _manager);
            Mf.Release(ref _device);
            if (_recreated && !_proven)
            {
                _disabled = true;
                Log.Error($"video: decoding device lost again before a picture ({reason}); hardware decoding is off for this session");
                return;
            }
            _recreated = true;
            Log.Warn($"video: decoding device lost ({reason}); the next stream gets a new one");
        }
    }
}
