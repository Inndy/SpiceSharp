# Native vendoring verification

Checked against local reference git objects on 2026-10-29. Both exact byte comparisons and comparisons normalized to LF were performed; the table reports normalized comparisons. The pinned historical commits are recorded by SpiceSee but unavailable in the local shallow clones. `comparison-*.patch` records every substantive difference against the checked local reference HEADs. Original origins and modification details follow the table. Newer reference revisions are comparison targets, not a claim that these older sources have been upgraded.

| Local file | Upstream path | SpiceSee source commit and comparison | Local reference HEAD and comparison |
|---|---|---|---|
| `spicecodec/vendor/common/draw.h` | spice-common `common/draw.h` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/lz.c` | spice-common `common/lz.c` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/lz.h` | spice-common `common/lz.h` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/lz_common.h` | spice-common `common/lz_common.h` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/lz_compress_tmpl.c` | spice-common `common/lz_compress_tmpl.c` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (differs beyond line endings) |
| `spicecodec/vendor/common/lz_config.h` | spice-common `common/lz_config.h` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/lz_decompress_tmpl.c` | spice-common `common/lz_decompress_tmpl.c` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/macros.h` | spice-common `common/macros.h` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/mem.c` | spice-common `common/mem.c` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/mem.h` | spice-common `common/mem.h` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/quic.c` | spice-common `common/quic.c` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/quic.h` | spice-common `common/quic.h` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/quic_config.h` | spice-common `common/quic_config.h` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/quic_family_tmpl.c` | spice-common `common/quic_family_tmpl.c` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/quic_tmpl.c` | spice-common `common/quic_tmpl.c` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/common/verify.h` | spice-common `common/verify.h` | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` (revision unavailable) | `71e45706981973014eaab3d4b533d35d79e19ffa` (identical after LF normalization) |
| `spicecodec/vendor/decode-glz-tmpl.c` | spice-gtk `src/decode-glz-tmpl.c` | `f04479c16f0969fb394ebe74b6eff74e560a42f0` (revision unavailable) | `4086377fb5379a849a7444a6b32686ae14856255` (identical after LF normalization) |
| `spicecodec/vendor/decode-glz.c` | spice-gtk `src/decode-glz.c` | `f04479c16f0969fb394ebe74b6eff74e560a42f0` (revision unavailable) | `4086377fb5379a849a7444a6b32686ae14856255` (differs beyond line endings) |
| `spicecodec/vendor/decode.h` | spice-gtk `src/decode.h` | `f04479c16f0969fb394ebe74b6eff74e560a42f0` (revision unavailable) | `4086377fb5379a849a7444a6b32686ae14856255` (differs beyond line endings) |
| `spicecodec/vendor/spice/barrier.h` | spice-protocol `spice/barrier.h` | `90b57dace404db564a8e034ad4427b9951071bcd` (revision unavailable) | `499cc8326a672e9e5747efc017319b19e1594b42` (identical after LF normalization) |
| `spicecodec/vendor/spice/end-packed.h` | spice-protocol `spice/end-packed.h` | `90b57dace404db564a8e034ad4427b9951071bcd` (revision unavailable) | `499cc8326a672e9e5747efc017319b19e1594b42` (identical after LF normalization) |
| `spicecodec/vendor/spice/enums.h` | spice-protocol `spice/enums.h` | `90b57dace404db564a8e034ad4427b9951071bcd` (revision unavailable) | `499cc8326a672e9e5747efc017319b19e1594b42` (differs beyond line endings) |
| `spicecodec/vendor/spice/macros.h` | spice-protocol `spice/macros.h` | `90b57dace404db564a8e034ad4427b9951071bcd` (revision unavailable) | `499cc8326a672e9e5747efc017319b19e1594b42` (differs beyond line endings) |
| `spicecodec/vendor/spice/start-packed.h` | spice-protocol `spice/start-packed.h` | `90b57dace404db564a8e034ad4427b9951071bcd` (revision unavailable) | `499cc8326a672e9e5747efc017319b19e1594b42` (identical after LF normalization) |
| `spicecodec/vendor/spice/types.h` | spice-protocol `spice/types.h` | `90b57dace404db564a8e034ad4427b9951071bcd` (revision unavailable) | `499cc8326a672e9e5747efc017319b19e1594b42` (identical after LF normalization) |

## SpiceSee wrapper and shims

`spicecodec/codec_bridge.c`, `spicecodec/include/spice_codec.h`, and every file under `spicecodec/shim/` originate from SpiceSee commit `41435442b59f510f2f3a64eb78b78f06299009a9`, at `Packages/CSpiceCodec/Sources/CSpiceCodec/` with the same relative paths. MIT; see LICENSE.SpiceSee. Header modified solely for SC_API exports and SC_CALL cdecl annotations. That describes the initial import; subsequent task-2 safety edits are listed below. `LICENSE.LGPL-2.1` is retained beside this manifest and in spicecodec/.

# Vendored sources

`spicecodec/vendor/` contains third-party source, imported through SpiceSee. This is the LGPL
source-publication record: every file below is listed with its upstream origin, revision, licence,
and any local modification. **A file on disk but missing from this list is a compliance failure.**
Each modified LGPL file also carries a dated `MODIFIED FILE` notice under its licence header
(LGPL-2.1 section 2b); update it with any further edit.

Nothing in `vendor/` may be reformatted, re-indented, or hand-retyped. The header comments are the
licence grant, not decoration.

## Upstream revisions

| Project | Tag | Commit | Fetched |
|---|---|---|---|
| [spice-gtk](https://gitlab.freedesktop.org/spice/spice-gtk) | `v0.42` | `f04479c16f0969fb394ebe74b6eff74e560a42f0` | 2026-08-23 |
| spice-common (submodule of spice-gtk) | — | `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6` | 2026-08-23 |
| [spice-protocol](https://gitlab.freedesktop.org/spice/spice-protocol) | `v0.14.3` | `90b57dace404db564a8e034ad4427b9951071bcd` | 2026-08-23 |

`spice-protocol` is not a submodule of spice-common at this revision — spice-gtk resolves it as an
external dependency (`spice_protocol_version = '0.14.3'` in `meson.build`), so it was cloned
separately at the version spice-gtk requires.

## Files

### From spice-common `common/` → `vendor/common/`

| File | Licence |
|---|---|
| `quic.c` | LGPL-2.1-or-later |
| `quic.h` | LGPL-2.1-or-later |
| `quic_config.h` | LGPL-2.1-or-later |
| `quic_family_tmpl.c` | LGPL-2.1-or-later |
| `quic_tmpl.c` | LGPL-2.1-or-later |
| `lz.c` | LGPL-2.1-or-later |
| `lz.h` | MIT (derived from [fastlz](http://www.fastlz.org/)) |
| `lz_common.h` | LGPL-2.1-or-later |
| `lz_config.h` | LGPL-2.1-or-later |
| `lz_compress_tmpl.c` | LGPL-2.1-or-later |
| `lz_decompress_tmpl.c` | LGPL-2.1-or-later |
| `macros.h` | LGPL-2.1-or-later |
| `verify.h` | LGPL-2.1-or-later |
| `mem.c` | LGPL-2.1-or-later |
| `mem.h` | LGPL-2.1-or-later |
| `draw.h` | BSD-3-Clause, © 2009 Red Hat |

`mem.c`/`mem.h` are vendored rather than shimmed: `draw.h` needs `SpiceChunks` from `mem.h`, and
`mem.c` is glib-free, so using upstream's real allocator keeps hand-written stand-ins out of the
decode path.

The plan also listed `quic_rgb_tmpl.c` and `bitops.h`. Neither exists at this revision — the RGB
templates were folded into `quic_tmpl.c` upstream — so neither is vendored.

### From spice-gtk `src/` → `vendor/`

| File | Licence |
|---|---|
| `decode-glz.c` | LGPL-2.1-or-later |
| `decode-glz-tmpl.c` | LGPL-2.1-or-later |
| `decode.h` | LGPL-2.1-or-later |

### From spice-protocol `spice/` → `vendor/spice/`

| File | Licence |
|---|---|
| `macros.h` | LGPL-2.1-or-later |
| `types.h` | BSD-3-Clause, © 2009 Red Hat |
| `enums.h` | BSD-3-Clause, © 2009 Red Hat |
| `start-packed.h` | BSD-3-Clause, © 2009 Red Hat |
| `end-packed.h` | BSD-3-Clause, © 2009 Red Hat |
| `barrier.h` | BSD-3-Clause, © 2009 Red Hat |

`LICENSE.LGPL-2.1` is spice-gtk's `COPYING`, copied unmodified.

## Local modifications

SpiceSee edited two vendored files (below); each of its edits is marked with a `SPICESEE MODIFICATION`
comment. SpiceSharp's later edits are listed under "SpiceSharp task-2 safety patch" (2026-10-29) and
"SpiceSharp codec patches" (2026-10-30); the 2026-10-30 ones all carry a `SPICESHARP MODIFICATION` comment.

Everything else upstream expects — glib, pixman, spice-gtk's coroutines, spice-common's logging — is
satisfied by non-upstream headers under `shim/`, which contain no vendored code.

### `vendor/decode-glz.c`

1. **`struct glz_image` no longer holds a pixman surface.** `glz_image_new()` allocated a
   `pixman_image_t` through `alloc_lz_image_surface()`; we have no pixman, and the decoder only ever
   needs a flat BGRA buffer, so it now allocates `gross_pixels * 4` bytes directly and records the
   row stride. `glz_image_destroy()` frees that buffer instead of unreffing the surface. Upstream
   walked `img->data` back to the allocation start for bottom-up images; ours starts there already.
2. **Added `struct glz_image *last` to `SpiceGlzDecoderWindow`**, set in `glz_decoder_window_add()`,
   cleared in `glz_decoder_window_clear()` and in `glz_decoder_window_release()` when the image it
   points at is destroyed. Upstream returns the decoded image through the pixman surface handed in
   via `usr_data`; we pass none, so the bridge reads it from here.
3. **Added `glz_decoder_window_last()` and five `sc_glz_image_*` accessors.** `struct glz_image`
   stays private to the file; `codec_bridge.c` reads it through these.

### `vendor/decode.h`

Declares the accessors added above. No other change.

## Known limitation (fixed)

SpiceSee documented that a GLZ stream failing validation *after* `glz_image_new()` had allocated
leaked that buffer, because the window never took ownership. The SpiceSharp safety patch below
tracks pending allocations and frees them on the fatal unwind.

## Wrapper file inventory

| Local file | SpiceSee upstream path | Commit |
|---|---|---|
| `spicecodec/codec_bridge.c` | `Packages/CSpiceCodec/Sources/CSpiceCodec/codec_bridge.c` | `41435442b59f510f2f3a64eb78b78f06299009a9` |
| `spicecodec/include/spice_codec.h` | `Packages/CSpiceCodec/Sources/CSpiceCodec/include/spice_codec.h` | `41435442b59f510f2f3a64eb78b78f06299009a9` |
| `spicecodec/shim/client_sw_canvas.h` | `Packages/CSpiceCodec/Sources/CSpiceCodec/shim/client_sw_canvas.h` | `41435442b59f510f2f3a64eb78b78f06299009a9` |
| `spicecodec/shim/common/canvas_utils.h` | `Packages/CSpiceCodec/Sources/CSpiceCodec/shim/common/canvas_utils.h` | `41435442b59f510f2f3a64eb78b78f06299009a9` |
| `spicecodec/shim/common/log.h` | `Packages/CSpiceCodec/Sources/CSpiceCodec/shim/common/log.h` | `41435442b59f510f2f3a64eb78b78f06299009a9` |
| `spicecodec/shim/config.h` | `Packages/CSpiceCodec/Sources/CSpiceCodec/shim/config.h` | `41435442b59f510f2f3a64eb78b78f06299009a9` |
| `spicecodec/shim/gio-coroutine.h` | `Packages/CSpiceCodec/Sources/CSpiceCodec/shim/gio-coroutine.h` | `41435442b59f510f2f3a64eb78b78f06299009a9` |
| `spicecodec/shim/glib.h` | `Packages/CSpiceCodec/Sources/CSpiceCodec/shim/glib.h` | `41435442b59f510f2f3a64eb78b78f06299009a9` |
| `spicecodec/shim/log.h` | `Packages/CSpiceCodec/Sources/CSpiceCodec/shim/log.h` | `41435442b59f510f2f3a64eb78b78f06299009a9` |
| `spicecodec/shim/os_dep.h` | `Packages/CSpiceCodec/Sources/CSpiceCodec/shim/os_dep.h` | `41435442b59f510f2f3a64eb78b78f06299009a9` |
| `spicecodec/shim/spice-common.h` | `Packages/CSpiceCodec/Sources/CSpiceCodec/shim/spice-common.h` | `41435442b59f510f2f3a64eb78b78f06299009a9` |
| `spicecodec/shim/spice-util.h` | `Packages/CSpiceCodec/Sources/CSpiceCodec/shim/spice-util.h` | `41435442b59f510f2f3a64eb78b78f06299009a9` |
| `spicecodec/shim/spice_common.h` | `Packages/CSpiceCodec/Sources/CSpiceCodec/shim/spice_common.h` | `41435442b59f510f2f3a64eb78b78f06299009a9` |

## SpiceSharp task-2 safety patch (2026-10-29)

The comparison table and comparison-*.patch artifacts above describe the initial
SpiceSee import. The following additional local modifications now apply; codecs
remain upstream implementations and the 17-function public ABI is unchanged.

- `spicecodec/codec_bridge.c`: preflight QUIC/LZ/GLZ header lengths, types,
  dimensions (maximum 64M pixels), and LZ palette stride/count before invoking
  upstream parsers. Keep the LZ palette in the decoder handle instead of freeing
  it between begin and decode (fixes a use-after-free and per-call allocation).
  Palette output includes padded pixels, which managed LzDecoder accommodates in
  retained scratch storage. Set the GLZ compressed input end for checked reads;
  on fatal unwind, free any GLZ allocation not yet owned by the window.
- `spicecodec/vendor/common/lz.c`: add a palette-index bounds helper.
- `spicecodec/vendor/common/lz_decompress_tmpl.c`: use that helper for PLT8
  literals. Original origin is spice-common `common/lz_decompress_tmpl.c`, commit
  `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6`, LGPL-2.1-or-later, as in the table.
- `spicecodec/vendor/decode-glz.c`: checked byte reader used by header/templates;
  track pending allocations for cleanup; check allocation failures; bound full
  cross-image reference length, reject duplicate/expired IDs and unreasonable
  window growth (65536 slots), ensure tail-gap entries match their actual IDs,
  and handle huge release-ID gaps in bounded time while retaining future images.
  Reject underflowing window-head distances and UINT64_MAX image IDs.
- `spicecodec/vendor/decode-glz-tmpl.c`: replace compressed byte dereferences with
  the checked reader, bound length extension, and pass match length to the
  cross-image reference bounds check. Original origin is spice-gtk
  `src/decode-glz-tmpl.c`, commit `f04479c16f0969fb394ebe74b6eff74e560a42f0`,
  LGPL-2.1-or-later.

These edits fix the previously documented GLZ pending-image leak. No decoder was
rewritten. Native C smoke tests and managed codec tests ran against the rebuilt
x64 DLL; mutation coverage is finite and is not a security audit of upstream.

## LZ4 decoder (2026-10-30)

lz4 is not vendored. Like libjpeg-turbo and opus, it is a pinned download:

| Project | Tag | Release asset | SHA-256 | Licence |
|---|---|---|---|---|
| [lz4](https://github.com/lz4/lz4) | `v1.10.0` | `https://github.com/lz4/lz4/releases/download/v1.10.0/lz4-1.10.0.tar.gz` | `537512904744b35e232912055ccf8ec66d768639ff3abe5788d90d792ec5f48b` | `lib/`: BSD-2-Clause (`lib/LICENSE`) |

`fetch.sh` downloads and checks it into `downloads/lz4-1.10.0`. Only `lib/lz4.c` (with `lib/lz4.h`) is compiled,
unmodified, into spicecodec.dll; `test/smoke.c` compiles its own copy to produce test streams. The rest of the
tarball (the GPL-2.0 command-line programs, lz4frame, lz4hc, xxhash) is not built.

SpiceSharp additions to the SpiceSee wrapper files:

- `spicecodec/include/spice_codec.h`: declares `sc_lz4_decode`, the 18th exported function.
- `spicecodec/codec_bridge.c`: implements it on `LZ4_decompress_safe_continue`, following spice-common
  `canvas_get_lz4` (`common/canvas_base.c`) for the record framing and bounds checks. No vendored file changed.

## SpiceSharp codec patches (2026-10-30)

The human allowed targeted patches to the vendored decoders on 2026-10-30, each verified by the differential test
in `difftest/` against the previous build ([../docs/decisions/0029](../docs/decisions/0029-vendored-codec-patches-allowed-with-differential-verification.md)).
Each edit is marked `SPICESHARP MODIFICATION`, and each file's `MODIFIED FILE` notice is dated 2026-10-30. Origins and
licences are as in the tables above: spice-common commit `58d375e5eadc6fb9e587e99fd81adcb95d01e8d6`, spice-gtk commit
`f04479c16f0969fb394ebe74b6eff74e560a42f0`, LGPL-2.1-or-later.

Fixes for the review in `docs/reviews/2026-10-30-native-codecs.md`:

- `spicecodec/vendor/decode-glz.c`:
  - N1: the window counts the capacity of the image buffers it holds (a reused buffer can be larger than its
    image) and rejects an image that would take it past `SC_GLZ_WINDOW_BUDGET` (128 MiB) before allocating it;
    a pooled buffer is reused only if its capacity fits the remaining budget.
  - N2: `glz_decoder_window_clear` also resets `oldest`, the byte count and the reuse pool.
  - N3: failed slot-array allocations in `glz_decoder_window_resize` unwind through `sc_fatal` (the pending image is
    freed). `glz_decoder_window_clear` keeps its old, emptied array if the new one can't be allocated.
    `glz_decoder_window_new` and `glz_decoder_new` return NULL on failure.
  - N5: retired images go to a pool of at most 32 images and 32 MiB, and `glz_image_new` (which now receives the
    window) reuses the smallest pooled buffer that fits without wasting more than about half of it. Reused buffers
    are not zeroed: a successful decode writes every pixel, and a failed one discards the image. Freshly allocated
    buffers are still zeroed, as upstream does.
- `spicecodec/codec_bridge.c` (MIT wrapper, not vendored): N3, `sc_glz_window_create` and `sc_glz_create` return
  NULL when the window or decoder can't be allocated. N4: `sc_lz_decode` takes the output capacity and writes
  `width * 4`-byte rows: palette images with padded rows decode in place when the buffer holds the padded rows
  (then compacted), else into scratch storage kept in the handle, up to 4 MiB, writing nothing past
  `width * height * 4`. `sc_abi_version` (2) marks the changed contract. `spicecodec/include/spice_codec.h`
  documents this. `src/Spice.Native/Decoders.cs` (`LzDecoder`) passes a padded-size buffer, reads the tight rows,
  and refuses a DLL of another interface version.

Speed (provisional numbers in decision 0029):

- `spicecodec/vendor/common/lz_decompress_tmpl.c`: match copies of every instance but `LZ_RGB_ALPHA` (which copies
  only the alpha byte) are `memcpy` blocks. An overlapping reference copies its period, then the doubled run, and
  so on. Runs are a plain fill. The per-pixel `op <= op_limit` checks are dropped; the
  `op + len <= op_limit` check before them still applies.
- `spicecodec/vendor/decode-glz-tmpl.c`: the same for GLZ. A reference into another image is one `memcpy`: other
  images never overlap the output.
- `spicecodec/vendor/common/quic.c`: `decode_state_run` counts leading one bits with
  `__builtin_clz(~io_word | 0x00FFFFFF)` (LZCNT on x86-64-v3) instead of the `lzeroes` table; includes
  `immintrin.h` when AVX2 is enabled. `cnt_l_zeroes` already used `__builtin_clz` upstream.
- `spicecodec/vendor/common/quic_family_tmpl.c`: with `__AVX2__` (x64 builds, decision 0026), `update_model` for
  8 bits per channel adds the 8 code lengths to the bucket's 8 counters with one vector add and takes the
  best code from a vector minimum. The scalar loop kept the highest index among equal minima, and so does this.
  The 5-bit family and builds without AVX2 keep the scalar loop.

Measured and not kept (no gain beyond noise): unchecked reads for LZ and GLZ literal runs whose bytes are all
present. A 64-bit QUIC bit reader was not attempted: refills already happen once per 32 bits, and reading ahead
would make truncated streams fail at different points.

## SpiceSharp memory trim (2026-10-31)

For finding K1 of `docs/verification/2026-10-30-v0-dod.md` ([../docs/decisions/0041](../docs/decisions/0041-idle-memory-is-trimmed-on-minimize-and-video-end.md)).
The edit is marked `SPICESHARP MODIFICATION (2026-10-31)`, and the file's `MODIFIED FILE` notice has a dated
2026-10-31 line. Origin and licence as above (spice-gtk commit `f04479c16f0969fb394ebe74b6eff74e560a42f0`,
LGPL-2.1-or-later).

- `spicecodec/vendor/decode-glz.c`: added `sc_glz_window_trim_pool()`, which frees the retired images kept for
  reuse (N5 above) and returns their pixel bytes. The window's own images, which later GLZ images may reference,
  are untouched. No decode path changed: the differential test (`difftest/run.sh --quick`) passed against the
  previous commit.
- `spicecodec/codec_bridge.c` and `spicecodec/include/spice_codec.h` (MIT wrapper, not vendored): export it as
  `sc_glz_window_trim(window)`. It is optional: the interface version stays 2, and the viewer treats a DLL without
  it as having nothing to trim. `difftest/review-regressions.c` checks the bytes freed, that the current image
  stays and that decoding continues.

## FFmpeg H.264 decoder (2026-10-31)

FFmpeg is not vendored. Like libjpeg-turbo, opus and lz4, it is a pinned download, built unmodified:

| Project | Release | Tarball | SHA-256 | Licence |
|---|---|---|---|---|
| [FFmpeg](https://ffmpeg.org) | 9.0.2 (2026-09-18) | `https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz` | `8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e` | LGPL-2.1-or-later as configured (`LICENSE.md`, `COPYING.LGPLv2.1`) |

Verified on 2026-10-31: `gpg --verify ffmpeg-9.0.2.tar.xz.asc` reports a good signature by the FFmpeg release signing
key, fingerprint `FCF9 86EA 15E6 E293 A564 4F10 B432 2F04 D676 58D8` (imported from `https://ffmpeg.org/ffmpeg-devel.asc`,
the key ffmpeg.org's download page names; the same key has signed FFmpeg releases since 2011). The `.tar.gz` of the same
release, also signed, extracts to an identical tree. The hash was then checked again on a second download, on Windows.

`build.sh` configures it with only the H.264 decoder and parser, without `--enable-gpl` or `--enable-nonfree` (the
configure line is in README.md), and builds `libavcodec.a` and `libavutil.a`, which spicecodec.dll links statically, so
all LGPL code stays in that one replaceable DLL. `--gc-sections` drops what the H.264 decoder doesn't reach. No FFmpeg
file is changed, so no `MODIFIED FILE` notice applies; the release zip ships the tarball itself.

SpiceSharp additions (MIT, not vendored):

- `spicecodec/h264_bridge.c`: the `sc_h264_*` functions (README.md, "ABI and binding decisions").
- `spicecodec/include/spice_codec.h`: declares them and `sc_h264_picture`.
- `test/smoke.c`: opens a decoder, checks junk and empty input and a conversion without a picture.
