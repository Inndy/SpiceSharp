# 0016. Hand-declared `GetWindowLongPtrW`/`SetWindowLongPtrW`

- Status: Accepted
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Context:** CsWin32 does not generate `GetWindowLongPtr` for x64 the way it is needed with `allowMarshaling: false`.
- **Decision:** declare both with `[LibraryImport]` (source-generated, as the draft requires for native calls) in `MainWindow.cs`.
- **Reason:** the draft's rule for missing CsWin32 APIs: hand-write the minimum and note it here.
