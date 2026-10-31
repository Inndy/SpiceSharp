using Spice.Session;
using Spice.Wire;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace SpiceViewer.Video;

/// <summary>
/// One stream's hardware decoder (docs/decisions/0037): a synchronous Media Foundation decoder MFT decoding with DXVA
/// into NV12 (or P010) textures on the session's decoding device; <c>ID3D11VideoProcessor</c> converts and scales a
/// picture into a B8G8R8A8 texture of the stream's destination size; a staging texture reused across frames brings it
/// back to the CPU, rows mirrored for a bottom-up stream, into the session's picture, which the canvas copies into the
/// framebuffer clipped by the stream's clip (so later draw commands that read those pixels stay correct).
/// </summary>
/// <remarks>
/// Each STREAM_DATA is one access unit (H.264/HEVC Annex-B with AUD, as x264enc's byte-stream=true aud=true) or one VP9
/// frame: spice-server's GStreamer encoder pushes one raw frame and pulls one compressed buffer per message. The decoder
/// runs in low-latency mode, so a frame comes out of the call that fed it. Nothing managed is allocated per frame: the
/// input sample and its buffer, the processor, the views and the textures are kept until the format changes.
/// </remarks>
sealed unsafe class MfVideoDecoder : IVideoDecoder
{
    const int MaxInputViews = 32;
    /// <summary>D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION: the largest render target or staging texture side.</summary>
    const int MaxOutputSide = 16384;
    const long FrameDuration = 166_667; // 100 ns units; any steady value: the decoder only orders by it

    struct InputView { public ID3D11Texture2D* Texture; public uint Slice; public ID3D11VideoProcessorInputView* View; }

    static readonly Guid IidTexture2D = typeof(ID3D11Texture2D).GUID;
    static readonly Guid IidDxgiBuffer = typeof(IMFDXGIBuffer).GUID;

    readonly HardwareVideo _owner;
    readonly string _name;
    readonly Guid _subtype;
    ID3D11Device* _device;
    ID3D11DeviceContext* _context;
    ID3D11VideoDevice* _videoDevice;
    ID3D11VideoContext* _videoContext;
    IMFDXGIDeviceManager* _manager;
    IMFActivate* _activate;
    IMFTransform* _mft;
    IMFSample* _input;
    IMFMediaBuffer* _inputBuffer;
    uint _inputCapacity;
    long _time;

    // The decoder's output format (from its current output type).
    int _codedW, _codedH;
    RECT _source;
    D3D11_VIDEO_PROCESSOR_COLOR_SPACE _inputColor;

    // Video processor and its targets, for one input size and one output size.
    ID3D11VideoProcessorEnumerator* _vpEnum;
    ID3D11VideoProcessor* _vp;
    int _vpInW, _vpInH, _outW, _outH;
    ID3D11Texture2D* _target;
    ID3D11VideoProcessorOutputView* _targetView;
    ID3D11Texture2D* _staging;
    readonly InputView[] _views = new InputView[MaxInputViews];
    int _viewCount;
    bool _configured, _failed, _pictureSeen, _closed;

    public MfVideoDecoder(HardwareVideo owner, VideoCodecType codec, Guid subtype, string name, ID3D11Device* device,
        IMFDXGIDeviceManager* manager, int width, int height)
    {
        _owner = owner;
        _name = name;
        _subtype = subtype;
        _device = device; device->AddRef();
        _manager = manager; manager->AddRef();
        try
        {
            ID3D11DeviceContext* ctx;
            _device->GetImmediateContext(&ctx);
            _context = ctx;
            _device->QueryInterface<ID3D11VideoDevice>(out var vd).ThrowOnFailure();
            _videoDevice = vd;
            _context->QueryInterface<ID3D11VideoContext>(out var vc).ThrowOnFailure();
            _videoContext = vc;

            IMFTransform* mft = null;
            IMFActivate* activate = null;
            if (HardwareVideo.FindDecoder(subtype, &mft, &activate) is not { } mftName)
                throw new InvalidOperationException("no Direct3D 11 decoder MFT");
            _mft = mft;
            _activate = activate;

            IMFAttributes* attributes;
            _mft->GetAttributes(&attributes);
            try
            {
                // Low latency: a picture comes out for each access unit fed, with no reordering delay.
                var lowLatency = PInvoke.MF_LOW_LATENCY;
                attributes->SetUINT32(&lowLatency, 1);
            }
            finally { attributes->Release(); }
            Mf.ProcessMessage(_mft, MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)manager).ThrowOnFailure();

            IMFMediaType* type;
            PInvoke.MFCreateMediaType(&type).ThrowOnFailure();
            try
            {
                var major = PInvoke.MFMediaType_Video;
                var keyMajor = PInvoke.MF_MT_MAJOR_TYPE;
                var keySub = PInvoke.MF_MT_SUBTYPE;
                var sub = subtype;
                type->SetGUID(&keyMajor, &major);
                type->SetGUID(&keySub, &sub);
                var keyInterlace = PInvoke.MF_MT_INTERLACE_MODE;
                type->SetUINT32(&keyInterlace, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
                if (width > 0 && height > 0)
                {
                    var keySize = PInvoke.MF_MT_FRAME_SIZE;
                    type->SetUINT64(&keySize, Mf.PackSize(width, height));
                }
                _mft->SetInputType(0, type, 0);
            }
            finally { type->Release(); }
            SelectOutputType();

            MFT_OUTPUT_STREAM_INFO info;
            _mft->GetOutputStreamInfo(0, &info);
            if ((info.dwFlags & (Mf.OutputProvidesSamples | Mf.OutputCanProvideSamples)) == 0)
                throw new InvalidOperationException("the decoder does not output Direct3D textures");

            Mf.ProcessMessage(_mft, MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0).ThrowOnFailure();
            Mf.ProcessMessage(_mft, MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0).ThrowOnFailure();
            EnsureInput(64 << 10);
            Log.Info($"video: {name} stream decoder '{mftName}'{(width > 0 ? $" for {width}x{height}" : "")}");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool NeedsEveryFrame => true;

    public bool HasDecoded => _pictureSeen;

    /// <summary>The formats the probed DXVA profiles cover (<see cref="HardwareVideo.CanDecode"/>).</summary>
    public bool Takes(in VideoFormat format) => _owner.CanDecode(format);

    public VideoDecodeResult Decode(ReadOnlySpan<byte> data, VideoOutput? output)
    {
        if (_failed || _mft is null) return VideoDecodeResult.Failed;
        // spice-gtk ignores an empty frame ("got an empty frame buffer!").
        if (data.IsEmpty) return VideoDecodeResult.NoPicture;
        IMFSample* picture = null;
        try
        {
            var hr = Submit(data);
            if (hr.Failed) return Fail(hr, "ProcessInput");
            hr = Drain(&picture);
            if (hr.Failed) return Fail(hr, "ProcessOutput");
            if (picture is null) return VideoDecodeResult.NoPicture;
            // A decoded picture proves the device, whether or not it is shown (a hidden or dropped frame's picture too).
            if (!_pictureSeen)
            {
                _pictureSeen = true;
                _owner.OnPicture(_device);
            }
            return output is null ? VideoDecodeResult.NoPicture : Present(picture, output);
        }
        catch (Exception e)
        {
            // CsWin32's wrappers throw on a failing HRESULT: a lost device or a driver error, never a per-frame event.
            return Fail((HRESULT)e.HResult, e.Message);
        }
        finally { if (picture is not null) picture->Release(); }
    }

    VideoDecodeResult Fail(HRESULT hr, string what)
    {
        _failed = true;
        HRESULT removed = default;
        try { _device->GetDeviceRemovedReason(); }
        catch (Exception e) { removed = (HRESULT)e.HResult; }
        Log.Warn($"video: {_name} decoding failed: {what} ({hr}){(removed.Failed ? $"; device removed ({removed})" : "")}");
        if (removed.Failed) _owner.OnDeviceLost(_device, removed);
        return VideoDecodeResult.Failed;
    }

    /// <summary>Copies the frame into the reused input sample and feeds it to the MFT.</summary>
    HRESULT Submit(ReadOnlySpan<byte> data)
    {
        // The MFT may still hold the sample from the previous frame; then this frame gets a new one.
        if ((uint)data.Length > _inputCapacity || Mf.RefCount((IUnknown*)_input) > 1) EnsureInput(data.Length);
        byte* p;
        _inputBuffer->Lock(&p, null, null);
        try { data.CopyTo(new Span<byte>(p, data.Length)); }
        finally { _inputBuffer->Unlock(); }
        _inputBuffer->SetCurrentLength((uint)data.Length);
        _input->SetSampleTime(_time);
        _input->SetSampleDuration(FrameDuration);
        _time += FrameDuration;
        var hr = Mf.ProcessInput(_mft, _input);
        if (hr == HRESULT.MF_E_NOTACCEPTING)
        {
            // Pictures the MFT still holds: take them (they are older than this frame and not shown), then feed it.
            IMFSample* stale = null;
            var drained = Drain(&stale);
            if (stale is not null) stale->Release();
            if (drained.Failed) return drained;
            hr = Mf.ProcessInput(_mft, _input);
        }
        return hr;
    }

    /// <summary>Takes every picture the MFT has ready; <paramref name="picture"/> gets the newest (the caller releases it).</summary>
    HRESULT Drain(IMFSample** picture)
    {
        for (int guard = 0; guard < 16; guard++)
        {
            MFT_OUTPUT_DATA_BUFFER buffer = default;
            uint status;
            var hr = Mf.ProcessOutput(_mft, &buffer, &status);
            if (buffer.pEvents is not null) buffer.pEvents->Release();
            if (hr == HRESULT.MF_E_TRANSFORM_NEED_MORE_INPUT) return default;
            if (hr == HRESULT.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                if (buffer.pSample is not null) buffer.pSample->Release();
                // A new format (the first frame, or a size change): pick the output type again.
                SelectOutputType();
                continue;
            }
            if (hr.Failed)
            {
                if (buffer.pSample is not null) buffer.pSample->Release();
                return hr;
            }
            if (buffer.pSample is null) continue;
            if (*picture is not null) (*picture)->Release();
            *picture = buffer.pSample;
        }
        return default;
    }

    void EnsureInput(int length)
    {
        Mf.Release(ref _inputBuffer);
        Mf.Release(ref _input);
        uint capacity = Math.Max(64u << 10, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)length));
        IMFSample* sample;
        IMFMediaBuffer* buffer;
        PInvoke.MFCreateSample(&sample).ThrowOnFailure();
        var hr = PInvoke.MFCreateMemoryBuffer(capacity, &buffer);
        if (hr.Failed) { sample->Release(); hr.ThrowOnFailure(); }
        sample->AddBuffer(buffer);
        _input = sample;
        _inputBuffer = buffer;
        _inputCapacity = capacity;
    }

    /// <summary>Chooses NV12 (or P010 for 10-bit) output and reads the picture format from it.</summary>
    void SelectOutputType()
    {
        var keySub = PInvoke.MF_MT_SUBTYPE;
        for (uint i = 0; ; i++)
        {
            IMFMediaType* type = null;
            var hr = Mf.GetOutputAvailableType(_mft, i, &type);
            if (hr == Mf.E_NO_MORE_TYPES) throw new InvalidOperationException("the decoder offers no NV12 or P010 output");
            hr.ThrowOnFailure();
            try
            {
                if (!Mf.TryGetGuid(type, keySub, out var sub) || (sub != PInvoke.MFVideoFormat_NV12 && sub != PInvoke.MFVideoFormat_P010)) continue;
                _mft->SetOutputType(0, type, 0);
                ReadFormat(type);
                return;
            }
            finally { type->Release(); }
        }
    }

    void ReadFormat(IMFMediaType* type)
    {
        int w = 0, h = 0;
        if (Mf.TryGetUInt64(type, PInvoke.MF_MT_FRAME_SIZE, out ulong size)) { w = (int)(size >> 32); h = (int)(uint)size; }
        _source = new RECT(0, 0, w, h);
        if (Mf.TryGetBlob(type, PInvoke.MF_MT_MINIMUM_DISPLAY_APERTURE, out MFVideoArea area) && area.Area.cx > 0 && area.Area.cy > 0)
        {
            int x = area.OffsetX.value, y = area.OffsetY.value;
            _source = new RECT(x, y, Math.Min(w, x + area.Area.cx), Math.Min(h, y + area.Area.cy));
        }
        // Colour: the bitstream's matrix and range as the decoder reports them; without them, what GStreamer assumes
        // for the encoder's input (BT.709 from 720 lines up, BT.601 below; studio range).
        bool bt709 = Mf.TryGetUInt32(type, PInvoke.MF_MT_YUV_MATRIX, out uint matrix) && matrix != 0
            ? matrix != (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT601
            : _source.bottom - _source.top >= 720;
        bool full = Mf.TryGetUInt32(type, PInvoke.MF_MT_VIDEO_NOMINAL_RANGE, out uint range) && range == (uint)MFNominalRange.MFNominalRange_0_255;
        _inputColor = default;
        _inputColor.YCbCr_Matrix = bt709;
        _inputColor.Nominal_Range = (byte)(full ? 2 : 1); // D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255 : _16_235
        _codedW = w;
        _codedH = h;
        _configured = false;
        ReleaseInputViews();
    }

    VideoDecodeResult Present(IMFSample* picture, VideoOutput output)
    {
        IMFMediaBuffer* buffer = null;
        IMFDXGIBuffer* dxgi = null;
        ID3D11Texture2D* texture = null;
        int w = output.DestinationWidth, h = output.DestinationHeight;
        // The session's buffer first: it refuses a size it can't hold (beyond the canvas's and D3D11's 16384 limit) or
        // one over the frame budget. Then nothing is allocated or converted for this picture, the decoder has still
        // advanced, and the conversion targets of an earlier size are freed rather than kept for a refused stream.
        var dst = output.Begin(w, h, mirrored: true);
        if (dst.IsEmpty || w > MaxOutputSide || h > MaxOutputSide)
        {
            ReleaseProcessor();
            return VideoDecodeResult.Dropped;
        }
        try
        {
            picture->GetBufferByIndex(0, &buffer);
            var iid = IidDxgiBuffer;
            if (buffer->QueryInterface(&iid, (void**)&dxgi).Failed) return Fail(default, "the decoder's picture is not a Direct3D texture");
            var texIid = IidTexture2D;
            dxgi->GetResource(&texIid, (void**)&texture);
            uint slice;
            dxgi->GetSubresourceIndex(&slice);

            D3D11_TEXTURE2D_DESC desc;
            texture->GetDesc(&desc);
            if (_codedW <= 0 || _codedH <= 0) { _codedW = (int)desc.Width; _codedH = (int)desc.Height; _source = new RECT(0, 0, _codedW, _codedH); }
            EnsureProcessor((int)desc.Width, (int)desc.Height, w, h);
            var view = InputViewFor(texture, slice);

            if (!_configured)
            {
                var outColor = default(D3D11_VIDEO_PROCESSOR_COLOR_SPACE); // full-range RGB for the framebuffer
                var outRect = new RECT(0, 0, w, h);
                var source = _source;
                var inColor = _inputColor;
                _videoContext->VideoProcessorSetStreamFrameFormat(_vp, 0, D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
                _videoContext->VideoProcessorSetStreamColorSpace(_vp, 0, &inColor);
                _videoContext->VideoProcessorSetOutputColorSpace(_vp, &outColor);
                _videoContext->VideoProcessorSetStreamSourceRect(_vp, 0, true, &source);
                _videoContext->VideoProcessorSetStreamDestRect(_vp, 0, true, &outRect);
                _videoContext->VideoProcessorSetOutputTargetRect(_vp, true, &outRect);
                _videoContext->VideoProcessorSetStreamAutoProcessingMode(_vp, 0, false);
                _configured = true;
            }
            var stream = new D3D11_VIDEO_PROCESSOR_STREAM { Enable = true, pInputSurface = view };
            _videoContext->VideoProcessorBlt(_vp, _targetView, 0, 1, &stream);
            _context->CopyResource((ID3D11Resource*)_staging, (ID3D11Resource*)_target);

            D3D11_MAPPED_SUBRESOURCE mapped;
            // Waits for the GPU (no polling): the copy above is the only work in flight on this texture.
            _context->Map((ID3D11Resource*)_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
            try
            {
                int rowBytes = w * 4;
                bool flip = output.BottomUp;
                for (int y = 0; y < h; y++)
                {
                    var src = new ReadOnlySpan<byte>((byte*)mapped.pData + (long)y * mapped.RowPitch, rowBytes);
                    src.CopyTo(dst.Slice((flip ? h - 1 - y : y) * rowBytes, rowBytes));
                }
            }
            finally { _context->Unmap((ID3D11Resource*)_staging, 0); }
            return VideoDecodeResult.Picture;
        }
        finally
        {
            if (texture is not null) texture->Release();
            if (dxgi is not null) dxgi->Release();
            if (buffer is not null) buffer->Release();
        }
    }

    /// <summary>The processor, its B8G8R8A8 target and the staging copy, for this input and output size.</summary>
    void EnsureProcessor(int inW, int inH, int outW, int outH)
    {
        if (_vp is not null && inW == _vpInW && inH == _vpInH && outW == _outW && outH == _outH) return;
        ReleaseProcessor();
        var content = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
        {
            InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
            InputWidth = (uint)inW, InputHeight = (uint)inH,
            OutputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
            OutputWidth = (uint)outW, OutputHeight = (uint)outH,
            Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_OPTIMAL_SPEED,
        };
        ID3D11VideoProcessorEnumerator* e;
        _videoDevice->CreateVideoProcessorEnumerator(&content, &e);
        _vpEnum = e;
        ID3D11VideoProcessor* vp;
        _videoDevice->CreateVideoProcessor(_vpEnum, 0, &vp);
        _vp = vp;

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)outW, Height = (uint)outH, MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT, BindFlags = D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
        };
        ID3D11Texture2D* target;
        _device->CreateTexture2D(&desc, null, &target);
        _target = target;
        var viewDesc = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC { ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D };
        ID3D11VideoProcessorOutputView* targetView;
        _videoDevice->CreateVideoProcessorOutputView((ID3D11Resource*)_target, _vpEnum, &viewDesc, &targetView);
        _targetView = targetView;

        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging;
        _device->CreateTexture2D(&desc, null, &staging);
        _staging = staging;

        _vpInW = inW; _vpInH = inH; _outW = outW; _outH = outH;
        _configured = false;
    }

    /// <summary>
    /// The processor's view of one decoded picture, kept per (texture, slice): the decoder cycles through a fixed set of
    /// texture-array slices. A view holds its texture, so a cached pointer can't be reused by another texture.
    /// </summary>
    ID3D11VideoProcessorInputView* InputViewFor(ID3D11Texture2D* texture, uint slice)
    {
        for (int i = 0; i < _viewCount; i++)
            if (_views[i].Texture == texture && _views[i].Slice == slice) return _views[i].View;
        if (_viewCount == MaxInputViews) ReleaseInputViews();
        var desc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC { ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D };
        desc.Texture2D.ArraySlice = slice;
        ID3D11VideoProcessorInputView* view;
        _videoDevice->CreateVideoProcessorInputView((ID3D11Resource*)texture, _vpEnum, &desc, &view);
        _views[_viewCount++] = new InputView { Texture = texture, Slice = slice, View = view };
        return view;
    }

    void ReleaseInputViews()
    {
        for (int i = 0; i < _viewCount; i++) Mf.Release(ref _views[i].View);
        Array.Clear(_views);
        _viewCount = 0;
    }

    void ReleaseProcessor()
    {
        ReleaseInputViews(); // created against the enumerator
        Mf.Release(ref _staging);
        Mf.Release(ref _targetView);
        Mf.Release(ref _target);
        Mf.Release(ref _vp);
        Mf.Release(ref _vpEnum);
        _vpInW = _vpInH = _outW = _outH = 0;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        ReleaseProcessor();
        Mf.Release(ref _inputBuffer);
        Mf.Release(ref _input);
        if (_mft is not null)
        {
            try { Mf.ProcessMessage(_mft, MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, 0); } catch (Exception) { }
            Mf.Release(ref _mft);
        }
        if (_activate is not null)
        {
            try { _activate->ShutdownObject(); } catch (Exception) { }
            Mf.Release(ref _activate);
        }
        Mf.Release(ref _videoContext);
        Mf.Release(ref _videoDevice);
        Mf.Release(ref _context);
        Mf.Release(ref _manager);
        Mf.Release(ref _device);
        _owner.OnDecoderClosed();
        _failed = true;
    }
}
