# Viewer helper and desktop integration tests

Runs on Windows as part of `dotnet test SpiceViewer.sln` (or `dotnet test tests/Spice.Viewer.Tests`). This project targets the
same `net10.0-windows10.0.17763.0` TFM as the viewer. It source-links the current
Viewport.cs and ClipboardFormats.cs so their internal types can be tested without
editing production visibility, constructing a window, or starting SpiceViewer.exe.
CsWin32 generates the helper and desktop integration APIs using the production
allowMarshaling=false configuration. Clipboard tests allocate private movable
HGLOBAL blocks and free them in finally; they never open or change the system
clipboard. Tests use tiny images and never try to exhaust memory.

Coverage includes Fit/1:1 layouts, integer rounding, coordinate mapping and
texture limits; 24/32-bit BMP/DIB orientation, row padding, external/embedded
bit masks and V2/V3/V4/V5 headers; canonical PNG output; invalid offsets,
headers, dimensions, planes, palettes and mask values; bounded UTF-16 blocks.
Those helper tests do not validate the renderer or actual desktop DPI transitions.

`PresenterDesktopTests` additionally creates a small, temporary, nonactivating topmost Win32 window and the real
Presenter with `SPICEVIEWER_FORCE_WARP=1`. It tests idempotent `SetSuspended(false)`, suppression of presents during
minimized suspension, and a pending present after restore without another guest update. It observes public
`PresentCount` / `HasFailed`, pumps window messages on the owning test thread, and restores process-wide debug
settings and destroys the window afterward. The collection is not run in parallel with other collections.

`PresenterDesktopFact` skips with a specific reason only for a noninteractive/inaccessible/background desktop or
an unavailable WARP device. It compares the test desktop with the input desktop, so a sandbox's isolated desktop
does not masquerade as an interactive session. Run from an interactive Windows terminal:

```sh
dotnet test tests/Spice.Viewer.Tests/Spice.Viewer.Tests.csproj --filter FullyQualifiedName~PresenterDesktopTests
```

The old two empty skipped placeholders are removed. R1's DXGI occlusion branch still needs a controlled seam:
minimizing a real WARP flip-model window did not return `DXGI_STATUS_OCCLUDED` on the tested interactive desktop.
The real suspension tests do not claim to exercise that branch or count GPU uploads; see the desktop-test
follow-up in `docs/reviews/2026-10-30-pre-release-commits.md` for the minimal seam request.

Other task-7 tests live in QueuedTransportTests.cs (Spice.Wire.Tests) and
AgentQueueTests.cs / CanvasDeterministicTests.cs (Spice.Replay.Tests).
The canvas tests adapt the supplied differential-harness approach into seeded,
small independent reference checks: 300 random clip unions checked pixel by
pixel and 1000 clipped lines compared with naive Bresenham, plus all 16 A4 levels.
They do not depend on the temporary OldCanvas source tree or live recordings.

Known failing cases are intentionally enabled and documented in
docs/reviews/codex-tests.md. Do not skip them or change the expected results to
match current bugs.
