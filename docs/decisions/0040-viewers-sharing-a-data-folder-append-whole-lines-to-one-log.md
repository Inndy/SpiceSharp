# 0040. Viewers sharing a data folder append whole lines to one viewer.log

- Status: Accepted
- Date: 2026-10-31
- Decided by: agent (spec silent), reviewed by Codex ([../reviews/2026-10-31-l13-l15.md](../reviews/2026-10-31-l13-l15.md))

- **Context:** finding L15 of the Ubuntu re-check
  ([2026-10-30-ubuntu.md](../verification/2026-10-30-ubuntu.md#re-check-l10l12-2026-10-31-00540100)). Running two
  viewers (two VMs) from one portable folder is normal use, and both write `data\viewer.log`. Each opened it with
  `FileMode.Append`: .NET opens the file with write access, seeks to the end once and then writes at its own
  position, so the later of two writers overwrote the other's lines, and their text was spliced inside a line. The
  spec is silent on the log file; [principles.md](../design/principles.md) asks that logs say what happened.
- **Decision:**
  - The log stays one file, `viewer.log`, in the same place. `LogFile` (`src/SpiceViewer/LogFile.cs`) opens it with
    `CreateFileW` for `FILE_APPEND_DATA | SYNCHRONIZE` only, without `FILE_WRITE_DATA`, sharing read, write and delete.
    For such a handle Windows writes every `WriteFile` at the current end of the file, whichever process wrote last.
  - Each line (timestamp, `[pid]`, level, message, CRLF) is encoded into a reused buffer and written with a single
    `WriteFile`, so lines from several processes alternate whole, in time order. The process ID already on every
    line tells the viewers apart.
  - A failed or short `WriteFile` throws an `IOException` and is not retried: a second call for the rest could
    land after another viewer's line and split this one. Logging is best effort for the viewer: its sink catches the
    exception, drops the line and counts it, and after the next line that gets through writes a warning with the
    number of lines lost. Nothing in the viewer fails because a log line couldn't be written. Writes after `Dispose`
    throw `ObjectDisposedException`; `Dispose` waits for a write in progress, and the handle is referenced
    (`DangerousAddRef`) for the duration of the native call (review
    [2026-10-31-l13-l15.md](../reviews/2026-10-31-l13-l15.md), R2 and R3).
  - Over 1 MB at startup, the file is emptied in place (opened with `FileMode.Truncate`) instead of deleted, so a
    viewer still running carries on at the new end rather than writing to a deleted file. If the file can't be
    truncated just then, the viewer appends and the next start tries again.
  - `LogFileTests` (in `tests/Spice.Viewer.Tests`) check two writers on one file, taking turns and concurrently, and
    the in-place truncation with another writer open.
- **Reason:** one file, read top to bottom, is the most transparent result: the user sends one log, and the
  interleaving shows what the two viewers did at the same moment. A log per process (`viewer-<pid>.log`) would leave
  files to clean up and would need its own size rule; an exclusive open with a fallback name makes the file name
  depend on start order. Append-only access costs nothing at run time (one system call per line, as before, and no
  `StreamWriter` buffer). Costs: two viewers starting together over 1 MB may each truncate; a few lines of the other
  are then lost, which the old delete did as well.
