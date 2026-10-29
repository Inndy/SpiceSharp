# Core library correctness and security review

Reviewed 2026-10-29. Production sources were read only. The new regression tests
deliberately express required behavior and currently fail; none is skipped.
No Proxmox credentials were read and no live server was contacted for this review.
Private session handlers/state are accessed with reflection in tests to exercise
failure paths without changing production visibility. That is a unit-level
reproduction, not a claim of an end-to-end network exploit.

## 1. high — TLS callback accepts certificates without server-authentication usage

**Location:** `src/Spice.Transport/TlsTrust.cs:49` through `:57`.

The custom chain is built without setting ApplicationPolicy. Its default is
empty, so building a chain does not establish that the certificate is authorized
for TLS server authentication. The callback also discards the incoming chain
error once its own chain builds. A peer holding a client-auth-only certificate
issued by the .vv CA, with the expected subject, passes this validator. It still
needs that certificate's private key and a matching subject; this is not a bypass
of the configured CA requirement. The test creates an in-memory CA and two
otherwise equivalent leaves: a server-auth positive control passes, but a leaf
restricted to client authentication also returns true when the callback receives
RemoteCertificateChainErrors. No live SslStream handshake was tested.

**Suggested fix:** add server-auth OID `1.3.6.1.5.5.7.3.1` to the custom chain's
ApplicationPolicy before Build, preserving CustomRootTrust. Check usage through
the entire chain. See the [ApplicationPolicy documentation](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509chainpolicy.applicationpolicy?view=net-10.0).

**Reproduction:** `CoreSecurityReviewTests.ClientAuthenticationOnlyCertificateMustNotAuthenticateServer`.

## 2. high — Bitmap payload dimensions bypass the descriptor limit

**Location:** `src/Spice.Wire/Display.cs:166`, `src/Spice.Canvas/ImageDecoder.cs:163`,
`src/Spice.Canvas/Surface.cs:59`.

Only descriptor dimensions are capped. BITMAP carries independent uint width,
height and stride; the parser checks stride*height but never validates these
dimensions against the descriptor or the output allocation. A 36-byte image
with descriptor 1x1, bitmap width `0x40000000`, height 1 and stride zero parses
successfully. RowBytes and decoded byte-count calculations wrap, so downstream
range/index failures escape Canvas.Apply's CanvasException/WireException filter
and terminate the display loop. Other accepted values can request large output
allocations. A height of `0x80000000` also parses and later casts negative.

**Suggested fix:** validate both dimension pairs and their required relationship;
use checked 64-bit arithmetic for row size, stride and decoded output bytes,
enforce a decoded-byte ceiling before allocating, and reject invalid inputs with
WireException. Keep checks in lower-level allocation helpers as defense in depth.

**Reproduction:** both cases of `CoreSecurityReviewTests.BitmapPayloadDimensionsMustBeValidated`.

## 3. high — Video scaling allocates before destination clipping or size validation

**Location:** `src/Spice.Canvas/Canvas.cs:447`, `src/Spice.Canvas/Raster.cs:239`,
`src/Spice.Session/SessionDisplay.cs:112`.

A stream's wire-controlled destination rectangle reaches BlitFrame. It scales
the JPEG to the full destination before intersecting with the surface. A small
JPEG targeting a huge or completely offscreen rectangle can therefore demand
hundreds of MB independently of the actual surface. The small safe regression
uses destination width `0x40000000`, height 1: the output byte count wraps to zero,
only four bytes are rented, and Raster.Scale throws IndexOutOfRangeException.
The exception escapes the display handler and disconnects the session. This
does not require an oversized JPEG.

**Suggested fix:** validate stream destination dimensions/output-byte bounds
before scaling. Clip first and scale only the visible subregion using the
original coordinate mapping; reject impossible rectangles with a protocol/canvas
error. Apply checked allocation arithmetic in DecodedImage as well.

**Reproduction:** `CoreReviewTests.VideoDestinationSizeMustBeCheckedBeforeScaling`.

## 4. high — Retained canvas resources have no aggregate memory budget

**Location:** `src/Spice.Canvas/Canvas.cs:65`, `:156`,
`src/Spice.Canvas/ImageDecoder.cs:142`, `src/Spice.Canvas/Surface.cs:20`.

A server can continually create new surface IDs or cache distinct image/palette
IDs without invalidating old ones. The dictionaries have no byte/count ceiling;
the per-image limits do not bound session memory. Even one accepted 16384x16384
surface allocates 1 GiB of pinned pixels. Repeated valid smaller resources retain
memory indefinitely. Glyph coverage also expands packed A1 data eightfold before
clipping (`Canvas.cs:377`): a roughly 62.5 MiB A1 glyph can request roughly
500 MiB of coverage although the destination is tiny. These are source-verified
allocation paths; the review did not intentionally exhaust the test process.

**Suggested fix:** enforce decoded-resource and aggregate session byte budgets
before surface/image/palette allocations, honor negotiated cache capacity, and
reject peers exceeding it rather than evicting still-promised cached resources
silently. Restrict glyph coverage to visible pixels and bound total glyph work.

## 5. high — Outbound agent queue grows without a limit when tokens are withheld

**Location:** `src/Spice.Session/SessionAgent.cs:91` through `:113`, `:231`.

SendClipboardData has no message-byte ceiling; SendAgentMessage materializes a
full copy plus all chunk arrays and enqueues them even with zero send tokens.
A peer that withholds tokens while repeatedly requesting clipboard data can
accumulate every UI response indefinitely. Ordinary repeated clipboard changes
can also trigger this when the peer stops granting credits. The test queues five
1 MiB responses with no credits and observes 5,243,000 retained chunk bytes.
The existing 64 MiB inbound MaxMessage does not protect this outbound path.

**Suggested fix:** cap clipboard bytes and total pending agent bytes before
copying, reject oversized messages, and coalesce/cancel stale clipboard responses
where protocol ordering allows. Fail the agent cleanly if a peer refuses to
drain a bounded queue. Do not solve this by blocking the UI indefinitely.

**Reproduction:** `CoreReviewTests.OutboundAgentQueueMustBoundBytesWhenServerWithholdsTokens`.

## 6. high — Offscreen strokes multiply line walking by every clip rectangle

**Location:** `src/Spice.Canvas/Canvas.cs:399` through `:432`,
`src/Spice.Canvas/Raster.cs:285`.

DrawStroke passes every segment to Bresenham before clipping the segment. Plot
then scans all clip rectangles for every generated point. The 65,536-iteration
line guard does not bound total command work. A message with 65,536 overlapping
1x1 clip rectangles and just two extreme FIXED28_4 points far outside the surface
can perform approximately 2^32 rectangle checks under Canvas.Sync for a line
that draws no visible pixel. The clip list fits in roughly 1 MiB; a longer path
multiplies the cost. This is a source-verified CPU denial-of-service path, not an
infinite-loop claim, and was not executed against the runner.

**Suggested fix:** normalize the clip union, geometrically clip segments before
line rasterization, and cap aggregate path/raster work per command. Reject
excessively expensive geometry rather than holding the canvas lock indefinitely.

## 7. med — Essential-channel disconnect does not stop sibling channel loops

**Location:** `src/Spice.Session/SpiceSession.cs:151` through `:157`.

RaiseDisconnected sets a flag and fires the event but neither cancels _cts nor
closes sibling streams. After main or display EOF, an otherwise open playback,
display or agent channel can continue producing events until the application
explicitly disposes the session. A caller that follows the documented
"session ended" notification can receive later frame/audio events, and an open
audio output may remain active. The test invokes the EOF transition and confirms
that the receive-loop cancellation token is still live; it does not simulate a
physical device or UI callback race.

**Suggested fix:** make the session terminal atomically, cancel sibling loops,
close owned channels, and establish a defined ordering for final audio-stop and
disconnect notifications. Avoid synchronously waiting for the current receive
task from inside its own handler.

**Reproduction:** `CoreReviewTests.EssentialDisconnectMustCancelOtherChannels`.

## 8. med — Failure linking an essential channel leaks successful sibling links

**Location:** `src/Spice.Session/SpiceSession.cs:69` through `:83`, `:104`.

All links finish in Task.WhenAll, but successful channels are only tracked when
the subsequent ordered foreach reaches Attach. If an early essential channel
failed, throwing there abandons successful later results. DisposeAsync only
closes tracked channels. A valid server channel list ordered inputs, display,
cursor therefore leaks display/cursor connections when the inputs link fails.
The reproduction supplies that channel ordering and uses existing fixture
handshakes for successful peers; both remain undisposed after session disposal.
The test explicitly closes them in its own finally block.

**Suggested fix:** take ownership of every successfully linked channel before
checking essential failures, or dispose all successes in a failure cleanup path.
Also clean up successes when Task.WhenAll itself faults/cancels.

**Reproduction:** `CoreReviewTests.FailedEssentialLinkMustDisposeOtherSuccessfulLinks`.

## 9. med — Agent data is still dispatched after agent-disconnected

**Location:** `src/Spice.Session/SessionAgent.cs:69`, `:117`.

OnAgentDisconnected clears running state and the outbound queue, but does not
clear inbound reassembly or gate OnAgentData. A later MAIN_AGENT_DATA chunk can
still emit GuestClipboardData while AgentConnected is false, including completing
a partially assembled message from before disconnection. That permits clipboard
events after the application has been told the guest agent is unavailable.

**Suggested fix:** discard inbound bytes while stopped and reset header/body fill
state and capabilities on disconnect. Establish the new connection's state
before accepting new chunks.

**Reproduction:** `CoreReviewTests.StoppedAgentMustIgnoreClipboardData`.

## 10. med — Overlapping clip rectangles apply non-idempotent draws repeatedly

**Location:** `src/Spice.Canvas/Canvas.cs:202` through `:220`, `:262`.

ClipRects intersects each rectangle individually but never forms their union;
draw operations then process every result. If a clip list contains overlapping
rectangles, pixels in the overlap are XORed/inverted or blended more than once.
The test supplies a two-pixel XOR fill with a second one-pixel overlapping clip:
pixel zero ends black instead of white. The same issue affects non-idempotent
operations beyond XOR. Upstream uses a pixman region for the destination clip.

**Suggested fix:** normalize the rectangle union into nonoverlapping spans/rects
before executing the draw, or guarantee one application per covered pixel.

**Reproduction:** `CoreReviewTests.OverlappingClipRectanglesMustApplyXorOnlyOnce`.

## 11. med — A4/A8 glyph coverage is collapsed into a binary mask

**Location:** `src/Spice.Canvas/Canvas.cs:384` through `:393`.

All nonzero A4/A8 samples become coverage 1, and Raster.Draw treats the mask as
boolean. A half-covered white glyph on black becomes solid white, rather than
gray. The A8 one-pixel test expects `0xFF808080` and obtains `0xFFFFFFFF`.
spice-common preserves A4/A8 mask intensities in canvas_put_glyph_bits and
composites text with PIXMAN_OP_OVER (`common/canvas_base.c:1610`,
`common/sw_canvas.c:1115`). This is visible corruption of anti-aliased text,
not just a rejection of an unsupported glyph format.

**Suggested fix:** retain full coverage values (expand A4 consistently with
upstream) and composite foreground with coverage using OVER semantics. Keep A1
as binary coverage and clip glyph processing before allocating large masks.

**Reproduction:** `CoreReviewTests.A8GlyphMustPreserveFractionalCoverage`.

## Validation and limits

- `dotnet test tests/Spice.Wire.Tests`: 3 deliberately failing cases, no skips.
- `dotnet test tests/Spice.Replay.Tests`: 7 deliberately
  failing review regressions and 4 passing existing desktop/rich replays.
- Failures are ordinary assertions/managed exceptions; no test intentionally
  crashes the runner or exhausts memory. Private-state tests and certificate
  callback tests are explicitly unit-level checks.
- Static inspection also covered HTTP CONNECT's bounded response reader,
  RSA-OAEP-SHA1/NUL ticket construction, send serialization, ack-window updates,
  source/destination clipping and CopyBits overlap direction. No additional
  proven defect in those paths is claimed here. CustomRootTrust is present;
  finding 1 concerns certificate purpose, not an observed fallback to OS roots.
- This is a bounded correctness/security review, not exhaustive fuzzing or a
  complete validation of native codecs, real TLS handshakes, or physical devices.
