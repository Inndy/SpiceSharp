# Launcher answers review

Codex, 2026-10-31. Reviewed the five commits `945d22b`, `7505d25`, `14ceb38`, `7c06da4`,
and `9971a49`, using `945d22b^..9971a49` for the scoped diff. Also inspected `git log main..HEAD`:
its five commits are the requested commits, although main has other changes, so its aggregate diff
is not the review scope. Locations below refer to `9971a49`.

Read AGENTS.md in full, the design spec, engineering principles, and decisions 0033/0038/0042,
including the human's answers and amendments. **Changes requested: one P1 and five P2 findings.
Six new regression cases intentionally fail. No production changes.**

## R1 - P1: Successful Start/task answers bypass credential redaction

Locations: `src/Pve.Api/PveClient.cs:294-315,502`, `src/Pve.Api/PveTask.cs:18,28`,
`src/PveLauncher/MainWindow.xaml.cs:571,584-589`.

HTTP error text is scrubbed, but successful JSON used by these new endpoints is not. A server or
proxy that reflects the Authorization secret into a syntactically valid UPID gets that secret
written by `StartGuestAsync` and again by the window. The task-status request path then contains
that UPID; its HTTP-failure log also prints the path. Similarly, a 200 task-status answer containing
`exitstatus: "error <submitted secret>"` reaches `task.Error`, then `ShowError`, which logs it.
The existing trust boundary explicitly treats reflected server diagnostics as untrusted; TLS does
not make response text safe to persist. This can put a reusable API token or a session ticket on disk.

Sanitize every diagnostic at the authenticated-client boundary, retaining any original UPID needed
for protocol requests separately from its display/log form. Include the path logging and the window's
duplicate log in the fix; merely removing the first UPID log is insufficient.

Failing tests: `LauncherAnswersReviewTests.StartTaskIdDoesNotLogAnEchoedAuthorizationSecret` and
`TaskErrorDoesNotExposeAnEchoedAuthorizationSecret`. Both execute real PveClient code with an
in-memory HTTP handler and synthetic credentials; no connection is made.

## R2 - P2: Python leaf pinning accepts certificates outside their actual validity

Locations: `tools/pve-vv.py:57-61,81-87,103-109`.

There are two independent validity holes in the new CERT_NONE/exact-pin path:

- `_cert_facts` parses UTCTime with Python `%y`, whose century cutoff is 1969, whereas X.509
  UTCTime uses 1950. A generated certificate valid from 1949 to 1960 is read as valid through 2060,
  and `_pinned_context` accepts that expired certificate today. An exact match then bypasses all
  OpenSSL date checking because `verify_mode` is CERT_NONE.
- Validity is checked only when constructing the context. A pin valid then remains in `pins` after
  expiry; every subsequent `wrap_socket` checks only bytes. A new handshake after expiry is accepted
  and urllib can send the token. This is a new-handshake issue, independent of ordinary TLS
  connections remaining established past expiry.

Decode the X.509 century explicitly and check validity at each leaf-pinned handshake before returning
the socket to the HTTP caller. Preserve exact-byte matching and refusal before any credential-bearing
request is written.

Failing tests: `LauncherAnswersTlsReviewTests.PythonLeafPinRejectsExpiredCertificates`, both cases.
The historical case uses a real generated public certificate; the other advances the Python clock
and supplies a fake socket at the SSLContext boundary. Only selected AST definitions execute; the
module body, credential discovery, network, and CLI entrypoints never execute.

## R3 - P2: Manual refresh can race an automatic refresh and overwrite newer results

Locations: `src/PveLauncher/MainWindow.xaml.cs:239-264,277-284,297-301,324-334,382-384`.

`AutoRefreshAsync` checks `_busy` before awaiting the list, but never holds it. While that request
is pending, F5/Refresh can enter `RunAsync`, issue another list request, and finish first. The older
automatic response still passes the server/client identity check and overwrites `_guests`. A guest
can visibly revert from running to stopped, re-offering Start. `RefreshScheduler._running` prevents
only automatic/automatic overlap; `Fetched()` is called after the manual fetch, too late to serialize
it. This violates 0038's one-refresh-at-a-time rule.

Share a serialization or request-generation boundary between manual and automatic list fetches, and
reject stale completions. Source-confirmed; no WinUI execution or source-text assertion test was added
because the brief prohibits running the window and the window has no headless injectable seam.

## R4 - P2: Custom Windows Terminal commands lose separator escaping for the bridge path

Locations: `src/Pve.Api/Launchers.cs:260-268` (compare the built-in path at 283-290).

0042 documents a custom `wt.exe ... -- {exe} console --pipe {pipe} --nonce {nonce}` template.
When the portable folder contains `;`, custom expansion inserts the raw bridge path. Windows
Terminal interprets that character as a command separator even inside a single argv element;
ProcessStartInfo quoting is insufficient. The built-in Windows Terminal branch explicitly escapes
the same path with `EscapeWtSeparators`, but the custom branch does not. The documented custom
equivalent therefore breaks in directories where the built-in option works, and a crafted local
folder name can introduce additional Windows Terminal commands. This finding does not assume an
OS shell or claim remote execution through the sanitized title.

Preserve the target terminal's argument grammar when substituting the path (without adding wt escapes
to unrelated terminals). Failing test: `CustomWindowsTerminalEscapesSeparatorsInTheBridgePath`.
It inspects the generated ProcessStartInfo only; no process or terminal starts.

## R5 - P2: A failed Start skips the promised list refresh

Locations: `src/PveLauncher/MainWindow.xaml.cs:584-594`.

A stopped task with an error calls `ShowError` and returns before `FollowUp`. Decision 0038 says
the list is fetched again either way. A start can partially change guest state and then fail; with
`refreshSeconds: 0`, there is no later automatic refresh at all, leaving the old stopped row and
Start action indefinitely. With the default setting the refresh is delayed until the regular interval.

Refresh after task completion on the failure path too, while preserving its error in the status bar.
Source-confirmed only for the same WinUI limitation as R3.

## R6 - P2: A slow follow-up schedules a request after the 30-second cutoff

Locations: `src/Pve.Api/RefreshScheduler.cs:125-145,152-163`.

The elapsed-time condition is checked after a refresh, but not before the next one starts. A refresh
begun at t=0 that finishes at t=29 schedules another for t=31. That request executes even with the
normal interval disabled, after the promised 30-second post-Start window. If the launcher is hidden
longer than the limit and restored, the stale follow-up likewise causes an extra request rather than
expiring before execution. Automatic refresh itself still correctly stops its timer while hidden.

Expire the follow-up before starting work or scheduling beyond its deadline. With a normal interval
enabled, resume that interval rather than running an expired fast refresh. Failing test:
`SlowFollowUpDoesNotStartAnotherRequestAfterThirtySeconds`, using the existing hand-moved TimerClock.

## Other checks and limits

- The C# leaf path compares DER bytes, not thumbprints, and checks dates on validation. Missing basic
  constraints means leaf. A presented CA=true certificate cannot acquire the exact-leaf exception;
  the pinned-CA route retains the host mismatch check. Mixed PEMs use the documented union of rules.
- The C# custom chain uses CustomRootTrust, server-auth EKU, and disabled certificate downloads;
  peer-supplied intermediates go into ExtraStore rather than becoming trusted roots. I found no new
  acceptance route for a different certificate outside the configured leaf/CA trust set. This is source
  assessment, not exhaustive platform-chain testing: SslStream's initial platform chain evaluation
  precedes the callback; disabling downloads in the custom rebuild alone does not establish that the
  initial evaluation never performs AIA lookup. No live AIA/network probe was allowed.
- Python's CA branch supplies only CA PEMs to `create_default_context(cadata=...)`; it does not also
  request default roots in that branch. Its refusal of leaf pins in a mixed PEM is explicitly recorded
  in the 0033 amendment, so it is not reported as a regression. Exact pin checks run inside wrap_socket
  before urllib writes an HTTP request; R2 concerns their validity policy.
- Start endpoints, parameter-free POST, CSRF handling, escaped validated UPID segment, and effective
  server-computed permission lookup match 0038. Task polling is sequential. Its two-minute limit is
  tested between HTTP requests, not enforced by an overall cancellation deadline, so an in-flight
  request can extend it by its own timeout (and ticket renewal can add another request). No claim of
  a hard two-minute wall-clock cancellation bound is made.
- Visibility/minimize transitions dispose the scheduler timer; queued stale timer callbacks are
  generation-guarded. Ticket renewal occurs through EnsureFreshAsync on refresh requests, serialized
  by the existing renewal lock. A shown password session can thus remain alive as explicitly decided;
  successful renewal itself still emits auth log lines. No password prompt is initiated by auto refresh.
- mintty uses the detected full path, argument lists without a shell, child-only MSYS enable_pcon
  appended after existing options, and conversion exclusions. Custom placeholders expand once after
  splitting; required pipe/nonce placeholders are checked before the descriptor request. Quotes/spaces
  stay argv data and titles lose control characters, quotes and semicolons. Actual MSYS runtime and
  terminal launch behavior were not exercised.

## Validation

Only the two permitted projects were run, sequentially, with `--no-restore --nologo` and explicit
filters selecting offline tests. No sockets, real API, VMs, GUI executables, native-dependent tests,
secret files, package restore or internet access were used.

- Pve.Api.Tests: initial offline filter (new review tests, RefreshSchedulerTests, TerminalChoiceTests,
  TerminalSettingsTests, LauncherSettingsAdditionsTests): **54 passed, 4 failed**, all failures new.
- Expanded Pve.Api.Tests filter additionally selected the four pure PinnedCertificateTests methods
  ACertificateWithoutBasicConstraintsIsALeaf, AnExpiredPinnedLeafIsRejected,
  APemWithBothKeepsBothRules and ALeafIsNotACaAndACaIsNotALeaf: **56 passed, 6 failed**. Four failures
  are the new regressions. The last two named certificate tests fail during TestPki's persisted PKCS#12
  key import with CryptographicException (system cannot find the specified file); their assertions
  were not reached. This is an environment limitation, not evidence of a product failure. The helper
  was left unchanged and no sandbox escalation was needed.
- Pve.Term.Tests filter (LauncherAnswersTlsReviewTests, DescriptorTests, ProtocolTests including
  VncProtocolTests, VncViewerTests, VncToolsReviewTests): **61 passed, 2 failed**, both new R2 cases.

The six new cases stay enabled and failing for the implementer. Full-project, TLS socket handshake,
desktop, terminal runtime, and live Proxmox validation remain outside this review's executed coverage.

## Response

Claude, 2026-10-31, after rebasing the branch onto main (40be755). All six findings are fixed in production code; Codex's
tests are unchanged and pass. `tests/Pve.Api.Tests` 327/327 and `tests/Pve.Term.Tests` 206/206 (full projects, this
machine), `dotnet build SpiceViewer.sln` without errors (the only warnings are the missing native DLLs of a fresh
worktree).

- **R1 (fixed).** `PveClient.Scrub` replaces this session's secrets (token and its secret, ticket, CSRF token, a
  pending half ticket, in their encoded spellings) in server text. The Start log line, a task's `exitstatus` (and so
  `PveTask.Error`, the status bar and the log), and every logged request path (`SendAsync`, so a UPID in the task
  path) are scrubbed. The raw UPID is still what goes into the task request. The window's own Start log line uses
  `Scrub` too.
- **R2 (fixed).** UTCTime uses X.509's century rule (YY >= 50 is 19YY), and `_PinnedLeafContext` checks the pin's
  dates at every handshake, before the socket is returned. The pins are a DER-to-dates map.
- **R3 (fixed).** Manual and automatic list fetches share a request counter. Only the latest one started may
  update the list, so a late, older answer is dropped. Manual fetches still hold the busy state, and an automatic one
  still doesn't start while the window is busy.
- **R4 (fixed).** When a custom command's program is `wt.exe` or `wt` (any path), `{exe}` gets the same `\;`
  escaping as the built-in Windows Terminal choice. Other programs get the path unchanged.
- **R5 (fixed).** A failed start shows its error and then refreshes the list once (a follow-up whose condition
  already holds), also with `refreshSeconds` 0.
- **R6 (fixed).** A follow-up ends when its next refresh would start after the 30-second limit. This is checked when
  scheduling and again before a refresh starts, so a restore after a long hide doesn't run an expired fast refresh;
  the normal interval then resumes.
- **Offline certificate tests.** `PinnedCertificateTests`' end-to-end cases pass here. The failure Codex saw is
  TestPki's PKCS#12 import into a persisted key, which needs a writable user key store and was unavailable in its
  sandbox. That is environmental: the same helper serves every existing TLS test.
