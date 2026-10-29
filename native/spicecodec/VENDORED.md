> Historical SpiceSee import record. See ../VENDORED.md for subsequent SpiceSharp safety modifications.

# Vendored sources

This directory contains third-party source copied verbatim into SpiceSee. It is the LGPL
source-publication record: every file below is listed with its upstream origin, revision, licence,
and any local modification. **A file on disk but missing from this list is a compliance failure** —
`scripts/check-vendored-notices.sh` enforces that, and must be re-run after any edit here and before
cutting a release.

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

Only two vendored files are edited; `quic.c`, `lz.c`, `mem.c` and every template file are byte-for-byte
upstream. Each edit is marked in the source with a `SPICESEE MODIFICATION` comment, and the commit
that introduced them sits directly on top of the commit that vendored the pristine copies, so
`git diff` shows the change set exactly.

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

## Known limitation

If a GLZ stream fails validation *after* `glz_image_new()` has allocated — a corrupt back-reference
rather than a corrupt header — the unwind skips `glz_decoder_window_add()` and that one buffer is
leaked, because the window never takes ownership. It is bounded by the image size and cannot be
reached today (nothing routes GLZ until task 14 wires `ImageDecoder`), but it is a resource-exhaustion
vector against a hostile server and should be closed when task 14 exercises this path — most likely
by having `decode()` record the in-flight image so the window can free it.
