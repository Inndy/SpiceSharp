using System.Buffers.Binary;

namespace Spice.Wire;

/// <summary>A malformed or unsupported message. Parsing untrusted server data raises this, never corrupts memory.</summary>
public sealed class WireException(string message) : Exception(message)
{
    public static WireException Truncated(int needed, int available) => new($"truncated: need {needed} bytes, {available} available");
    public static WireException BadValue(string field, long value) => new($"bad {field}: {value}");
}

/// <summary>
/// Bounds-checked little-endian reader over one message body. SPICE pointer fields are offsets from the
/// start of the message body; <see cref="At"/> resolves them against <see cref="Message"/>.
/// </summary>
public struct SpiceReader
{
    readonly ReadOnlyMemory<byte> _message;
    int _offset;

    public SpiceReader(ReadOnlyMemory<byte> message, int offset = 0)
    {
        if ((uint)offset > (uint)message.Length) throw WireException.BadValue("offset", offset);
        _message = message;
        _offset = offset;
    }

    public readonly ReadOnlyMemory<byte> Message => _message;
    public readonly int Offset => _offset;
    public readonly int Remaining => _message.Length - _offset;

    ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || Remaining < n) throw WireException.Truncated(n, Remaining);
        var s = _message.Span.Slice(_offset, n);
        _offset += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    /// <summary>A slice of the message; no copy.</summary>
    public ReadOnlyMemory<byte> Bytes(int n)
    {
        if (n < 0 || Remaining < n) throw WireException.Truncated(n, Remaining);
        var m = _message.Slice(_offset, n);
        _offset += n;
        return m;
    }

    /// <summary>Reads a u32 size, checks it against <paramref name="max"/>, and returns that many bytes.</summary>
    public ReadOnlyMemory<byte> SizedBytes(string field, int max = 1 << 26)
    {
        uint n = U32();
        if (n > (uint)max) throw WireException.BadValue(field, n);
        return Bytes((int)n);
    }

    public ReadOnlyMemory<byte> Rest() => Bytes(Remaining);

    public void Skip(int n) => Take(n);

    /// <summary>A reader positioned at a SPICE pointer (offset from the message start).</summary>
    public readonly SpiceReader At(uint pointer)
    {
        if (pointer > (uint)_message.Length) throw WireException.BadValue("pointer", pointer);
        return new SpiceReader(_message, (int)pointer);
    }
}

/// <summary>Little-endian writer into a caller-provided span (usually a long-lived channel buffer).</summary>
public ref struct SpiceWriter(Span<byte> buffer)
{
    readonly Span<byte> _buf = buffer;
    int _pos;

    public readonly int Length => _pos;
    public readonly Span<byte> Written => _buf[.._pos];

    Span<byte> Take(int n)
    {
        if (_buf.Length - _pos < n) throw new InvalidOperationException("SpiceWriter buffer too small");
        var s = _buf.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public void U8(byte v) => Take(1)[0] = v;
    public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
    public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
    public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);
    public void U64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v);
    public void I64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), v);
    public void Bytes(ReadOnlySpan<byte> b) => b.CopyTo(Take(b.Length));
    public readonly void PatchU32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(_buf.Slice(at, 4), v);
}
