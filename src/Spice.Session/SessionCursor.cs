using Spice.Wire;

namespace Spice.Session;

public sealed partial class SpiceSession
{
    readonly Dictionary<ulong, CursorShape> _cursorCache = [];

    // Recently decoded shapes matched by content. Some guest drivers (the Windows 8.1 QXL driver) never use the
    // server's cursor cache and resend every shape in full; reusing the decoded object then also lets the viewer reuse
    // its native cursor, so a shape change costs neither a decode nor a new HCURSOR.
    const int RecentShapes = 48; // stays below the viewer's 64-entry native cursor cache
    readonly (CursorHeader Header, int Hash, byte[] Data, CursorShape Shape)?[] _recentShapes = new (CursorHeader, int, byte[], CursorShape)?[RecentShapes];
    int _recentNext;

    CursorShape DecodeOrReuse(CursorHeader h, ReadOnlySpan<byte> data)
    {
        var hc = new HashCode();
        hc.AddBytes(data);
        int hash = hc.ToHashCode();
        // Compare everything but the unique id, which such drivers leave at 0 or change per message.
        var key = h with { Unique = 0 };
        foreach (var e in _recentShapes)
            if (e is { } r && r.Hash == hash && r.Header == key && data.SequenceEqual(r.Data)) return r.Shape;
        var shape = CursorShape.Decode(h, data);
        _recentShapes[_recentNext] = (key, hash, data.ToArray(), shape);
        _recentNext = (_recentNext + 1) % RecentShapes;
        return shape;
    }

    /// <summary>The guest cursor changed. Null means hidden.</summary>
    public event Action<CursorShape?>? CursorChanged;
    /// <summary>Server mouse mode: the guest moved its cursor to (x, y).</summary>
    public event Action<int, int>? CursorMoved;

    public CursorShape? CurrentCursor { get; private set; }

    void OnCursorMessage(ushort type, ReadOnlyMemory<byte> m)
    {
        var r = new SpiceReader(m);
        switch (type)
        {
            case CursorMsg.Init:
            {
                short x = r.I16(), y = r.I16();
                r.U16(); r.U16(); // trail length, frequency
                bool visible = r.U8() != 0;
                SetCursor(SpiceCursor.Read(ref r), visible);
                CursorMoved?.Invoke(x, y);
                break;
            }
            case CursorMsg.Set:
            {
                short x = r.I16(), y = r.I16();
                bool visible = r.U8() != 0;
                SetCursor(SpiceCursor.Read(ref r), visible);
                CursorMoved?.Invoke(x, y);
                break;
            }
            case CursorMsg.Move:
                CursorMoved?.Invoke(r.I16(), r.I16());
                break;
            case CursorMsg.Hide:
                CurrentCursor = null;
                CursorChanged?.Invoke(null);
                break;
            case CursorMsg.Reset:
                _cursorCache.Clear();
                break;
            case CursorMsg.InvalOne:
                _cursorCache.Remove(r.U64());
                break;
            case CursorMsg.InvalAll:
                _cursorCache.Clear();
                break;
        }
    }

    void SetCursor(SpiceCursor c, bool visible)
    {
        CursorShape? shape = null;
        if (c.Header is CursorHeader h)
        {
            if (Log.IsEnabled(LogLevel.Debug)) Log.Debug($"cursor: set {h.Unique:x} {h.Width}x{h.Height} flags {c.Flags}");
            if ((c.Flags & CursorFlags.FromCache) != 0)
            {
                if (!_cursorCache.TryGetValue(h.Unique, out shape)) Log.Warn($"cursor {h.Unique:x} not cached");
            }
            else
            {
                try
                {
                    shape = DecodeOrReuse(h, c.Data.Span);
                    if ((c.Flags & CursorFlags.CacheMe) != 0) _cursorCache[h.Unique] = shape;
                }
                catch (WireException e) { Log.Warn($"cursor: {e.Message}"); }
            }
        }
        if (shape is null && c.Header is not null) return; // keep the old cursor rather than flicker
        CurrentCursor = visible ? shape : null;
        CursorChanged?.Invoke(CurrentCursor);
    }
}
