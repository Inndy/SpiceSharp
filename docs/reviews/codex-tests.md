# Task 7: deterministic tests and newly exposed bugs

Added tests only; no production/native changes, viewer execution, server
connection, credential access or commit. The viewer helpers are compiled into a
dedicated Windows test assembly from their current source files to reach internal
types without changing production visibility. No window or system clipboard is
opened; HGLOBAL tests use private allocations which are freed in finally blocks.

## 1. med — Fit viewport can exceed a lower feature-level texture limit

**Location:** `src/SpiceViewer/Viewport.cs:31` through `:38`;
actual caller `src/SpiceViewer/Presenter.cs:290`.

`Compute(1920,1080,8192,4096,false,4096)` returns an 8192-wide buffer despite the
supplied 4096 texture limit. Fit retains one full guest dimension and uses
Math.Max(maxDim, guestDimension) when bounding the other, so it does not enforce
the supplied maximum. Presenter selects this lower cap for feature level 9_3;
an otherwise valid large guest can therefore lead to a failed swap-chain/buffer
creation rather than a supported viewport. This is a pure layout test, not a
live feature-level-9_3 device test.

**Suggested fix:** enforce both buffer limits and define an explicit fallback
when guest dimensions cannot fit unscaled: scale/tile the content, choose an
adequate rendering device, or report the unsupported configuration cleanly.
Do not silently return dimensions exceeding the hardware limit.

**Failing test:** `ViewportTests.LowerFeatureLevelLimitMustAlsoBoundGuestDimensions`.

## 2. med — Clamping the 1:1 buffer reintroduces stretching

**Location:** `src/SpiceViewer/Viewport.cs:24` through `:25`, `:52` through `:54`.

For a 20000x1000 client and 800x600 guest in 1:1 mode, the buffer width is clamped
to 16384. GuestRectInClient then applies client/buffer scaling, mapping the
800-pixel guest width to 977 client pixels. DXGI's full-client stretch follows
that mapping, so 1:1 is lost, and centering is computed in the clamped buffer
rather than actual client pixels. This also applies with smaller texture limits
on an oversized client. The test asserts the intended pixel dimensions; it does
not create such a window or claim an observed GPU result.

**Suggested fix:** preserve a unit-scale guest destination in client coordinates
independently of texture clamping. Use a supported presentation fallback for
clients larger than the texture limit, or explicitly reject that state instead
of advertising 1:1 while stretching it. Share the final transform with input.

**Failing test:** `ViewportTests.OneToOneMustNotStretchWhenClientExceedsTextureLimit`.

## 3. med — Queue-overflow agent shutdown leaves public state connected

**Location:** `src/Spice.Session/SessionAgent.cs:119` through `:125`, `:51` through `:52`.

The 40 MiB queue-limit path sets _agentRunning=false and resets buffers, but it
does not clear AgentConnected or guest capabilities and does not emit
AgentConnectedChanged(false). Input from the agent is now ignored while
AgentClipboardReady and AgentSupportsMonitorsConfig can still report true.
The viewer can continue showing/using resize and clipboard functionality that
has silently stopped. The test seeds queue accounting just below the threshold
and queues a real ClipboardGrab, avoiding an unnecessary 40 MiB allocation.
Its companion test confirms the internal running state and queue are cleared.

**Suggested fix:** use a common agent-stop transition that resets both internal
and public state and emits one disconnected notification. Arrange event delivery
outside the critical section to avoid calling application code under _agentLock.

**Failing test:** `AgentQueueTests.QueueLimitMustAlsoNotifyPublicAgentDisconnectedState`.

## 4. low — Decoded-size guard itself overflows for extreme positive dimensions

**Location:** `src/Spice.Canvas/Surface.cs:81` through `:86`.

ByteCount multiplies `(long)width * height * 4` without checked arithmetic.
`ByteCount(int.MaxValue,int.MaxValue)` wraps to -17179869180 and returns without
CanvasException, bypassing its documented maximum-byte validation. The test only
calls the size guard, never allocates the claimed image. Current wire dimension
limits prevent this exact input on the reviewed message paths; this is a defect
in the public allocation guard, not a demonstrated remote exploit. A caller
trusting it can later rent a tiny buffer for impossible dimensions or obtain an
unrelated range exception.

**Suggested fix:** use checked multiplication and convert overflow to
CanvasException, or compare pixel count with maxBytes/4 before multiplying by 4.
Reject invalid/unsupported side lengths at the appropriate boundary as well.

**Failing test:** `CanvasDeterministicTests.DecodedImageBudgetRejectsBeforeAllocation`
with both arguments int.MaxValue.

## Validation results

| Project | New cases | Total passing | Total failing |
|---|---:|---:|---:|
| Spice.Viewer.Tests | 40 | 38 | 2 |
| Spice.Wire.Tests | 4 | 7 | 0 |
| Spice.Replay.Tests | 16 | 25 | 2 |
| Total | 60 | 70 | 4 |

The 60 added cases have 56 passes and four expected current-bug failures.
Fourteen pre-existing core/replay cases also pass. Nothing is skipped.
Some cases contain many seeded comparisons: 300 clip-union scenarios and 1000
line scenarios are counted as five xUnit theory cases, not 1300 test cases.

Commands: `dotnet test tests/Spice.Viewer.Tests`,
`dotnet test tests/Spice.Wire.Tests`, and
`dotnet test tests/Spice.Replay.Tests`.

Transport tests use a gated in-memory asynchronous writer, not a socket. They
verify full and mini framing, per-producer order and full-header serial order
under six concurrent producers, prompt Send completion while writes are blocked,
and the current eight-MiB pending-buffer drop threshold. They make no timing or
network throughput claim. The drop-limit test treats the drain buffer already
being written separately, matching the current double-buffered implementation.

Agent tests cover first-message preservation, replacement of parked clipboard
data, token-driven draining, the exact 32 MiB clipboard boundary and one byte
above it, plus queue-limit cleanup/public state. Canvas memory tests reject
oversized images/surfaces before allocation; no exhaustion stress test was run.
