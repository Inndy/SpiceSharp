# Task 12 regression tests

2026-10-30. Persistent tests added; no commits, production/native edits, viewer
launch, network/server/VM access, or MJPEG pacing tests. Claude's fixes arrived
concurrently, so several tests already pass against the revised sources.

| Test | Current result |
|---|---|
| `WriterWrapTests.WriterParksWithoutExtraWritesAcrossFullVersionWrap` | Pass: 70,000 separate park/wake cycles, exactly 70,000 nonempty writes/flushes, no extra idle writes. Private `_parked` access is synchronization only, to prevent batching from hiding wrap; assertions concern output, counts and channel health. |
| `ReviewRegressionTests.GlzRgbaImageDecoderPreservesNativeAlpha` | Pass: handmade GLZ RGBA literals first verified through native decoding, then ImageDecoder; alpha 0/128 and HasAlpha retained. No GLZ encoder export is available. |
| `ReviewRegressionTests.RasterCopyPreservesAlphaOnlyOnArgbSurface` | Both ARGB/xRGB cases pass: 0x80402010 / 0xFF402010 respectively. |
| `ReviewRegressionTests.CopyToArgbThenAlphaBlendRetainsTransparency` | Pass: copied offscreen RGBA blends over white to 0xFFBF9F8F. |
| `ReviewRegressionTests.PlaybackChannelEndStopsAudioExactlyOnceWithoutDisconnectingSiblings` | **Fails both EOF and IOException cases:** AudioStarted=1, AudioStopped=0 rather than 1, Disconnected=0; main/display receive tasks remain alive. Uses in-memory channels and awaits completion of the playback loop, not a sleep-based event assertion. |
| `AllocationTests.VisibleDrawTextDoesNotAllocateAfterWarmup` (Replay) | Pass: visible A8 glyph, 256 warm-up draws, then 10,000 draws allocating 0 B; checks pixels and zero canvas errors. |
| `SettingsConcurrencyTests.ConcurrentProcessesPreserveBothVmUpdates` | **Fails:** both child test processes save successfully, final file contains only A. Source-links real Settings.cs; named events coordinate read/modify callbacks, with a bounded wait allowing a correct cross-process lock to serialize B. Temporary settings use a unique test directory, never the user's settings. |
| `SettingsConcurrencyTests.RepeatedSetSuspendedFalseDoesNotRequestFullFrame` | Explicitly skipped: constructing Presenter creates graphics objects/worker; no headless observable frame-request seam. Testing only private pending fields would encode implementation shape. |
| `SettingsConcurrencyTests.OcclusionStopsUploadsAndPresentsUntilVisible` | Explicitly skipped: observing uploads/presents and clearing occlusion requires a window/swap chain/GPU or an injectable production graphics abstraction. |

New cases: **6 pass, 3 fail, 2 skip**. Kept the three failures enabled.
Files: Wire `WriterWrapTests.cs`; Replay `ReviewRegressionTests.cs` and the
existing `AllocationTests.cs`; Viewer `SettingsConcurrencyTests.cs` and its
test-project source-link/child-entry-point configuration.

Commands:

```powershell
dotnet test tests/Spice.Wire.Tests --no-restore --filter FullyQualifiedName~WriterWrapTests --logger "console;verbosity=normal"
dotnet test tests/Spice.Replay.Tests --no-restore --filter "FullyQualifiedName~ReviewRegressionTests|FullyQualifiedName~VisibleDrawText" --logger "console;verbosity=normal"
dotnet test tests/Spice.Viewer.Tests --no-restore -p:BuildProjectReferences=false --filter FullyQualifiedName~SettingsConcurrencyTests --logger "console;verbosity=normal"
```

The normal viewer test dependency build was blocked by concurrent production
compile errors at Presenter.cs:825 (`PInvoke.DXGI_PRESENT_TEST` missing) and :861
(member access on void). The final settings run therefore reused existing
dependency artifacts with BuildProjectReferences=false while compiling the
current source-linked Settings.cs. This is not validation of a complete viewer
build. No production sources were reverted to manufacture failures in fixes
already landed. Only the named new cases were run for this task.
