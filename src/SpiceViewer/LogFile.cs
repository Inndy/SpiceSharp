using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace SpiceViewer;

/// <summary>
/// viewer.log, which every viewer started from the same folder shares (docs/decisions/0040). The file is opened for
/// append only (FILE_APPEND_DATA without FILE_WRITE_DATA), so Windows puts each write at the current end of the file,
/// whoever else wrote last; each line goes out in one WriteFile call, so lines from two processes alternate whole
/// instead of overwriting each other (finding L15). A FileStream opened with FileMode.Append can't do this: it holds
/// write access and its own position, so the later of two writers overwrites the other's lines.
/// </summary>
sealed unsafe class LogFile : IDisposable
{
    readonly SafeFileHandle _handle;
    readonly object _gate = new();
    byte[] _buffer = new byte[512];
    bool _disposed;

    LogFile(SafeFileHandle handle) => _handle = handle;

    /// <summary>
    /// Opens (or creates) <paramref name="path"/> for appending. A file larger than <paramref name="maxBytes"/> is
    /// emptied first, in place, so another viewer still writing to it carries on at the new end.
    /// </summary>
    public static LogFile Open(string path, long maxBytes)
    {
        try
        {
            if (new FileInfo(path) is { Exists: true } info && info.Length > maxBytes)
                using (new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
        }
        catch (IOException) { } // too big but busy: keep appending; the next start tries again
        var handle = PInvoke.CreateFile(path, (uint)(FILE_ACCESS_RIGHTS.FILE_APPEND_DATA | FILE_ACCESS_RIGHTS.SYNCHRONIZE),
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE, null,
            FILE_CREATION_DISPOSITION.OPEN_ALWAYS, FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, null);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Failure($"can't open {path}", error);
        }
        return new LogFile(handle);
    }

    /// <summary>
    /// Writes <paramref name="line"/> and a CRLF in one append. Thread-safe. A failed or short write throws an
    /// <see cref="IOException"/> and is not retried: writing the rest in a second call could land after another
    /// viewer's line and split this one (the caller decides what a lost line means). Throws
    /// <see cref="ObjectDisposedException"/> after <see cref="Dispose"/>. Allocates only when a line is longer than any
    /// before it, or to report a failure.
    /// </summary>
    public void WriteLine(string line)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int max = Encoding.UTF8.GetMaxByteCount(line.Length) + 2;
            if (_buffer.Length < max) _buffer = new byte[Math.Max(max, _buffer.Length * 2)];
            int n = Encoding.UTF8.GetBytes(line, _buffer);
            _buffer[n++] = (byte)'\r';
            _buffer[n++] = (byte)'\n';
            uint written = 0;
            bool ok, added = false;
            // Dispose is serialized by _gate; the reference also keeps the handle open for the native call itself.
            try
            {
                _handle.DangerousAddRef(ref added);
                fixed (byte* p = _buffer) ok = PInvoke.WriteFile((HANDLE)_handle.DangerousGetHandle(), p, (uint)n, &written, null);
            }
            finally { if (added) _handle.DangerousRelease(); }
            if (!ok) throw Failure("can't append to the log", Marshal.GetLastPInvokeError());
            if (written != n) throw new IOException($"the log took {written} of {n} bytes of a line");
        }
    }

    /// <summary>Closes the file; later writes throw. Waits for a write in progress.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _handle.Dispose();
        }
    }

    static IOException Failure(string what, int error) =>
        new($"{what}: {new System.ComponentModel.Win32Exception(error).Message}", unchecked((int)0x80070000) | (error & 0xFFFF));
}
