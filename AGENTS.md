# Agent guide

Instructions for coding agents working in this repository (Claude Code reads this through CLAUDE.md; Codex reads it
directly).

## Sources of truth

1. [docs/design/spec.md](docs/design/spec.md): what to build. Its MUST rules, rejected alternatives and energy rules
   are binding. Its open questions belong to the human: ask, don't decide.
   [docs/design/principles.md](docs/design/principles.md) ranks the trade-offs (performance and energy, then memory,
   then binary size; aggressively opt in to new ISA and APIs above an x86-64-v3 floor) and is binding the same way.
2. [docs/decisions/](docs/decisions/README.md): choices made where the spec is silent. Read the relevant records before
   changing behaviour they cover. Record every new choice there, in the same change, using the template.
3. The code. When code and a decision record disagree, the record wins until the human says otherwise.

## Roles

- Claude (and Claude subagents) implement.
- Codex reviews and writes tests. It doesn't edit production code; findings go to [docs/reviews/](docs/reviews/README.md),
  and a test that exposes a bug stays failing until the fix lands.
- The human approves golden images, MUST-level departures and open questions.

## Engineering rules (summary; the spec has the full list)

- C# .NET 10, Native AOT, warnings clean. Win32 through CsWin32 with `allowMarshaling: false`; hand-written native
  calls use `[LibraryImport]`, never `DllImport`.
- Energy: no timers or polling while idle, present only on change, no per-frame or per-message allocation on hot
  paths (display draws, presents, input sends, mouse motion, audio). Allocation tests guard this; keep them green.
- TLS trusts only the CA and host-subject from the `.vv` file. Never fall back to the system store or hostname checks.
- Codecs stay in the replaceable LGPL `spicecodec.dll`; vendored C under `native/spicecodec/vendor` is never reformatted.
- Licensing: an edit to a vendored LGPL file updates its dated `MODIFIED FILE` notice and `native/VENDORED.md`; a new
  third-party component (vendored, linked or compiled in) gets a row in [licenses/README.md](licenses/README.md) and
  its text in the `LICENSE.txt` that `tools/package.ps1` writes.

## Commands

```sh
bash native/build.sh x64      # native DLLs (MSYS2 clang64 or llvm-mingw)
dotnet build SpiceViewer.sln
dotnet test SpiceViewer.sln   # all test projects
pwsh tools/package.ps1        # AOT publish + release zip in artifacts/
```

## Secrets and private details

- `PVE_TOKEN_DONT_COMMIT.txt` holds the Proxmox API token. Only `python tools/pve-vv.py` reads it; never print, copy
  or commit it. `.vv` files contain one-time passwords and are ignored by git.
- Keep VM IDs, hostnames, IP addresses, account names and absolute local paths out of the repository. Name test
  guests by OS (`win7`, `win81`); keep the real mapping in your local notes.
- New replay fixtures must be scrubbed with `tools/spicerec/scrub.py` before they are added (see
  [tools/spicerec/README.md](tools/spicerec/README.md)). Golden images change only with the human's approval.

## Where to write things

| You did | Write it in |
|---|---|
| Made a choice the spec doesn't cover | a new `docs/decisions/NNNN-*.md` and its index row |
| Tested against a live guest | `docs/verification/YYYY-MM-DD-<topic>.md` and its index row |
| Reviewed code | `docs/reviews/` |
| Changed what users see or do | `tools/package/README.txt` (shipped in the zip) |
