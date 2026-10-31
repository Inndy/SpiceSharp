# MSVC native codec build review

Scope: `build/msvc-codec`, native compiler portability, DLL ABI and hardening,
source notices, and package provenance. The human explicitly authorized Codex to
implement this task; this is implementation self-review, not an independent review.
No guest, Proxmox or credential data was used.

## Findings and response

- **P2, resolved: QUIC initialization was discarded under MSVC LTCG.** The upstream
  static `.CRT$XCU` pointer was removed by `/GL` and `/OPT:REF`, and the existing
  QUIC round-trip smoke test failed. The constructor/destructor macros now expose
  and force-include their CRT pointers on MSVC. The public exports stay unchanged.
- **P2, resolved: ARM64 builds selected host SIMD and linker architecture.** Set
  CMake's target processor explicitly, recognize `_M_ARM64` endianness, and give
  `.def` links a COFF input and explicit machine type. The PE verifier now checks
  machine type as well as exports, imports and protections for both architectures.
- **P2, resolved: package notices described MinGW for MSVC DLLs.** Build metadata
  follows the DLLs through publish; LICENSE.txt uses its actual FFmpeg arguments,
  names the extra upstream debug-helper object, and selects the matching runtime
  license. Microsoft's static runtime text is included for MSVC builds.
- **P3, resolved: localized compiler output broke incremental header dependencies.**
  Configure/build use UTF-8 console output so CMake and Ninja agree on the localized
  `/showIncludes` prefix. No global language or profile setting is changed.

## Remaining limits

No open code finding. ARM64 DLLs were built and inspected on x64, not executed on
ARM64. Full validation and any test-run limitations are recorded in
[the verification note](../verification/2026-11-01-msvc-codec.md).
