namespace Pve.Term;

/// <summary>
/// The byte-stream seam between a terminal transport and a front end (docs/decisions/0036): PveTerm's console and
/// Telnet bridges today, a built-in VT later. Output is the remote program's raw bytes; input is what a terminal
/// would type, already encoded (UTF-8, VT sequences).
/// </summary>
public interface ITermChannel : IAsyncDisposable
{
    /// <summary>Reads terminal output into <paramref name="buffer"/>. Returns 0 once the session has ended; then
    /// <see cref="CloseReason"/> says why.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Sends input. After the session ended, input is dropped (the next read returns 0).</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default);

    /// <summary>Tells the remote side the terminal's size in cells; repeats of the last size are not sent.</summary>
    ValueTask ResizeAsync(int cols, int rows, CancellationToken cancellationToken = default);

    /// <summary>Why the session ended, in words for the user; null while it runs.</summary>
    string? CloseReason { get; }
}
