using Spice.Wire;

namespace Spice.Session;

/// <summary>
/// The coding format a stream's bitstream declares (docs/decisions/0037): the profile, chroma format and bit depth that
/// decide whether a hardware decoder can take it. <see cref="ChromaFormat"/> follows H.264's chroma_format_idc
/// (0 monochrome, 1 4:2:0, 2 4:2:2, 3 4:4:4). <see cref="BitDepth"/> is the larger of the luma and chroma depths. VP9's
/// chroma format and depth are placeholders from its profile number (not read from its colour config).
/// </summary>
public readonly record struct VideoFormat(VideoCodecType Codec, int Profile, int ChromaFormat, int BitDepth)
{
    public override string ToString()
    {
        string chroma = ChromaFormat switch { 0 => "4:0:0", 1 => "4:2:0", 2 => "4:2:2", 3 => "4:4:4", _ => "?" };
        return Codec switch
        {
            VideoCodecType.H264 => $"H.264 {H264ProfileName(Profile)} (profile {Profile}), {chroma}, {BitDepth}-bit",
            VideoCodecType.Vp9 => $"VP9 profile {Profile}",
            _ => $"{Codec} profile {Profile}",
        };
    }

    static string H264ProfileName(int profile) => profile switch
    {
        66 => "Baseline", 77 => "Main", 88 => "Extended", 100 => "High", 110 => "High 10", 122 => "High 4:2:2",
        244 => "High 4:4:4 Predictive", 44 => "CAVLC 4:4:4 Intra", _ => "other",
    };
}

/// <summary>Reads a <see cref="VideoFormat"/> from a stream frame, without allocating.</summary>
public static class VideoBitstream
{
    /// <summary>
    /// The format the frame declares: an H.264 sequence parameter set (Annex B, as spice-server's x264enc sends) or a VP9
    /// frame header. False when the frame carries none (an H.264 frame without SPS) or the codec isn't read (HEVC).
    /// </summary>
    public static bool TryReadFormat(VideoCodecType codec, ReadOnlySpan<byte> frame, out VideoFormat format)
    {
        format = default;
        return codec switch
        {
            VideoCodecType.H264 => TryReadH264(frame, out format),
            VideoCodecType.Vp9 => TryReadVp9(frame, out format),
            _ => false,
        };
    }

    static bool TryReadVp9(ReadOnlySpan<byte> frame, out VideoFormat format)
    {
        format = default;
        // VP9 bitstream spec 6.2: frame_marker (2 bits, 0b10), profile_low_bit, profile_high_bit.
        if (frame.IsEmpty || frame[0] >> 6 != 2) return false;
        int profile = (frame[0] >> 5 & 1) | (frame[0] >> 4 & 1) << 1;
        // Profiles 0 and 1 are 8-bit, 2 and 3 are 10 or 12-bit; 0 and 2 are 4:2:0 (the exact subsampling of 1 and 3 is
        // in the key frame's colour config; 4:4:4 stands for "not 4:2:0").
        format = new VideoFormat(VideoCodecType.Vp9, profile, profile is 0 or 2 ? 1 : 3, profile < 2 ? 8 : 10);
        return true;
    }

    static bool TryReadH264(ReadOnlySpan<byte> frame, out VideoFormat format)
    {
        format = default;
        int i = 0;
        while (true)
        {
            int start = frame[i..].IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
            if (start < 0) return false;
            i += start + 3;
            if (i >= frame.Length) return false;
            if ((frame[i] & 0x1F) != 7) continue; // nal_unit_type 7: SPS
            if ((frame[i] & 0x80) != 0) return false; // forbidden_zero_bit
            var nal = frame[(i + 1)..];
            return TryReadSps(nal[..NalLength(nal)], out format);
        }
    }

    /// <summary>
    /// The length of a NAL unit's payload: up to the next 00 00 00, 00 00 01 or 00 00 02, which can't occur inside one
    /// (7.4.1), so the next NAL's start code and header are never read as this one's syntax.
    /// </summary>
    static int NalLength(ReadOnlySpan<byte> data)
    {
        int at = 0;
        while (true)
        {
            int z = data[at..].IndexOf((ReadOnlySpan<byte>)[0, 0]);
            if (z < 0) return data.Length;
            at += z;
            if (at + 2 >= data.Length) return data.Length; // trailing zero bytes
            if (data[at + 2] <= 2) return at;
            at++;
        }
    }

    /// <summary>
    /// H.264 7.3.2.1.1, the prefix up to bit_depth_chroma_minus8, with the semantics of its fields checked (7.4.2.1.1):
    /// reserved_zero_2bits, seq_parameter_set_id 0..31, chroma_format_idc 0..3, both bit depths 8..14. Not a full SPS
    /// validation: the syntax after the depths isn't read.
    /// </summary>
    static bool TryReadSps(ReadOnlySpan<byte> rbsp, out VideoFormat format)
    {
        format = default;
        var r = new RbspReader(rbsp);
        int profile = r.Bits(8);
        int constraints = r.Bits(8);
        r.Bits(8); // level_idc
        if ((constraints & 3) != 0) return false; // reserved_zero_2bits
        if (r.Ue() > 31) return false; // seq_parameter_set_id
        int chroma = 1, lumaDepth = 0, chromaDepth = 0;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chroma = r.Ue();
            if (chroma > 3) return false;
            if (chroma == 3) r.Bits(1); // separate_colour_plane_flag
            lumaDepth = r.Ue();
            chromaDepth = r.Ue();
        }
        if (r.Overrun || lumaDepth > 6 || chromaDepth > 6) return false;
        format = new VideoFormat(VideoCodecType.H264, profile, chroma, 8 + Math.Max(lumaDepth, chromaDepth));
        return true;
    }

    /// <summary>
    /// Reads bits of a NAL unit payload, dropping emulation prevention bytes (00 00 03). A byte after one that is over 3
    /// can't occur (7.4.1); it ends the read as damage.
    /// </summary>
    ref struct RbspReader(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> _data = data;
        int _pos, _zeros, _bit = 8;
        byte _cur;
        public bool Overrun;

        int Bit()
        {
            if (_bit == 8)
            {
                if (_zeros >= 2 && _pos < _data.Length && _data[_pos] == 3)
                {
                    _pos++;
                    _zeros = 0;
                    if (_pos < _data.Length && _data[_pos] > 3) { Overrun = true; return 0; }
                }
                if (_pos >= _data.Length) { Overrun = true; return 0; }
                _cur = _data[_pos++];
                _zeros = _cur == 0 ? _zeros + 1 : 0;
                _bit = 0;
            }
            return _cur >> (7 - _bit++) & 1;
        }

        public int Bits(int n)
        {
            int v = 0;
            for (int k = 0; k < n; k++) v = v << 1 | Bit();
            return v;
        }

        /// <summary>Exp-Golomb ue(v); a run of more than 16 leading zeros is treated as damage.</summary>
        public int Ue()
        {
            int zeros = 0;
            while (Bit() == 0 && !Overrun) if (++zeros > 16) { Overrun = true; return 0; }
            return (1 << zeros) - 1 + Bits(zeros);
        }
    }
}
