# Proxmox VNC bridge review

Codex, 2026-10-31. Reviewed all five commits in `main..HEAD`: `7353eb8`, `7113065`,
`cd8115a`, `3fc2baf`, and `72723ea`. Production references below are at `72723ea`.
Read AGENTS.md first, the design spec and principles, decisions 0033/0034/0036/0038/0043,
the changed production paths, existing VNC tests, and the committed verification record.

**Verdict: changes requested. Three P2 findings; five intentionally failing regression cases.**
Production code is unchanged. The peer-match regression establishes a wrong-connection lookup,
not a demonstrated cross-user or lower-integrity takeover. The local None-security endpoint's
identity assurance remains conditional on resolving that lookup and the lifetime concerns below.

## R1 - P2: Match the complete TCP connection before trusting its process

Locations: `src/Pve.Term/VncLocal.cs:81-85,105-118,124-147`.

`FindOwner` compares local address and both ports, but ignores the row's remote address and state.
Consequently a connection from `127.0.0.1:clientPort` to `127.0.0.2:serverPort` supplies a PID for
the lookup intended to authorize a connection to the bridge at `127.0.0.1:serverPort`. The new
loopback test creates exactly that unrelated connection, with no connection to the bridge's
destination address. `FindOwner` nevertheless returns the test process's PID, rather than zero.
This is a confirmed mismatch in the only authentication gate protecting security type None.

For the actual listener, a competing row would also need matching ports; simultaneous compatible
bindings or connection turnover and table ordering determine whether the wrong row wins. The test
does not demonstrate those conditions with a second user or a restricted token, and does not claim
a completed unauthorized session. The implementation must not rely on that ambiguity for admission.
Match both addresses, both ports, and an appropriate connected state against the accepted socket,
and reject ambiguous or vanished matches.

There is also an unresolved lifetime gap: the table yields a numeric PID, then `OpenProcess` opens
whatever process has that PID at that later instant. The process handle pins the process only after
it is opened. There is no connection/process creation correlation or second connection validation;
an exited process, a stale row, PID reuse, or a transferred socket is not tied back to the accepted
endpoint. Simply repeating the same incomplete lookup would not establish process identity.
Strengthen and document the lifetime binding, retaining the inspected process handle through admission
and failing closed when ownership cannot be established. A forced PID-reuse/token-boundary exploit
was not run, so this is an assurance gap alongside the reproducible tuple defect, not a separate
proven privilege-escalation finding.

Failing test: `VncBridgeReviewTests.PeerLookupMustMatchTheRemoteLoopbackAddressToo`.
Existing same-process lookup and inaccessible/other-user process rejection controls pass.

## R2 - P2: Keep a deadline through the complete local and upstream initialization

Locations: `src/Pve.Term/VncBridge.cs:69-91,100,106,119-122`;
`src/Pve.Term/VncUpstream.cs:187-217`.

The `--wait` cancellation source is disposed immediately after accept. All subsequent local reads
and `up.InitAsync` receive `life.Token`, which has no deadline in the CLI. An admitted connection
can read the banner and never send its version, or select None and never send ClientInit, keeping
the process and authenticated upstream alive indefinitely. The listener has already closed, so the
intended viewer cannot take over. An upstream that finishes security but withholds ServerInit has
the same effect: the connector's 20-second budget has already been disposed when it returns.
This contradicts 0043's explicit requirement that the viewer finish its handshake within `--wait`.

The regression uses an already-secured synthetic upstream and a one-second viewer timeout. At
each of the version, ClientInit, and ServerInit stalls, the bridge remains unfinished after an
additional three-second observation window. Cleanup explicitly cancels the session and closes
both socket pairs; the failing tests leave no indefinitely blocked workers.

Use a startup deadline across accept, version/security exchange, ClientInit, ServerInit, and its
forwarding. Distinguish startup expiry from caller cancellation, and remove that deadline once the
relay starts so it cannot kill an established session. Bound failure reporting writes as well.

Failing test: `VncBridgeReviewTests.ViewerDeadlineMustCoverTheWholeHandshake` (three stages).

## R3 - P2: Bound the API response before HttpClient buffers it

Locations: `src/Pve.Term/VncUpstream.cs:233-247,254-294`.

`HttpClient.SendAsync(req, ct)` uses response-content completion and buffers the body before returning.
`ReadAsByteArrayAsync` then obtains the whole body before any application limit is checked. The limits
on ticket/password fields and RFB names/reasons do not bound this HTTP response. A faulty or hostile
authenticated API endpoint can force very large buffering and JSON scanning, including ignored
properties. Even an error response is buffered before the status code is inspected. The connection
timeout bounds elapsed time, not memory consumed by a fast responder.

The loopback regression sends a valid session object with over 2 MiB of irrelevant padding. The reader
accepts it as a session. The fixture uses direct HTTP solely to exercise this body-reading method
without certificate persistence; normal production descriptors require HTTPS. This is not a claim
that the bridge accepts plaintext production descriptors or bypasses TLS. No large-memory exhaustion
was attempted.

Set a small explicit session-response budget before content buffering, or use response-headers
completion and a bounded reader. Enforce the budget for chunked or dishonest Content-Length responses
too, and translate over-limit failures into a normal connection failure. The precise production cap
is an implementation choice; this regression requires that a multi-megabyte session answer be refused.

Failing test: `VncBridgeReviewTests.VncproxyResponseMustHaveABoundedBodyBeforeJsonParsing`.

## Other reviewed behavior and limits

- **Listener and admission:** the socket is explicitly IPv4 `IPAddress.Loopback`, port zero, with
  `ExclusiveAddressUse=true`; there is no IPv6 or dual-stack listener. The socket is accepted before
  inspection, so client bytes can queue, but no RFB banner, password, framebuffer, or other bytes are
  sent to that socket before `_checkPeer` succeeds. Rejected connections are reset and do not consume
  the session; the listener stops after the first accepted identity. The existing rejection-before-banner
  and single-session tests pass. SID equality and integrity RID comparison are fail-closed when process
  or token access fails; a lower RID is rejected. This run did not create other-user, low-IL, AppContainer,
  impersonating, or PID-reuse processes. SID plus IL is not a general proof that a process is unsandboxed:
  restricted tokens at the same IL are not separately classified. The documented boundary is user plus IL.
- **RFB security:** upstream prefers VNC authentication when offered, uses the first eight ASCII password
  bytes with reversed key bits and DES ECB over the 16-byte challenge, and checks SecurityResult.
  The older-server ticket fallback and independent challenge-response tests pass. None upstream is an
  explicit 0043 choice, not an accidental local-to-remote downgrade. Downstream None follows the peer
  check and is re-presented independently of upstream security. The local 3.3/3.7/3.8 success paths,
  3.3 and 3.8 refusal paths, and forced shared=1 are covered by passing tests; disabling ForceShared is
  an explicit library/test option, not exposed by the CLI. No ticket is sent to the viewer.
- **Handshake bounds:** version reads are exactly 12 bytes; security count is one byte; challenge is
  exactly 16 bytes; reasons and names are capped at 4096 bytes before their allocations. ServerInit
  dimensions are 1..16384, bpp is 8/16/32, and depth is nonzero and at most bpp. The unchanged ServerInit
  is forwarded after these checks, and buffered bytes following it are passed to the relay. These are
  the checks specified by 0043, not complete semantic validation of every pixel-format field: colour
  maxima, shifts, and flag values are passed to the real viewer. Post-init server data is deliberately
  opaque; the external viewer remains responsible for safely decoding it. Malformed handshake controls pass.
- **Client framing:** the new byte-at-a-time tests forbid a boundary at every interior byte, then exercise
  header splits and coalesced following key/pointer messages. Layouts checked against 0043's recorded
  QEMU dialect are listed below. Payload contents never become headers. Unknown type/subtype/operation
  sets Lost permanently and disables injection without stopping transparent forwarding. `int.MinValue`
  clipboard length is promoted to long before abs, so it cannot overflow into a boundary. Tracking uses
  fixed storage, even for huge declared payloads; the relay does not allocate those declared lengths.

  | Client message | Total bytes |
  |---|---|
  | SetPixelFormat (0) | 20 |
  | SetEncodings (2) | 4 + 4 * u16 count; empty and 65535 tested |
  | FramebufferUpdateRequest (3) | 10 |
  | KeyEvent (4), PointerEvent (5) | 8, 6 |
  | ClientCutText (6), including extended clipboard | 8 + abs(s32 length); zero, positive, negative, and extremes tested |
  | Xvp (250) | 4 |
  | SetDesktopSize (251) | 8 + 16 * byte-at-offset-6 screen count; zero and 255 tested |
  | QEMU extended key (255/0) | 12 |
  | QEMU audio (255/1) | operation 0/1: 4; operation 2: 10 |

  No local QEMU source checkout was available at the inspected reference location, and internet access
  was forbidden. These results validate the recorded dialect and parser behavior, not an independent
  fresh audit of every extension in the cited upstream commit or a future QEMU version.
- **Injection and relay:** the client pump and keep-alive share one send semaphore; tracker updates and
  complete WebSocket sends occur while holding it. An injection can precede an as-yet-unforwarded
  complete client message, but cannot split one already partially forwarded when the tracker is correct.
  The idle test proves that a split KeyEvent blocks insertion until its remaining bytes arrive. Both
  directions update activity, client reads are capped at 32 KiB, and each becomes a complete binary frame.
  Either pump ending closes the socket and aborts upstream. The allocation test passes its <=1024 B
  bookkeeping allowance over 2000 warmed real-loopback round trips; it is not a measurement of all
  startup, logging, or keep-alive allocations. No idle power measurement was performed.
- **Pending-data caveat:** `VncRelay.cs:150` forwards the initial Pending buffer with a single socket
  SendAsync and ignores its returned count; subsequent payloads correctly loop at lines 162-163.
  This deserves the same full-write handling if that send can complete short on the supported Windows
  socket path. A short initial send was not reproduced here, so this is recorded as a remaining
  transport-contract concern rather than a fourth confirmed regression or a byte-transparency guarantee.
- **Hand-off and secrets:** VNC descriptors reject serial/session fields and non-QEMU targets; absent
  console retains terminal compatibility. The CLI subcommands reject the other descriptor kind. The
  unchanged one-instance pipe uses owner DACL, no-read/no-write-up integrity label, remote-client rejection,
  nonce matching, and a whole-exchange deadline. The VNC launcher uses that hand-off and zeroes serialized
  descriptor bytes after delivery. The session request and WebSocket use API TLS policy 0033, independent
  of SPICE's .vv trust rule. Session/password data stays in memory; the WebSocket ticket is URL-escaped for
  its required request query, not placed on a process command line. Session ToString omits ticket/password.
  Source review found no new deliberate ticket write to disk, command line, or status log. This is not
  proof against arbitrary endpoint text reflecting secrets, crash dumps, or same-user memory inspection.
- **Viewer detection and commands:** detection checks files and uninstall registry values without starting
  candidates. Auto order and literal `host::port` construction match 0043. The registry/file locations and
  warning-suppression options were checked against the record and synthetic probes, not installed viewers.
  Both launcher-to-bridge and bridge-to-viewer commands use ArgumentList and UseShellExecute=false.
  Placeholders are expanded within individual arguments; a new control preserves a title containing
  quotes, shell punctuation, and a trailing backslash as one argument. Explicitly configuring a command
  interpreter delegates its grammar to the user; arbitrary shell templates are not made safe by argv quoting.
- **Python tools:** an AST-only control extracts the VNC option and command blocks from pve-vv.py, bypassing
  the credential-reading module body, config/API calls, and all pipe creation. It verifies VNC option
  passthrough, custom viewer argv/placeholders, --here flags, and use of a list without shell=True.
  A second part runs only vnc-probe definitions against an in-memory fake socket delivering three-byte
  chunks. It verifies shared ClientInit, SetEncodings, a full-update request, and a complete raw update,
  with no key/pointer/clipboard input or actual connection. The probe is a limited diagnostic parser:
  unsupported encodings stop it, and its recv/name/compressed/clipboard lengths lack defensive caps.
  It should not be treated as a hardened decoder or evidence that all viewer encodings work. Missing
  values for helper options can still produce Python argument errors; no real helper entrypoint was run.
- **Launcher routing:** config parsing follows the amended decision: qxl..qxl4 and virtio* -> SPICE;
  serial0..3 and none -> terminal; other/absent vga -> VNC; LXC -> terminal. Non-auth config failures
  preserve the previous SPICE fallback. Templates are refused, explicit VNC is QEMU-only, the VNC choice
  is persisted, and the bridge is hidden with stderr status reporting. All 18 VncLauncherTests pass.
  UI event wiring, actual viewer execution, and the five open human questions in 0043 remain unapproved
  by this review. The earlier live verification record was read as historical evidence, not repeated;
  its ClientInit row says the probe sent zero whereas the committed probe sends one.

## Validation

All new connections were loopback. No real credential file, Proxmox, VM, internet, GUI, VNC viewer,
PveLauncher, SpiceViewer, or Spice.Viewer.Tests was used. The existing immediate-failure test starts
only hidden `cmd.exe /c exit 3`; the tools control starts hidden Python with synthetic state and no
network calls. The sandbox blocked the existing synthetic certificate PKCS#12 import; the approved
outside-sandbox reruns of the focused API and VNC suites passed their TLS fixtures. No restore was run.

```powershell
dotnet test tests/Pve.Term.Tests/Pve.Term.Tests.csproj --no-restore --filter "FullyQualifiedName~Vnc" --logger "console;verbosity=minimal" -p:NuGetAudit=false
dotnet test tests/Pve.Api.Tests/Pve.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~VncLauncherTests" --logger "console;verbosity=minimal" -p:NuGetAudit=false
dotnet test tests/Pve.Term.Tests/Pve.Term.Tests.csproj --no-restore --filter "FullyQualifiedName~VncToolsReviewTests|FullyQualifiedName~VncFramingReviewTests.CustomViewerTitleStaysOneArgumentWithoutAShell" --logger "console;verbosity=minimal" -p:NuGetAudit=false
git diff --check
```

- Focused VNC suite before the last two controls: **68 passed, 5 intentionally failing**, 73 total.
- Last two added controls: **2 passed**, 0 failed. Across the final selected VNC cases: 70 passes and
  the same five intentional failures, 75 distinct cases. All new test code compiled without warnings.
- API launcher selection: **18 passed**, 0 failed after the certificate-fixture rerun.
- New tests comprise 19 framing/argv controls, one Python-tools control, and the five failing cases.
- No Native AOT publish, forced partial-send test, PID-reuse stress, restricted-token runtime test,
  real-viewer compatibility test, or full-solution suite was performed.
- `git diff --check`: clean. Only this review, its index row, and three new test files are changed.

## Response

Claude, 2026-10-31. All three findings are fixed in production code. None of Codex's tests were changed: each of the
five failed for the reason it describes, so none is wrong.

- **R1** (`VncLocal.cs`):
  - `FindOwner` now matches the whole connection: state ESTABLISHED, 127.0.0.1 at both ends, and both ports. A row
    with PID 0, or two matching rows with different owners, returns 0. The listener also refuses a client whose
    address is not 127.0.0.1 before any lookup.
  - Lifetime: `Check` opens the process from the first lookup, then reads the table again while holding that
    handle, and requires the same PID to still own the established connection. The open handle keeps the PID from
    being reused. If the first owner had exited before `OpenProcess`, its connection would no longer be an
    established row, so the second lookup fails. The token is then read through the same handle. Every step fails
    closed.
  - Not covered: a socket handed to another process (inherited or duplicated) is reported under its creating
    process. That process passed the same user and integrity check, and handing a socket on needs a handle into
    that process at that level. Decision 0043 records the rule.
- **R2** (`VncBridge.cs`):
  - One startup deadline (`--wait`, the `ViewerTimeout` option) covers the accept, the viewer's version and
    security choice, the wait for the upstream, the failure report to the viewer, ClientInit, ServerInit and its
    forwarding.
  - Expiry is told apart from the caller's cancellation (`ct`):
    - a viewer stall gives `NoViewer`;
    - an upstream that doesn't open, or withholds ServerInit, gives `ConnectFailed`;
    - each stage gets its own message.
  - The deadline is cancelled when the relay starts, so it can't end an established session. That also ends the
    viewer-exit watch, which used to outlive the wait. PveTerm's usage text now describes `--wait` as covering the
    handshake.
- **R3** (`TermproxyConnector.cs`, `VncUpstream.cs`):
  - Both session requests (vncproxy, and termproxy, which had the same pattern) use `ResponseHeadersRead`, and check
    the status before reading any body.
  - `ReadSessionBodyAsync` refuses a Content-Length over 64 KiB at once. It reads at most 64 KiB whatever the
    framing (chunked or a dishonest length), and one more byte means refusal.
  - Read failures and over-limit answers become `TermConnectException`. The buffer is zeroed after parsing.
  - A real vncproxy answer is a few KiB, most of it the node certificate's PEM.
- **Also from "Other reviewed behaviour":**
  - The initial Pending buffer is now sent with the same full-write loop as later payloads.
  - The verification note's ClientInit row is corrected: the probe sends shared=1.

Validation:

| Check | Result |
|---|---|
| `dotnet build SpiceViewer.sln --no-incremental` | 0 warnings, 0 errors |
| Pve.Term.Tests | 190 passed, including the five regressions of this review |
| Pve.Api.Tests | 237 passed |
| Spice.Replay.Tests | 431 passed |
| Spice.Video.Tests | 47 passed |
| Spice.Wire.Tests | 27 passed |
| Spice.Native.Tests | 27 passed |
| Spice.Diagnostics.Tests | 12 passed |

`AllocationTests.FramingDoesNotAllocate` (termproxy framing, untouched code) failed once in the first full
Pve.Term.Tests run and passed alone and in the full re-run. It wasn't investigated further. No live Proxmox run, GUI
or viewer was used for these fixes.
