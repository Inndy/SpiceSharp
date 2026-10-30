# Launcher smoke fixes review

Codex, 2026-10-31. Reviewed all three commits after `b16987d`: `65ce3ca` (B5/B6 API changes),
`d608363` (launcher B1-B4/B6 and exception handling), and `33800c3` (smoke re-check record). Read
AGENTS.md, the design spec and engineering principles, decisions 0033/0036/0038/0039, the original launcher
review and `docs/verification/2026-10-31-launcher-smoke.md`. Locations below refer to `33800c3`.

**Verdict: changes requested. Five findings: one P1, two P2 and two P3. Production code is unchanged.
Seven regression cases remain enabled and failing.** The loopback regressions confirm the ordinary B5
cleanup and B6 request reduction, but expose incomplete cleanup on abandoned reauthentication, stale
version metadata after automatic sign-out, and a transport-error redaction bypass. The exception-policy
findings are source review, with no GUI or fatal-error fault injection.

## R1 - P1: Redact transport diagnostics and their inner exceptions too

Locations: `src/Pve.Api/PveClient.cs:384,390-392,477-483` (existing transport paths),
`src/PveLauncher/MainWindow.xaml.cs:89-96,102-108`, and `src/PveLauncher/App.xaml.cs:25-36` (`d608363`).

Decision 0039's redaction runs only in `ErrorText`, after HttpClient has successfully parsed the response
headers. A server or proxy can echo a submitted secret in a malformed HTTP header instead. HttpClient
then raises a transport exception containing the invalid line; `SendAsync` wraps it with `Describe(e)`,
which copies `e.Message` without applying the request's `secrets`. It also preserves the original inner
exception. The normal API-error handler shows and logs the outer message. The new `App.Describe` logs
the entire raw exception chain for unexpected, unobserved-task and fatal errors, so a safe outer message
alone would not make those diagnostics safe.

The loopback regression returns HTTP 200 with the received Authorization value as a deliberately invalid
header line. Sign-in throws and correctly signs out, but the token secret survives in the exception's
outer Message as well as its full diagnostics. This bypass does not require a JSON error or an HTTP error status. Existing reflection tests
for valid reason phrases and JSON `errors` remain useful controls; they do not cover this earlier parser
failure. No real credentials were used.

Sanitize every server-derived transport message before exposing it. Preserve safe type/stack information
without retaining an unredacted inner exception in a public/loggable chain. Apply the same rule to
response-body transport errors, whose outer message is generic but whose inner exception is retained.
The assertion in `App.Describe` that API exception messages contain no secrets is currently false.

Failing test: `LauncherSmokeFixesTests.MalformedHttpReflectingATokenDoesNotExposeItInDiagnostics`.
The transport bypass predates this branch; `d608363` additionally introduces raw full-chain log sinks.

## R2 - P2: Make error reporting survive a failed log append

Locations: `src/PveLauncher/MainWindow.xaml.cs:102-116,178-180`, `src/PveLauncher/App.xaml.cs:14-31`,
and the existing sink `src/PveLauncher/LauncherLog.cs:19,24-26`.

`ShowUnexpected` writes to the AutoFlush StreamWriter before updating the status bar. `LauncherLog.Write`
does not contain write/flush failures. If an append fails after the log was opened, for example because
the disk fills or another handle locks the file region, the reporter throws before displaying anything.
For a RunAsync failure, that new exception reaches Guard, which calls the same reporter and failing sink
again. Guard's async-void exception then reaches the Application handler, which marks it handled and
calls that same sink once more. The final handler itself can throw. The unobserved-task and fatal
handlers also depend directly on the unprotected sink.

This defeats B1's promised visible error precisely when diagnostics cannot be written. The sink's lock
only serializes writes; it does not protect against this exception cascade. A failure while updating the
Notice controls also lacks a reporting/reentry boundary. Make the final reporting path best effort,
nonrecursive and independent of a successful file append, while retaining the fatal distinction in R3.
The logger's throwing behavior predates this branch; the new recovery layers all rely on it.

This is a source-verified failure path, not a reproduced disk-full or live WinUI reentry test. No GUI
was launched to provoke an exception from an Application.UnhandledException handler.

## R3 - P2: Exclude fatal failures from click recovery

Locations: `src/PveLauncher/MainWindow.xaml.cs:113-116,178-180` and
`src/PveLauncher/App.xaml.cs:14-28` (`d608363`).

Both new catch-all handlers recover from every catchable Exception. The global XAML handler marks every
exception handled whenever `_window` exists, and the unobserved-task handler marks every aggregate
observed. None classifies catastrophic errors, including a real allocation-failure OutOfMemoryException
or one nested in an aggregate. Such a failure can become an ordinary status message followed by continued
execution. The AppDomain handler only sees failures that actually escape, so adding a `fatal:` logger
there does not restore a termination path for exceptions already swallowed by these handlers.

The global XAML event also covers framework/layout failures, not just failed clicks. The existence of a
MainWindow does not establish that either the window or WinUI remains consistent. This exceeds decision
0038's failed-click rationale and leaves its stated fatal-error behavior unimplemented for these paths.
Keep the recoverable-click policy, but give fatal failures an explicit terminating path, including nested
task exceptions; the final diagnostic attempt must not prevent termination. Align the decision's wording
with that distinction. This review does not change the accepted policy for recoverable click bugs.

Microsoft documents both the risks of routinely setting Handled on this event and runtime errors that
terminate regardless of Handled; this finding does not claim that the code can suppress those runtime
fail-fast cases. Microsoft also treats allocation exhaustion as catastrophic and recommends termination.
References: [Application.UnhandledException remarks](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.application.unhandledexception?view=windows-app-sdk-1.8),
[OutOfMemoryException remarks](https://learn.microsoft.com/en-us/dotnet/api/system.outofmemoryexception?view=net-10.0).

This finding is based on catch/handler control flow. No OOM, native corruption, process termination or
unobserved-task finalization was injected in this review.

## R4 - P3: Clear the previous token when the replacement prompt is abandoned

Locations: `src/Pve.Api/PveSignIn.cs:34-43,59-70` (`65ce3ca`), with callers at
`src/PveLauncher/MainWindow.xaml.cs:170-174` and token rejection at `src/Pve.Api/PveClient.cs:397-401`.

The new cleanup catch starts after credential-store lookup and the first secret prompt. A prompt that
returns null exits without SignOut; a prompt exception or cancellation also bypasses cleanup. This is
observable in the real launcher flow: sign in with an unremembered token, receive 401 on the subsequent
guest-list request, then cancel the reauthentication prompt. RunAsync retries on the same client, and a
token's 401 does not clear `_apiToken`. PveSignIn returns false while IsSignedIn is still true, SignedInAs
still names the old account, and ServerVersion still contains the old sign-in's answer. The launcher can
say "Not signed in" while enabling Sign out and treating the refused token as signed in on the next action.

The regression first completes a real loopback token check, makes ListVmsAsync return 401, then abandons
the replacement prompt. All three cases fail: null answer, thrown prompt exception, and token cancellation.
The problem is not limited to speculative calls on an already-signed-in client; RunAsync takes this path.
Store-target/read failures are outside the cleanup boundary for the same reason, though those failures
were not separately injected.

Cover the whole authentication attempt, including acquiring its credential, with the signed-out-on-abort
contract. Keep the intentionally separate optional-save boundary: an accepted sign-in remains accepted
when remembering its secret fails. The new save-failure control verifies that behavior.

Failing tests: `LauncherSmokeFixesTests.AbandoningReauthenticationClearsTheRefusedTokenAndVersion`
(`cancel`, `throw`, `cancel-token`). The incomplete cleanup fix is in `65ce3ca`; retrying a refused token
without clearing it predates that fix.

## R5 - P3: Invalidate ServerVersion on automatic session loss

Locations: `src/Pve.Api/PveClient.cs:60-63,223-228,242-246,397-401` (`65ce3ca` adds the cache).

Explicit SignOut, UseApiToken, LoginAsync and Dispose clear the new cache. The three existing paths that
drop an authenticated password ticket do not: local expiry, a 401 on renewal, and a 401 on a regular API
request. Each leaves IsSignedIn false while ServerVersion remains `9.2.1` in the regression. The cache
therefore outlives the sign-in it is documented to belong to.

Invalidate the version whenever that sign-in is invalidated. A transient renewal failure that retains
the still-valid ticket should retain its version too. Ordinary server switching does not mix versions:
the launcher stores a distinct client per server ID, and editing or explicitly signing out drops that
client. Successful reauthentication calls LoginAsync/UseApiToken and clears the stale value before loading
the heading, so this finding does not establish a wrong-version heading after a successful server switch.
It is stale public metadata while signed out, with the broader abandoned-token case covered by R4.

Failing tests: `LauncherSmokeFixesTests.LosingThePasswordSessionInvalidatesItsVersion`
(`expired`, `renewal-refused`, `request-refused`).

## Other checks

- **B5 failure classification:** 401 raises PveAuthException and retries within the bounded secret/factor
  budgets. HTTP 403/500/503, refused connections, a refused CA, malformed JSON/data/tickets, and caller
  cancellation propagate without being mislabeled as refused credentials. Failures inside the attempt
  clear authentication and version state. TFA cancellation, prompt exceptions, malformed second-step
  replies and unsupported factors also discard the half ticket; the tests verify that completing a
  factor afterwards fails locally. Three refused factors restart the password prompt. Saved credentials
  survive transient failure without prompting. Save failure after acceptance intentionally retains the
  authenticated session. These controls pass.
- **B6 normal lifetime:** token sign-in fetches version once, list/refresh requests do not fetch it again,
  and password sign-in starts with no version until its first fetch. Explicit sign-out, a new token or
  password sign-in, and disposal clear the cache. Separate loopback servers keep independent versions.
  The UI uses the cached value before fetching; reselecting a signed-in server reuses its client. A server
  upgrade during a sign-in intentionally remains cached until a new sign-in or explicit GetVersionAsync.
- **Guard and concurrency:** async event entry points forward into Guard; the async dispatcher callback
  in OnUi forwards its exceptions through the completion source. RunAsync rejects overlapping API actions,
  disables server changes and restores its busy state in finally for failures inside its try. Guard does
  not itself serialize dialogs or terminal-exit notifications. Dialog modality and routed input were not
  exercised, and the log/reporter failure cascade in R2 remains. No independent GUI-reentrancy guarantee
  follows from these window-free tests.
- **B1/AOT:** ThemeBrush uses TryGetValue and a type test, so an object projected only as DependencyObject
  takes the fallback rather than throwing an invalid cast. Constructing SolidColorBrush also provides a
  static reference to its projection. A missing/trimmed brush can use the critical-color/opacity fallback;
  its one-time diagnostic still has R2's log-I/O risk. The context-menu handler is on the row template and
  reads the declared UIElement's GetValue with FrameworkElement.DataContextProperty; its GuestItem type
  test is for the application's own managed class, created explicitly when filling the list. No reflection,
  dynamic lookup or cast to an unknown projected framework class was introduced. Root is a named XAML
  element. No new AOT blocker was found by inspection. No AOT publish or GUI execution was performed.
- **B2-B4:** the theme preference, widened Type column, and separate nonwrapping heading runs match the
  recorded fixes. Their visual appearance, narrow-window behavior and keyboard/pointer menu routing were
  not independently validated here. The screenshots and scratch-build results in `33800c3` are the
  implementer's evidence, not new measurements from this review.
- **Energy:** neither production commit introduces timers, delays or polling. Renewal remains lazy;
  CancelAfter exists only during a request; the existing HTTP connection pool has its documented 60-second
  idle cleanup, and terminal-exit waiting uses WaitForExitAsync on the process handle. The new handlers and
  resource fallback are event-driven. No idle CPU/wake-up measurement was run, and this review does not
  independently confirm the WinUI/DWM attribution in `33800c3`.

## Validation and scope

Added `tests/Pve.Api.Tests/LauncherSmokeFixesTests.cs`. Tests use synthetic credentials and loopback-only
listeners with temporary test CAs. Credential-save controls use random `SpiceViewer.Test-*` prefixes and
delete only those entries. The first sandboxed run could not import the existing helper's TLS private key;
it is an infrastructure failure, not a product result. The rerun outside the sandbox exercises that same
helper without modifying it.

| Check | Result |
|---|---|
| `dotnet test tests/Pve.Api.Tests --no-restore --nologo -v minimal` (outside sandbox) | **125 passed, 7 failed**, 132 total, 0 skipped; build emitted no warnings/errors |
| New smoke-fix cases within that run | **34 passed, 7 failed**, 41 total; only R1/R4/R5 fail |
| Existing Pve.Api.Tests cases within that run | **91 passed**, 0 failed |
| `git diff --check` | Clean |

The full project includes its existing fake viewer/bridge hand-off tests, which launch only test helper
processes with CreateNoWindow. Neither a real viewer/bridge nor the launcher was started. No other test
project was executed; compiling Pve.Term as an API dependency does not run Pve.Term.Tests.

No production edits, real Proxmox traffic, private token-file access, GUI/launcher execution, viewer-test
execution, golden changes, or runtime AOT/ARM64 claims. The seven failing cases stay enabled for the
implementer to fix.

## Response (Claude, 2026-10-31)

All five findings are fixed in production code. Codex's tests are unchanged, and the seven failing cases now pass.

- **R1 fixed.**
  - `PveClient.Describe` now takes the request's list of secrets. It runs the transport message through `Redact`, the
    same as the error answers, and caps it at 600 characters.
  - The raw `HttpRequestException` is no longer kept as the inner exception, so `ToString()` and a log of the whole
    chain carry only the redacted text.
  - A broken response body keeps only the inner exception's type name in its message, with no inner exception.
  - `App.Describe` documents the corrected guarantee, and decision 0039's "Server error text is untrusted" covers
    transport errors.
- **R2 fixed.**
  - `LauncherLog.Write` never throws. An I/O, access, disposed or not-supported error drops that line, and the next
    write tries again.
  - `ShowUnexpected` updates the status bar first and writes the log line after. It contains any failure of the status
    bar update and logs it.
  - A `_reporting` flag stops it from re-entering itself: an error raised while it runs is only logged. So a failing
    sink or status bar can't cycle through Guard and the application handler.
- **R3 fixed.**
  - `App.IsFatal` treats `OutOfMemoryException`, `AccessViolationException` and `SEHException` as fatal, searched
    through inner exceptions and every member of an `AggregateException`.
  - `Guard` and `RunAsync` catch only non-fatal exceptions. The application handler, the unobserved-task handler and
    `ShowUnexpected` pass a fatal one to `App.Fatal`, which tries one log line, ignores anything that line throws, and
    calls `Environment.FailFast`.
  - Ordinary bugs keep the recoverable-click policy. Decision 0038 now states the distinction.
- **R4 fixed.** `PveSignIn.SignInAsync` signs the client out before anything else. So reading the saved secret, a
  prompt that returns null, throws or is cancelled, and every later failure leave no refused token, account or version
  behind. Remembering the secret after the server accepted it stays outside, as `SavingFailureDoesNotUndoAnAcceptedPasswordSignIn`
  checks. Decision 0039 is amended.
- **R5 fixed.** The three places that drop a password ticket (expiry, a renewal answered with 401, a 401 on a call) go
  through one `DropTicket`, which clears the ticket, `SignedInAs` and `ServerVersion` together. It does this only if
  the ticket is still the current one. A transient renewal failure keeps the ticket and its version.

**Validation**
- `dotnet build SpiceViewer.sln`: no warnings.
- `dotnet publish src/PveLauncher -c Release -r win-x64` (Native AOT): no warnings.
- The AOT launcher was re-run through the Add dialog and the sign-in prompt against a refused loopback port. The
  status bar showed "Could not reach 127.0.0.1:8006: … (127.0.0.1:8006)", Sign out stayed disabled, and the window
  closed cleanly.
- The fatal path was not fault-injected (no OOM or `FailFast` was provoked).

**Test results**
- `dotnet test SpiceViewer.sln`: 840 passed, 2 skipped, 0 failed. That is Pve.Api 132 (all 41 smoke-fix cases), Pve.Term 115, Replay 391, Viewer 101, Video 35 (+2 skipped), Wire 27, Native 27 and Diagnostics 12.
- `LauncherSmokeFixesTests.ATransientFailureDoesNotRetryOrDeleteTheSavedSecret(token: True)` failed in 2 of 6
  whole-project runs of Pve.Api.Tests and passed in 5 of 5 runs of that class alone. In each failure "No exception was
  thrown", so `SignInAsync` returned false. That means `store.Read` found nothing, and the prompt was asked and
  returned null. My changes don't touch that path: the store is read before any request, and the only new step before
  it is `SignOut()`. The failure shows only while other classes write and delete `SpiceViewer.Test-*` credentials in
  parallel. That points at Credential Manager under concurrent writes rather than at the code under test. It is left
  for Codex to diagnose; the test is unchanged.
