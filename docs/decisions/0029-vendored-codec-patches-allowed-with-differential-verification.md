# 0029. Vendored codec patches are allowed when a differential test proves them

- Status: Accepted
- Date: 2026-10-30
- Decided by: the human (2026-10-30: targeted improvements to the vendored codec C code are allowed, patches and not
  rewrites, provided behavioural correctness is verified aggressively; speed measured on the busy development
  machine is provisional). What "verified" means below, and the patches, are by agent.

- **Context:**
  - [spec.md](../design/spec.md) says the QUIC, LZ and GLZ decoders MUST NOT be rewritten. A C# port is allowed later
    only as a separate LGPL assembly, verified by differential fuzzing against the C DLL.
  - Codex's review [2026-10-30-native-codecs.md](../reviews/2026-10-30-native-codecs.md) found five problems in the
    vendored GLZ code and the bridge (N1 to N5).
  - Three independent reviews proposed speed patches. [principles.md](../design/principles.md) ranks speed and
    energy first, and asks for x86-64-v3 instructions where they help.
- **Decision:**
  - The vendored decoders may be patched: targeted edits, never a rewrite or a reformat. Each edit is marked
    `SPICESHARP MODIFICATION`, dates the file's `MODIFIED FILE` notice and is listed in
    [native/VENDORED.md](../../native/VENDORED.md).
  - A patch is "verified" when `bash native/difftest/run.sh` passes against the previous build (by default the
    sources at `HEAD`), and `--asan` passes too:
    - Every input goes through the reference and the patched DLL. Both must fail, or both succeed with the same
      return codes, dimensions, type, stride and every output byte, including guard bytes around the buffer.
    - The inputs are the fixture recordings' QUIC, LZ and GLZ images (GLZ as one dictionary per recording) and
      synthetic images of every type the vendored encoders produce, in 24 sizes and 8 content kinds. GLZ also gets
      synthetic sequences from a test encoder that covers every reference form (same image and other images, short,
      far and very far offsets, overlapping copies, out-of-order IDs, clears). On top come seeded mutations of all
      of them: bit flips, truncation, garbage, edge values in header fields, swapped headers, deleted and inserted
      chunks.
    - A behaviour change on purpose is allowed only if it is listed in the harness with its finding ID, and the
      harness checks the new behaviour itself.
    - `faultinject.exe` fails every heap allocation of a QUIC, LZ and GLZ session in turn. It must not crash or
      leak, and warmed-up decoding must not allocate.
    - `--asan` runs the same under AddressSanitizer and UBSan. The only suppression is the vendored LZ encoder's
      hash overflow: test-input code the viewer doesn't use.
    - `bash native/build.sh x64` (smoke test, `verify.py`) and `dotnet test` stay green, the replay goldens included.
  - A speed patch is kept only if it passes the above and `native/bench/run.sh` shows it is not slower beyond noise.
    Speed figures from the development machine are provisional; `native/README.md` says how to rerun the same
    benchmark on a quiet machine.
  - Patches applied on 2026-10-30 (details in VENDORED.md):

    | Patch | Files | Kind |
    |---|---|---|
    | N1: the GLZ window holds at most 128 MiB of decoded images | `decode-glz.c` | intentional behaviour change |
    | N2: clearing the GLZ window resets the oldest-ID floor | `decode-glz.c` | intentional behaviour change |
    | N3: failed GLZ slot-array, window and decoder allocations return errors | `decode-glz.c`, `codec_bridge.c` | only under allocation failure |
    | N4: `sc_lz_decode` writes exactly `width*height*4` bytes, palette rows included | `codec_bridge.c`, `spice_codec.h`, `Decoders.cs` | intentional C ABI change |
    | N5: retired GLZ images are reused (pool of 32 images, 32 MiB) | `decode-glz.c` | output identical |
    | LZ match copies are block copies (fill, `memcpy`, period doubling) | `lz_decompress_tmpl.c` | output identical |
    | GLZ match copies likewise; cross-image references are one `memcpy` | `decode-glz-tmpl.c` | output identical |
    | QUIC run-length decoder counts leading ones with `__builtin_clz` (LZCNT) | `quic.c` | output identical |
    | QUIC `update_model` for 8-bit channels uses AVX2 (x86-64-v3 builds only) | `quic_family_tmpl.c`, `quic.c` | output identical |

  - Tried and dropped: unchecked reads for complete LZ and GLZ literal runs (no gain beyond noise).
  - Not attempted: a 64-bit QUIC bit reader. Refills already happen once per 32 bits, and reading ahead would make
    truncated streams fail at different points.
- **Reason:**
  - The differential test turns "patched C behaves like the vendored C" into something checked on every change,
    over real traffic, every image type and many thousands of malformed streams. A patch that passes it is lower
    risk than leaving a known bug in, and keeping the code in C still honours the spec's rule against rewrites.
  - Keeping the old build as the reference makes the test usable for future patches too: pass `--ref` to compare
    against any revision.
- **Cost:**
  - The vendored files drift further from upstream. Every edit is marked and listed, so it can be rebased or
    dropped.
  - N4 changes what `sc_lz_decode` writes for palette images with padded rows (tight rows instead of padded ones).
    `LzDecoder` was updated in the same change. Because a replaced DLL could keep the old contract under the same
    names, `sc_lz_decode` also takes the output capacity and the DLL reports `sc_abi_version` 2; the viewer refuses
    LZ with another version (review R4, 2026-10-30). `LzDecoder` passes room for the padded rows, so the decode runs
    in place without allocating; only a C caller with a tight buffer uses the handle's scratch, and one larger than
    4 MiB is allocated on every such decode (review R5).
  - The GLZ window keeps up to 32 MiB of retired images for reuse while a session runs.
  - The 128 MiB GLZ budget is twice what a well-behaved server can make the client hold with the advertised
    32 MiB dictionary. A server that goes past it gets its images rejected until it releases images or the display
    is reset.

## Measurements

Provisional: taken on the busy development machine (AMD Ryzen AI MAX+ 395, pinned to one core at HIGH
priority), not a quiet one. `native/bench/run.sh` interleaves the two DLLs A B B A over 31 repetitions and reports
median wall time, thread cycles and paired B/A ratios with a 95% confidence interval. Two copies of the same DLL
measured B/A 0.993 to 1.020 on this machine, so changes within about 2% are noise. Rerun on a clean machine as
`native/README.md` describes before quoting these numbers anywhere else.

The tree before these patches ("Before") against the patched tree, median wall time per batch. All outputs were
identical:

| Workload | Before | Final | B/A |
|---|---|---|---|
| `quic_fixture` (the fixture's QUIC images) | 16.59 ms | 13.37 ms | 0.806 |
| `lz_fixture` | 2.36 ms | 0.61 ms | 0.252 |
| `glz_fixture` (both GLZ dictionaries) | 8.28 ms | 7.63 ms | 0.922 |
| `quic_desktop720p` | 9.56 ms | 9.30 ms | 0.974 |
| `lz_desktop720p` | 3.45 ms | 2.94 ms | 0.852 |
| `lz_periodic720p` | 0.95 ms | 0.14 ms | 0.144 |
| `glz_scroll720p` | 15.38 ms | 9.57 ms | 0.615 |
| `glz_periodic720p` | 4.66 ms | 3.41 ms | 0.731 |

Per patch, each against the tree before it (B/A):

| Patch | Result | Kept |
|---|---|---|
| N1 to N5 (against the unpatched sources) | `glz_scroll720p` 0.734, `glz_periodic720p` 0.795 (N5's buffer reuse), the rest about 1.0 | yes (fixes) |
| LZ block copies | `lz_fixture` 0.236, `lz_periodic720p` 0.139, `lz_desktop720p` 0.857 | yes |
| GLZ block copies | `glz_fixture` 0.911, `glz_scroll720p` 0.899, `glz_periodic720p` 0.960 | yes |
| QUIC `__builtin_clz` | `quic_desktop720p` 0.992 (CI 0.987 to 0.996), `quic_fixture` 0.996 (CI includes 1.0) | yes: not slower, fewer instructions |
| QUIC AVX2 `update_model` | `quic_fixture` about 0.80, `quic_desktop720p` 0.966 | yes |
| LZ unchecked literal reads | `lz_desktop720p` 1.020 | no |
| GLZ unchecked literal reads | `glz_fixture` 1.002, `glz_scroll720p` 0.979 (CI 0.959 to 1.021), `glz_periodic720p` 1.06 | no |

`update_model` was patched because a sampling profile of QUIC decoding put about 18% of the time there; it wasn't
on the reviews' list. `spicecodec.dll` grew from 145,408 bytes before these patches to 151,040.

Coverage of the patched DLL by one difftest run (`--coverage`, line coverage): `lz_decompress_tmpl.c` 100%,
`decode-glz.c` 94%, `decode-glz-tmpl.c` 93%, `quic_family_tmpl.c` 89%. `quic.c` (66%) and `quic_tmpl.c` (53%) also
hold the encoders and decode paths the bridge can't reach (GRAY, same-format RGB16 and RGB24 output).
