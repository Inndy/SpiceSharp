# 0015. Windows TFM `net10.0-windows10.0.17763.0`

- Status: Accepted
- Date: 2026-10-29
- Decided by: agent (spec silent), reviewed by Codex

- **Decision:** SpiceViewer targets `net10.0-windows10.0.17763.0` with the same `SupportedOSPlatformVersion`.
- **Reason:** we call APIs from Windows 10 1607+ (`GetDpiForWindow`, `AdjustWindowRectExForDpi`) and DXGI 1.3; this makes the platform analyzer check against what we actually require. The AOT binary size is unchanged (no WinRT projection is used). Windows 10 support is best effort (see below).
