# Review: human answers and follow-ups

Reviewed the five commits `20f3919`, `86a4a46`, `8cbafeb`, `82571fb`, and `1bd1e83`
against `main`, after reading AGENTS.md, the design spec/principles, and decisions
0036, 0037, 0041 and 0043, including their Answers sections. Production files were
not edited. Two P2 findings below: one introduced here, one inherited integration
gap relevant to the requested software-only advertising guarantee. No P1 or P3 findings.

## Findings

### AF-R1 - P2: automatic stty leaves the escape filter on the previous guest line

Location: [ConsoleFrontEnd.cs:208](../../src/PveTerm/ConsoleFrontEnd.cs#L208),
[ConsoleFrontEnd.cs:237](../../src/PveTerm/ConsoleFrontEnd.cs#L237), and
[EscapeFilter.cs:67](../../src/PveTerm/EscapeFilter.cs#L67). Introduced by `1bd1e83`.

`SendStty` sends a line ending in CR directly to the channel, without updating the
escape filter's line state. Type `x`, resize with `--auto-stty`, then type `~r`.
The automatic line has ended with Enter, but `_atLineStart` is still false from
the `x`. The explicit resize command is sent as literal `~r` instead of executing.
The same stale state prevents `~.` from disconnecting at that new guest line.
A pending held escape can also outlive the injected Enter, since `_held` is untouched.

Regression: `AnswersFollowupsTests.AutoSttyEnterStartsANewEscapeLine` runs the real
`ConsoleFrontEnd` under a hidden pseudoconsole with an in-memory `ITermChannel`.
It waits for input and automatic stty sends before typing the next keys, and
compares the complete transmitted bytes. Expected two stty lines; actual:
`xstty rows 40 cols 120\r~r\r`. It fails and remains enabled. No socket, guest,
or visible window is involved.

Fix direction: reconcile pending input and the filter's line state when injecting
the stty line, including its final Enter; preserve bracketed-paste tracking and
the disabled-escape path. Do not introduce a timeout or polling.

### AF-R2 - P2: saved softwareVideo=false can lose the race with initial advertising

Location: [Program.cs:74](../../src/SpiceViewer/Program.cs#L74),
[MainWindow.Settings.cs:50](../../src/SpiceViewer/MainWindow.Settings.cs#L50), and
[MainWindow.Session.cs:552](../../src/SpiceViewer/MainWindow.Session.cs#L552).
This ordering already exists in `main`; it is not introduced by these five commits.

The router starts with software enabled. `window.StartSession()` schedules the
connection on the thread pool before `settings.Load()` and `window.Create(settings)`
apply the saved preference. There is no settings-ready barrier before
`DecodableVideoCodecs` snapshots the router's codecs. On WARP or missing MF, the
hardware probe can complete immediately; a delayed settings read/UI thread and a
fast connection can therefore advertise H.264 even though the saved setting is off.
Applying false afterward prevents software decoder creation but cannot retract the
display link's advertised capability. This can lead to unsupported H.264 streams
before the server falls back.

The comment claiming settings are applied before the probe answers is a timing
assumption, not synchronization. The new off test initializes `SoftwareEnabled=false`
before advertising and passes, so it does not cover this production ordering.

Fix direction: establish the saved software preference before codec advertising
can run, while allowing transport connection startup to proceed promptly. This is
source-established concurrency exposure, not a reproduced live viewer failure.
No startup regression is added here: the current startup seam includes the real
window, which this brief forbids opening. A window-free startup/settings seam is
needed to test the actual ordering without duplicating startup logic in a test.

## Other focus areas

- **0041:** `TrimMemory` still trims the GLZ reuse pool and clears spare pixel
  buffer references, preserving admission/rate limiting and live protocol state.
  No `GC.Collect` or other forced-collection call remains in production `src`.
  Dropped arrays become eligible for collection; no immediate managed-memory or
  private-bytes reduction is promised. All seven selected pool/trim tests pass.
  The new collection-count test runs in the nonparallel allocation collection and
  is a useful guard against the removed full collection. It observes process-wide
  GC counts, so a natural/background collection could still cause a false failure;
  passing it does not prove that no collection can ever coincide with a trim.
- **0037:** with the preference already applied, the actual software-adapter probe,
  router and session advertise H.264 when available, decode the software 4:2:0 clip,
  and advertise MJPEG only when software is disabled. All three new video tests
  pass, with no skips. Two existing router/format tests also pass, including keeping
  hardware H.264/VP9 when software is disabled and preferring hardware for 4:2:0.
  The added bool constructor has no production caller passing true; production
  uses the parameterless constructor passing false. There is no new setting,
  command-line or environment route to that test argument. The pre-existing
  `SPICEVIEWER_FORCE_WARP` override remains separate.
- **Package/source notices:** source inspection confirms release 9.0.2, pinned
  download URL and 64-digit SHA-256 from `fetch.sh`, the complete `ff_args` block
  from `build.sh`, and the explicit unmodified/no-patches statement in the FFmpeg
  notice. The note is emitted, and both FFmpeg license texts remain included.
  Evaluating only the read-only block-index expression finds all six configure
  lines (zero-based 37 through 42). The replaceable DLL notice and tracked native
  sources/build scripts remain; the only removed archive payload is the approved
  FFmpeg tarball. Viewer/DLLs, PveTerm, launcher/support files, README and other
  component notices retain their copy paths. This is source-level verification
  against the accepted packaging contract: package.ps1 was not run and no zip was
  built or inspected. External source availability and distribution arrangements
  were not checked offline.
- **0043:** AutoOrder, launcher XAML items and its index-to-kind array all use
  TigerVNC, UltraVNC, TightVNC, RealVNC. The fake detection/order tests pass.
- **0036:** ordinary `~r`, `~~r`, other following keys, VT sequences, bracketed
  paste, and disabled escapes pass the selected pure filter tests. Program enables
  serial handling only for Qemu; both Program and the front end gate auto-stty.
  Resize injection is coalesced once per input-record batch and deduplicated by
  last typed size. It can still type once per batch during a long drag; this is
  batching, not a debounce timer. Formatting uses the fixed UTF-8 buffer, and the
  filter allocation test passes. Output forwarding is unchanged. Node/LXC gating,
  actual stty allocation, and burst resizing were inspected rather than newly
  exercised end to end. The opt-in command can enter arbitrary guest programs,
  including a paste in progress, as the accepted decision explicitly permits.

## Validation

All test invocations used `--no-restore`, one allowed project at a time:

- Pve.Term.Tests: selected escape/filter/allocation and VNC viewer tests, **29 passed**.
- Spice.Video.Tests: SoftwareOnlyVideoTests, **3 passed**, no skips.
- Spice.Replay.Tests: selected PixelBufferTests and trim tests, **7 passed**.
- Spice.Replay.Tests: router advertising and format selection, **2 passed**.
- Pve.Term.Tests: new AnswersFollowupsTests, **1 intentionally failing** (AF-R1).

No full-suite claim. No Spice.Viewer.Tests, visible windows, VMs, Proxmox,
network connections, secret files, package execution, or production edits.

## Response (Claude, 2026-10-31)

- **AF-R1, fixed.** `EscapeFilter.LineSent()` marks the start of a new line; `ConsoleFrontEnd.SendStty` calls it
  after each stty line it sends (`--auto-stty` and `~r`). A held escape character stays held, so the next key decides
  it as at any line start; bracketed-paste state is untouched (inside a paste nothing is an escape anyway); with the
  escape off it does nothing. No timeout or polling. `AnswersFollowupsTests.AutoSttyEnterStartsANewEscapeLine` now
  passes unchanged, and `SttyEscapeTests.ALineTheBridgeSentStartsANewEscapeLine` covers the held-escape, paste and
  disabled cases at the filter.
- **AF-R2, fixed** with a settings-ready gate rather than reading settings.json before connecting, so the transport
  start stays first. `VideoDecoderRouter` takes `awaitSoftwareSetting`; its `Codecs` (what
  `SpiceSession.DecodableVideoCodecs` waits on before advertising) awaits `ApplySoftwareSetting(bool)` as well as the
  hardware probe, and `KnownCodecs` stays null until then. `Program` builds the router with the gate, and
  `MainWindow.ApplySettings` (run at the start of `Create`, tens of milliseconds after `StartSession`) releases it
  with the saved value. The session's existing 5 s probe bound still applies: without an answer it advertises MJPEG
  only. The window-free seam is the router plus the session's advertising step:
  `SoftwareOnlyVideoTests.AdvertisingWaitsForTheSavedSoftwareSetting` uses the real probe as on a software adapter
  (which answers at once, the racy case), checks that advertising has not completed before the setting is applied,
  and that a saved off advertises MJPEG only and a saved on advertises H.264. The real `Program`/`MainWindow` ordering
  is not exercised, since that needs the window; the gate makes the ordering irrelevant to correctness.
- **Validation:** `dotnet build SpiceViewer.sln` 0 warnings; Pve.Term.Tests 204 passed, Spice.Video.Tests 67 passed,
  Spice.Replay.Tests 445 passed. On the collection-count test of 0041: noted; it runs in the non-parallel allocation
  collection and has not been flaky here, so it stays.
