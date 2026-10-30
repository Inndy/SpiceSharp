# Idle memory after a busy period (K1), 2026-10-31

Follows finding K1 of [2026-10-30-v0-dod.md](2026-10-30-v0-dod.md). The question was where the 100+ MB that a busy
period leaves in an idle viewer sits. This records what could be released on existing events, at the cost of one
collection and a pool re-warm per trim (no timers, no per-frame work), and the result of [decision 0041](../decisions/0041-idle-memory-is-trimmed-on-minimize-and-video-end.md).

**Setup:**

- Host: as in the DoD record (Windows 11 IoT LTSC 2024, AMD Radeon 8060S, 3840x2160, 32 logical CPUs).
- Guests:
  - `win81` (1616x1044, image draws, no video streaming);
  - `win7` (1280x800, `streaming-video=all`, MJPEG).
- Builds, each an AOT publish with `-c Release -r win-x64` and default settings, run with `--debug`:
  - **base:** `55ba8cc`;
  - **diag:** base plus a temporary in-process breakdown, not committed;
  - **final:** this change, without the diagnostics.
- Every connection used a fresh `.vv` from `tools/pve-vv.py`.

## Method

**External readings.** A scratch script read, from outside the process:

- private bytes and working set (`GetProcessMemoryInfo`);
- a `VirtualQueryEx` walk of committed private pages, grouped by allocation. The GC's reservations (≥ 1 GB
  reserved) count as "GC". The rest of the private pages count as "native", which covers heaps, the driver and
  stacks.

**In-process readings (diag build).** Logged when the window was minimized:

- `GC.GetGCMemoryInfo` (heap size, committed bytes, per-generation size);
- `HeapSummary` of every process heap, and a `HeapWalk` histogram of busy blocks of 256 KiB and more, with their
  exact sizes;
- the image cache's size.

The diag build also tried these after one another, logging each:

- a compacting `GC.Collect`;
- an aggressive `GC.Collect`;
- `HeapCompact`;
- clearing the GLZ window (this breaks the session, so it was used only to size the window);
- on the presenter thread, `ClearState` + `Flush` + `IDXGIDevice3::Trim`.

**Workloads.** The guest-side scripts ran in the interactive session through a scheduled task. No CPU busy-loops
were used.

- **K1 workload, win81:** the DoD's 20 s window painting photo-like 480x320 bitmaps about 30 times a second, plus
  three system sounds.
- **Mixed, win81, 30 s:** a 900x640 painter whose window changes size every second, and a second window scrolling a
  4,000-line text. During it, the viewer window was resized three times from the host (each a guest resolution
  request), and the same three sounds played.
- **MJPEG, win7:** the same painter for 25 s, sent as a 480x320 MJPEG stream (about 510 frames), plus three sounds.

**Schedule.** Readings were taken at connect, 10 s and 70 s after the workload (window visible, idle), after a
minimize, and after the restore.

## Where the memory was (base and diag builds, win81, K1 workload)

| Reading | Value |
|---|---|
| Private bytes, connect → 10 s after | 81.8–83.9 → 183–194 MB (three runs; a fourth started at 97.4 and reached 178.2) |
| Change over the next 60 s idle, and on minimize (base) | none (±0.1 MB) |
| GC heap committed after | 31–44 MB (17.7–25 at connect). A full GC left LOH 22–28 MB live with an empty image cache: spare arrays held by `ArrayPool<byte>.Shared` |
| Default process heap, allocated | 18 → 97 MB, nearly all in 33 blocks ≥ 256 KiB |
| GLZ window + reuse pool | 54.5–55 MB: clearing the window freed it (25 × 1,359 KiB and 6–7 MB images). The pool held 19–22 MB of that, so the window held about 33 MB, as the 8M-pixel dictionary the client advertises allows |
| GPU driver (AMD user-mode driver) | one block that doubles as it grows: 8 MiB at connect, 16 or 32 MiB after; plus 4 MiB and 1 MiB blocks; about 10 MB more outside the heap. None of these exist under WARP (`SPICEVIEWER_FORCE_WARP=1`): native private memory was 5.5 MB at connect and 6.2 MB after the GLZ clear |
| `IDXGIDevice3::Trim` (after `ClearState` + `Flush`), `HeapCompact` | no change in private bytes |
| Compacting or aggressive GC alone | −0 to −2.4 MB: the shared pool still holds its arrays |

The mixed workload grew more, from 85.2 to 264.1 MB (base). The GC committed 81 MB, with 71 MB of LOH live in the
shared pool. The default heap held 107 MB: GLZ about 70 MB, the driver's 32 MiB block, and smaller blocks.

## GC settings (base build, K1 workload, through `DOTNET_` variables, which Native AOT reads)

| Setting | Private, connect → 10 s after | GC committed after | Native after |
|---|---|---|---|
| default | 81.8 → 193.7 MB | 44.0 MB | 143.6 MB |
| `gcConcurrent=0` | 83.0 → 182.1 MB | 36.3 MB | 139.7 MB |
| `GCgen0size=0x400000` | 84.1 → 170.2 MB | 32.8 MB | 131.3 MB |
| `GCConserveMemory=5` | 83.7 → 187.2 MB | 32.0 MB | 149.1 MB |

The GC differences (12 MB) are within the run-to-run spread of the native part (131–149 MB) under the same
workload. The GC heap is live pooled arrays, not gen-0 garbage, because the hot paths don't allocate (decision
0009). No GC setting was adopted.

## After decision 0041 (final build)

| Run | Connect | 10 s after (visible) | 70 s after (visible) | Minimized | Trim log |
|---|---|---|---|---|---|
| win81 K1 workload, base | 97.4 | 178.2 | 178.1 | 178.1 | — |
| win81 K1 workload, final | 82.7 | 161.5 | 161.4 | **127.9** | GLZ pool 22.2 MB, pixel buffers 10.3 MB; GC 15.9 MB committed; 3.3 ms |
| win81 mixed, base | 85.2 | 264.1 | 264.0 | 264.0 | — |
| win81 mixed, final | 82.7 | 208.5 | 208.3 | **160.5** | GLZ pool 31.7 MB, pixel buffers 15.9 MB; GC 16.6 MB committed; 4.8 ms |
| win7 MJPEG, base | 95.4 | 127.2 | 127.1 | 127.1 | — |
| win7 MJPEG, final | 103.8 | **102.2** | 102.1 | 102.0 | at the stream's end: pixel buffers 18.0 MB, 7.4 ms; at minimize: nothing left, 0.7 ms |

Private bytes in MB. The win7 connect reading varies by up to 10 MB between runs: other final runs read 94.3 and 96.1
at connect, and 104.6 10 s after the video.

- **Visible, after a busy period, before any event:** the owned pool reuses arrays better than the shared pool. The
  GC commit was 27.6–34.1 MB, against 42.5–81.3 MB in the base build. Nothing else changes until an event.
- **Minimize:** the GLZ reuse pool and every spare pixel buffer go, and the GC returns its free space. Restoring
  caused no reallocation: restore re-uploads the surface, nothing is decoded.
- **End of a video (win7):** the stream frame and the frame pool already went at stream end. Now the spare decode
  buffers go too, and the GC returns them. The viewer was back at its connect level while the window stayed
  visible.
- **What stays, about 45 MB above connect on win81 after minimize:**
  - the GLZ window's images (about 32 MB), which later GLZ images may reference;
  - the AMD driver's grown allocations (about 25 MB);
  - the display channel's receive buffer (sized to the largest message, 8 MiB here).
  On win7 what stays is the GLZ window (8 × 600 KiB), the image cache (26 images, 5.7 MB) and the driver's 16 MiB
  block.
- **Trims during the busy period:** in a mixed run the window was minimized and restored three times while the
  guest drew. The trims freed 28.8, 31.7 and 21.8 MB of GLZ pool, in 3.0–4.5 ms each, and the log showed no canvas or
  decode warning. Decoding continued, including GLZ images that referenced the window.
- **Occlusion:** a topmost window covering the whole screen while the guest drew produced no
  `DXGI_STATUS_OCCLUDED` from the windowed flip-model present. Occlusion therefore isn't a trigger (decision 0041).

## Tests

- `dotnet build SpiceViewer.sln`: 0 warnings. `dotnet test SpiceViewer.sln`: all projects pass. The new tests:
  - `PixelBufferTests`: reuse, trim, the limits, and 0 B for warm rent and return;
  - stream-end trim and its 30 s interval;
  - `GlzTrimPoolFreesRetiredImagesOnly`.
- Native:
  - `native/verify.py x64` passes with 20 spicecodec exports;
  - `native/difftest/run-review-regressions.sh` passes, release and `--asan`. The new K1 case checks that the trim
    frees the pooled bytes it reports, that the current image stays and that decoding continues;
  - `native/difftest/run.sh --quick` (differential test against `HEAD`) passes.

## Guest and host state

- `C:\sstest\busy.ps1` was copied to both guests and deleted afterwards. The `C:\sstest` folder created on win81 was
  removed. No scheduled tasks remain, and no guest settings changed.
- Every viewer started was closed. The `.vv` files were deleted (the viewer does it, `delete-this-file=1`; the
  launch script checked).
