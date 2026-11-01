# Clang-default CI integration self-review

Scope: combine the GitHub CI branch with optional MSVC/VS support using rebase
and fast-forward, preserving the human's explicit Clang default. Codex implements
and reviews this authorized integration; this is not an independent review.

## Findings and response

- **P2, resolved: the AOT developer environment changed the default platform.**
  Its ambient `Platform=x64` made SDK MSBuild select the optional native project
  during the hosted solution build. GitHub and GitLab now explicitly select
  solution `Any CPU` and test-project `AnyCPU` when consuming the Clang outputs.
  MSVC remains available through explicit VS solution configurations.
- **P2, resolved: hosted builds could not fetch FFmpeg from upstream.** The
  first run failed on both targets before compilation with curl TLS resets;
  a local Windows HTTP client also could not reach that endpoint. CI now tries
  upstream and then Alpine Linux's source cache, requiring the original
  fetch.sh hash from either. A hash mismatch fails immediately. The exact
  workflow block downloaded the fallback locally and matched the existing pin.
  No source version, source bytes or native compiler setting changed.
- **P2, resolved: the initial GitHub workflow selected MSVC codecs.** That
  conflicted with the human's final instruction to keep Clang as default.
  Both jobs now invoke `native/build.sh` with the SHA-256-pinned llvm-mingw
  20261020 UCRT cross-toolchain. MSVC setup follows codec compilation and is used
  by Native AOT publishing. The Any CPU managed build does not invoke the
  optional MSVC native project. GitLab's native build already uses Clang; only
  its managed platform selection is made explicit.
- **P3, resolved: the two branches allocated decision number 0047.** Retained
  the original GitHub CI record as 0047 and renumbered the MSVC tuning/solution
  record to 0048, including links. The rebase's sole conflict was the decision
  index; both entries are retained in order.

The workflow retains read-only repository permissions, hash-pinned actions,
nonpersistent checkout credentials, architecture verification, sequential
window-free test projects, archive validation and separate test artifacts.
The new compiler archive is checked against the official release asset's
published digest before extraction. No signing, release publication, guest
connection or real credential use is introduced.

Integration validation is recorded in
[the verification note](../verification/2026-11-01-clang-ci-integration.md).
