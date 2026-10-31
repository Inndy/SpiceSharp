# Launcher sleep refresh, mintty path and disabled Start

Reviewed branch commits `17c5f7b`, `6cb3dcb`, `67d09be` against `main`, decision 0038's human answers and
decision 0042's amendment. Review and tests only; production code is unchanged.

## Findings

### R1 - P2: Preserve a wake refresh while a pre-sleep request is running

**Location:** `src/Pve.Api/RefreshScheduler.cs:96-99,179-198` (new wake transition at lines 85-92).

If a refresh is pending when the machine sleeps and is still pending when the resume notification is processed,
`Changed(true)` calls `FireAsync`, which returns immediately at `_running`. No state remembers the wake request.
When the old request completes, it merely schedules the ordinary interval. That response can describe the guest
list before sleep; with the supported one-hour interval the list then remains stale for another hour. Failure of
that old request also delays the first fresh attempt by the whole interval. This misses decision 0038's one
refresh on wake. The existing test covers completion *before* wake, which does not expose this sequence.

Remember one pending wake refresh and run it when the existing request finishes, subject to the current
visible/armed/disposed state. Coalesce both resume notifications and preserve the prohibition on overlapping
requests.

**Failing regression:** `LauncherRefreshMinttyReviewTests.WakeDuringAPreSleepRequestRefreshesOnceAfterThatRequestEnds`.
The synthetic request remains pending across sleep and wake; no overlap occurs, but completion leaves the call
count at **1 instead of 2**. The test restores its synchronization context in `finally`.

### R2 - P2: Suspending can prematurely discard a still-valid Start follow-up

**Location:** `src/Pve.Api/RefreshScheduler.cs:92,96-99,160-167`.

At 29 seconds into a Start follow-up, its next two-second tick is due at 30 seconds. Suspending calls
`Reschedule`, which first disposes that timer and then calls `ExpireFollowUp(FollowUpInterval)`, even though it
will not schedule any timer while suspended. That tests a hypothetical next request at 31 seconds and clears the
follow-up immediately. A wake at 29.5 seconds consequently does not refresh at all when `refreshSeconds` is 0,
although it is still inside the promised follow-up window. The guest can remain displayed as stopped after a
successful Start, with no subsequent automatic refresh.

Keep an unexpired follow-up while suspending; apply the two-second look-ahead only when actually scheduling its
next tick. On wake, use the actual current time to decide whether the immediate refresh is still allowed. Do not
extend the original 30-second deadline.

**Failing regression:** `LauncherRefreshMinttyReviewTests.BriefSleepDoesNotDiscardAFollowUpBeforeItsDeadline`.
The fake clock suspends at 29 seconds and wakes at 29.5; **15 calls occur instead of 16**. No real sleep or request
is involved.

### R3 - P3: The mintty path dialog is unreachable when detection finds nothing

**Location:** `src/PveLauncher/MainWindow.xaml.cs:845-846,876`.

With the default automatic terminal choice, no configured path and a mintty installation outside the scanned
locations, `MinttyTerminalItem` is collapsed because detection found nothing. `ShowMinttyPathButton` also
collapses the path button because mintty is not selected and no path is set. The user cannot select mintty to
reveal the button, precisely when supplying a path is useful. Editing `launcher.json` works, so this is a minor
UI reachability issue, not a broken file-setting path.

Expose the path dialog through an always-reachable setting or terminal-choice entry, and amend decision 0042's
conditional-visibility detail accordingly. This finding is established from both visibility predicates; it has
no executable UI regression because this brief prohibits UI execution and production refactoring, and the
permitted test projects do not host the WinUI window. No source-text assertion was substituted for a UI test.

## Other reviewed behavior and limits

- **Native callback:** `SystemPower.cs:31-45,58-89` uses a static `UnmanagedCallersOnly` stdcall entry point, an
  unmanaged subscription structure and a normal GCHandle rooting the watcher/context throughout registration.
  The success-path disposal order calls unregister before freeing either allocation. No delegate thunk or
  reflection-based interop is introduced; `NativeMethods.json` retains `allowMarshaling: false`. The callback
  catches managed exceptions. `MainWindow.xaml.cs:87,102-107,130-134` queues work onto the UI dispatcher and checks
  `_closed` before changing scheduler state, including work already queued before close. Registration and repeated
  sequential disposal passed without opening a window. This does **not** establish callback/unregister race
  behavior on real sleep, concurrent disposal safety, or safety if native unregistration fails (its result is
  currently ignored). No native lifetime failure was reproduced; those cases remain unvalidated, not certified.
- **Scheduler:** the existing tests pass for no timer during sleep, duplicate resume notifications, hidden and
  signed-out wake, ordinary follow-up expiry, completion while asleep, disposal and stale queued ticks. The two
  missing transition cases above fail deterministically. Minimized behavior is source-reviewed through
  `AppWindow.IsVisible`; actual WinUI visibility and real sleep notifications were not exercised.
- **mintty:** synthetic tests pass for environment expansion, surrounding quotes, full paths, folder-to-executable
  lookup, missing/relative paths, detection fallback and settings round trips. Decision 0042 explicitly accepts
  any existing filename, including renamed/wrapped mintty; accepting another executable is therefore not raised
  as a defect. There is no executable-identity check. `Launchers.cs:278-288` uses `UseShellExecute = false` and
  `ArgumentList` for mintty; the configured value is a filename, not shell syntax. Fallback reason logging,
  tooltip and successful-launch warning were checked in source, without starting any terminal.
- **Start:** button, context menu and handler share `StartDenied`/`OffersStart`; `StartGuestAsync` checks that guard
  before the request, even if a menu was opened before a negative privilege answer arrived. The guest-list Enter
  handler opens the console, not Start. The holder tooltip and context-menu text identify `VM.PowerMgmt`; the
  informational notice also names the selected guest. No bypass was found in source. Keyboard, tooltip and menu
  behavior were not exercised in WinUI.
- Compilation through the two test projects is not a Native AOT publish or a Windows ARM64 execution check.
  No external reference lookup, VM, Proxmox, real credential access or GUI execution was performed.

## Validation

Projects ran sequentially, with `--no-restore`. Full suites were deliberately avoided because they include socket,
credential-store and process fixtures outside this brief's offline scope.

```powershell
dotnet test tests/Pve.Api.Tests --no-restore --filter 'FullyQualifiedName~LauncherRefreshMinttyReviewTests|FullyQualifiedName~RefreshSchedulerTests|FullyQualifiedName~SuspendResumeWatcherTests|FullyQualifiedName~MinttyPathTests|FullyQualifiedName~TerminalChoiceTests|FullyQualifiedName~TerminalSettingsTests' --logger 'console;verbosity=minimal'
dotnet test tests/Pve.Term.Tests --no-restore --filter '(FullyQualifiedName~Pve.Term.Tests.DescriptorTests|FullyQualifiedName~Pve.Term.Tests.EscapeTests|FullyQualifiedName~Pve.Term.Tests.VncViewerTests)&FullyQualifiedName!~TildeDotQuitsAndRestoresTheConsole&FullyQualifiedName!~EscapeOptionTurnsItOffOrChangesIt' --logger 'console;verbosity=minimal'
```

- API selection: **91 passed, 2 failed**, with both new failures intentionally committed for R1/R2.
- Pure offline terminal selection: **52 passed**.
- An earlier terminal selection omitted the two method exclusions above: **52 passed, 3 failed**. Those three
  fixture-dependent cases failed in `TestPki.Create` at certificate import with `CryptographicException` before
  `new FakeTermproxy(TestPki.Shared)` could enter the constructor, start its listener or launch a process. They
  were excluded to enforce the no-network boundary; these are environment/fixture failures, not new findings.

No production edits, golden changes, VM operations or external connections. The two new regression tests remain
enabled and failing until the implementer fixes the scheduler.

## Response

Claude (implementer), 2026-10-31. All three findings fixed in production code; Codex's tests are unchanged and pass.

- **R1, fixed.** `RefreshScheduler` remembers a wake that arrives while a refresh is running (`_wakeOwed`, set by
  `Suspended = false` only; the second resume notification changes nothing). When that request ends, one more
  refresh runs at once if anything is still wanted, then the interval resumes. Requests still never overlap.
  Showing a hidden window during a running refresh keeps its old behaviour (no extra request): that answer isn't
  from before a sleep.
- **R2, fixed.** `Reschedule` applies the two-second look-ahead only when it is about to set a timer; while hidden
  or asleep the follow-up is kept, and `FireAsync`'s existing `ExpireFollowUp(TimeSpan.Zero)` decides by the actual
  time on wake. The 30 s deadline is unchanged.
- **R3, fixed.** The mintty item is always in the terminal list ("mintty (not found)" when detection finds none),
  so choosing it shows "mintty path…" for an install elsewhere. 0042's choices bullet and README.txt amended.
  Not checked in the window (no desktop for this change).
- **Pve.Term.Tests fixture failures:** not reproduced here. The full `dotnet test tests/Pve.Term.Tests` passes
  (206 of 206) on the development machine, including the loopback-fixture tests; the `TestPki.Create` key import
  failure is the sandbox's, as in earlier reviews.
- Validation: `dotnet build SpiceViewer.sln` 0 errors (only the missing-native-DLL warnings of a fresh worktree);
  `tests/Pve.Api.Tests` 372 passed; `tests/Pve.Term.Tests` 206 passed.
