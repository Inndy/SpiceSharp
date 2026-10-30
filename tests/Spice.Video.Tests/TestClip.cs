using SpiceViewer.Video;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace Spice.Video.Tests;

/// <summary>
/// A synthetic H.264 clip made at test time with Windows' inbox Media Foundation H.264 encoder (the software MFT), so no
/// fixture is committed. Each frame has four flat quadrants and a light block that moves 8 pixels to the right per frame,
/// so the P-frames depend on the frames before them. The encoder emits one Annex-B access unit per frame, as
/// spice-server's x264enc does (byte-stream=true); the first carries SPS and PPS.
/// </summary>
static unsafe class TestClip
{
    public const int Width = 320, Height = 240, BlockSize = 32, BlockTop = 8, BlockStep = 8;
    // RGB of each quadrant (top-left, top-right, bottom-left, bottom-right) and of the moving block.
    public static readonly (byte R, byte G, byte B)[] Quadrants = [(200, 40, 40), (40, 180, 60), (50, 60, 200), (128, 128, 128)];
    public static readonly (byte R, byte G, byte B) Block = (235, 235, 235);

    static readonly Lazy<(byte[][] Frames, string? Error)> Clip = new(() =>
    {
        try { return (Encode(60), null); }
        catch (Exception e) { return ([], $"the inbox H.264 encoder failed: {e.Message}"); }
    });

    /// <summary>60 access units, or the reason there are none.</summary>
    public static byte[][] Frames => Clip.Value.Frames;
    public static string? Error => Clip.Value.Error;

    /// <summary>The block's left edge in frame <paramref name="i"/>.</summary>
    public static int BlockLeft(int i) => i * BlockStep % (Width - BlockSize);

    /// <summary>The colour the frame has at (x, y), as RGB.</summary>
    public static (byte R, byte G, byte B) ColorAt(int frame, int x, int y)
    {
        int bx = BlockLeft(frame);
        if (y >= BlockTop && y < BlockTop + BlockSize && x >= bx && x < bx + BlockSize) return Block;
        return Quadrants[(y < Height / 2 ? 0 : 2) + (x < Width / 2 ? 0 : 1)];
    }

    static byte[] Nv12(int frame)
    {
        var data = new byte[Width * Height * 3 / 2];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                var (r, g, b) = ColorAt(frame, x, y);
                // BT.601, studio range (what the decoder side assumes for SD video without other information).
                data[y * Width + x] = (byte)Math.Clamp(Math.Round(16 + (65.481 * r + 128.553 * g + 24.966 * b) / 255), 0, 255);
            }
        int uv = Width * Height;
        for (int y = 0; y < Height / 2; y++)
            for (int x = 0; x < Width / 2; x++)
            {
                var (r, g, b) = ColorAt(frame, x * 2, y * 2);
                data[uv + y * Width + x * 2] = (byte)Math.Clamp(Math.Round(128 + (-37.797 * r - 74.203 * g + 112.0 * b) / 255), 0, 255);
                data[uv + y * Width + x * 2 + 1] = (byte)Math.Clamp(Math.Round(128 + (112.0 * r - 93.786 * g - 18.214 * b) / 255), 0, 255);
            }
        return data;
    }

    static byte[][] Encode(int count)
    {
        PInvoke.CoInitializeEx(COINIT.COINIT_MULTITHREADED);
        PInvoke.MFStartup(Mf.Version, Mf.StartupLite).ThrowOnFailure();
        var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_H264 };
        IMFActivate** list = null;
        uint n = 0;
        // Synchronous MFTs only: the inbox software encoder, never an asynchronous hardware one.
        PInvoke.MFTEnumEx(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER,
            null, &output, &list, &n).ThrowOnFailure();
        if (n == 0) throw new InvalidOperationException("no synchronous H.264 encoder MFT");
        IMFTransform* mft;
        IMFActivate* activate = list[0];
        try
        {
            var iid = typeof(IMFTransform).GUID;
            mft = (IMFTransform*)activate->ActivateObject(&iid);
        }
        finally
        {
            for (uint i = 1; i < n; i++) list[i]->Release();
            PInvoke.CoTaskMemFree(list);
        }
        try
        {
            SetType(mft, PInvoke.MFVideoFormat_H264, output: true);
            SetType(mft, PInvoke.MFVideoFormat_NV12, output: false);
            Mf.ProcessMessage(mft, MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0).ThrowOnFailure();
            Mf.ProcessMessage(mft, MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0).ThrowOnFailure();
            var units = new List<byte[]>();
            for (int i = 0; i < count; i++)
            {
                var sample = Sample(Nv12(i), i);
                try { Mf.ProcessInput(mft, sample).ThrowOnFailure(); }
                finally { sample->Release(); }
                Collect(mft, units);
            }
            Mf.ProcessMessage(mft, MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
            Mf.ProcessMessage(mft, MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, 0).ThrowOnFailure();
            Collect(mft, units);
            if (units.Count != count) throw new InvalidOperationException($"{units.Count} access units for {count} frames");
            return [.. units];
        }
        finally
        {
            mft->Release();
            activate->ShutdownObject();
            activate->Release();
        }
    }

    static void SetType(IMFTransform* mft, Guid subtype, bool output)
    {
        IMFMediaType* type;
        PInvoke.MFCreateMediaType(&type).ThrowOnFailure();
        try
        {
            Guid major = PInvoke.MFMediaType_Video, sub = subtype;
            Guid k;
            k = PInvoke.MF_MT_MAJOR_TYPE; type->SetGUID(&k, &major);
            k = PInvoke.MF_MT_SUBTYPE; type->SetGUID(&k, &sub);
            k = PInvoke.MF_MT_FRAME_SIZE; type->SetUINT64(&k, Mf.PackSize(Width, Height));
            k = PInvoke.MF_MT_FRAME_RATE; type->SetUINT64(&k, 30UL << 32 | 1);
            k = PInvoke.MF_MT_PIXEL_ASPECT_RATIO; type->SetUINT64(&k, 1UL << 32 | 1);
            k = PInvoke.MF_MT_INTERLACE_MODE; type->SetUINT32(&k, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            k = PInvoke.MF_MT_YUV_MATRIX; type->SetUINT32(&k, (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT601);
            if (output)
            {
                k = PInvoke.MF_MT_AVG_BITRATE; type->SetUINT32(&k, 4_000_000);
                k = PInvoke.MF_MT_MPEG2_PROFILE; type->SetUINT32(&k, (uint)eAVEncH264VProfile.eAVEncH264VProfile_Base);
                mft->SetOutputType(0, type, 0);
            }
            else mft->SetInputType(0, type, 0);
        }
        finally { type->Release(); }
    }

    static IMFSample* Sample(byte[] data, int index)
    {
        IMFSample* sample;
        IMFMediaBuffer* buffer;
        PInvoke.MFCreateSample(&sample).ThrowOnFailure();
        PInvoke.MFCreateMemoryBuffer((uint)data.Length, &buffer).ThrowOnFailure();
        byte* p;
        buffer->Lock(&p, null, null);
        data.CopyTo(new Span<byte>(p, data.Length));
        buffer->Unlock();
        buffer->SetCurrentLength((uint)data.Length);
        sample->AddBuffer(buffer);
        buffer->Release();
        sample->SetSampleTime(index * 333_333L);
        sample->SetSampleDuration(333_333);
        return sample;
    }

    static void Collect(IMFTransform* mft, List<byte[]> units)
    {
        while (true)
        {
            MFT_OUTPUT_STREAM_INFO info;
            mft->GetOutputStreamInfo(0, &info);
            IMFSample* sample = null;
            IMFMediaBuffer* buffer = null;
            bool provides = (info.dwFlags & Mf.OutputProvidesSamples) != 0;
            if (!provides)
            {
                PInvoke.MFCreateSample(&sample).ThrowOnFailure();
                PInvoke.MFCreateMemoryBuffer(Math.Max(info.cbSize, 1u << 20), &buffer).ThrowOnFailure();
                sample->AddBuffer(buffer);
            }
            var b = new MFT_OUTPUT_DATA_BUFFER { pSample = sample };
            uint status;
            var hr = Mf.ProcessOutput(mft, &b, &status);
            if (b.pEvents is not null) b.pEvents->Release();
            try
            {
                if (hr == HRESULT.MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                if (hr == HRESULT.MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    IMFMediaType* t;
                    Mf.GetOutputAvailableType(mft, 0, &t).ThrowOnFailure();
                    mft->SetOutputType(0, t, 0);
                    t->Release();
                    continue;
                }
                hr.ThrowOnFailure();
                IMFMediaBuffer* contiguous;
                b.pSample->ConvertToContiguousBuffer(&contiguous);
                byte* p;
                uint length;
                contiguous->Lock(&p, null, &length);
                units.Add(new ReadOnlySpan<byte>(p, (int)length).ToArray());
                contiguous->Unlock();
                contiguous->Release();
            }
            finally
            {
                if (b.pSample is not null && b.pSample != sample) b.pSample->Release();
                if (buffer is not null) buffer->Release();
                if (sample is not null) sample->Release();
            }
        }
    }
}
