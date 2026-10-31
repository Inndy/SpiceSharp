# Agent guide

Instructions for coding agents working in this repository. Claude Code reads this through CLAUDE.md; Codex (CLI or
desktop) reads it directly. Read it in full before your first change, then follow it on every change.

## Sources of truth

1. [docs/design/spec.md](docs/design/spec.md): what to build. Its MUST rules, rejected alternatives and energy rules
   are binding. Its open questions belong to the human: ask, don't decide.
   [docs/design/principles.md](docs/design/principles.md) ranks the trade-offs (performance and energy, then memory,
   then binary size; aggressively opt in to new ISA and APIs above an x86-64-v3 floor; transparency and flexibility)
   and is binding the same way.
2. [docs/decisions/](docs/decisions/README.md): choices made where the spec is silent. Read the relevant records before
   changing behaviour they cover. Record every new choice there, in the same change, using the template. A record
   marked "agent (spec silent), not yet reviewed" stands until the human accepts or changes it.
3. The code. When code and a decision record disagree, the record wins until the human says otherwise.

## Roles

- **Claude** (Claude Code and its subagents) implements: production code, fixes, decision records, verification notes.
- **Codex** reviews and writes tests. It doesn't edit production code. Findings go to
  [docs/reviews/](docs/reviews/README.md); a test that exposes a bug is committed failing and stays failing until the
  fix lands. Either side may fix a test that is provably wrong, and says why in the review.
- **The human** approves golden images, MUST-level departures, spec changes, open questions, anything installed on
  the Proxmox host, and pushes to any remote other than the build box.
- **A coordinator** (usually the main Claude session) plans, splits work, delegates, integrates and commits to `main`.
  If you are taking over as coordinator, follow "Workflow" below; if you are a worker, stay inside your brief.

## Workflow

Every change goes through the same loop:

1. **Brief.** One task per worker, in its own git worktree (`.claude/worktrees/...` or any sibling worktree), with
   the scope, the files it owns, the rules below, and whether it owns the desktop or the test VMs.
2. **Implement** on the worktree's branch: WIP commits are fine. Write the decision record, the verification note
   and the README change in the same branch.
3. **Review.** Codex reviews the branch's commits (`git log main..HEAD`), writes
   `docs/reviews/YYYY-MM-DD-<topic>.md` with ranked findings (P1 security or data loss, P2 wrong behaviour, P3 minor),
   adds failing regression tests, and commits them on the same branch with its co-author line. Security-sensitive work
   (TLS, credentials, parsing untrusted bytes) always gets a review.
4. **Fix.** The implementer fixes every finding in production code, leaves Codex's tests unchanged unless provably
   wrong, and adds a "Response" section to the review. Repeat 3–4 if the fix is large.
5. **Live check** when behaviour depends on a real guest or the real desktop (see "Shared resources"), recorded in
   `docs/verification/`.
6. **Integrate** (coordinator):
   - squash-merge the branch into `main` as one commit per topic (`git merge --squash`), resolving index-table
     conflicts by keeping every row in order;
   - run the full build and every test project before committing;
   - tag the branch tip `archive/<topic>-wip` so the WIP history stays reachable, then delete the worktree and branch;
   - push `main` and the tag to the `vibebox` remote (the Linux build box). Other remotes need the human's ask;
   - rebuild `out/<rid>/` with `pwsh tools/package.ps1` when the human is testing builds.

Commits: LF line endings, independently revertible, a body that says what changed and why. End every message with
the trailers of everyone who took part in that change:

```text
Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Co-Authored-By: GPT 6.1 Sol <codex@openai.com>
```

The Codex trailer goes only on commits Codex took part in, and names the model that ran that round. Commit or push
only when the human asked for it or the workflow above covers it.

### Running Codex from Claude (herdr)

When Claude coordinates, Codex runs in a terminal multiplexer pane (herdr) or the Codex desktop app:

- Open a pane or tab in the worktree, start Codex there, and answer the hooks prompt with "Continue without trusting".
- Send one self-contained review prompt: the commits, the focus areas, where to write findings, which test projects
  it may run, and the rules (no production edits, no window-opening tests, no secrets, no VMs, the co-author line).
- Start a **babysitter** with every Codex task: a small Claude subagent that answers Codex's approval prompts. It
  approves once (`y`) builds, window-free tests and git commits on the current branch; it denies (`esc`) anything
  touching secrets, Proxmox, VMs, GUIs, git history, settings or main. It never chooses "don't ask again" (a
  persistent permission change), and re-reads the screen before every key because the human may answer first.
- Close Codex (and the pane you created) when it has no follow-up queued, so updated Codex builds get picked up.
- Use `/daybreak` (max gpt-6-sol) only for security audits; keep it off otherwise.

## Shared resources

- **Test VMs** (named by OS: `win7`, `win81`, `ubuntu`) and **the local desktop** (anything that opens a window:
  `SpiceViewer.exe`, `PveLauncher.exe`, terminals, `tests/Spice.Viewer.Tests`) are exclusive. At most one worker owns
  the VMs and at most one owns the desktop at a time; the brief says who. Everyone else runs window-free test projects
  only, in their own worktree.
- When the human is using the PC, never take the keyboard, mouse or screen. Headless tools (`tools/spice-cli`,
  `tools/spicerec`, loopback fakes) are fine. A headless SPICE connection kicks an open viewer off the same VM: ask
  first.
- Screenshots capture a window by handle, never the whole screen.
- Close every window you open; delete `.vv` files you create; leave guests as you found them and record anything you
  changed.
- Config changes and reboots are allowed on the Windows test guests; the Ubuntu guest is free to use inside the guest.
  Proxmox VM config changes need coordination; anything on the Proxmox host is the human's decision.

## Engineering rules (summary; the spec has the full list)

- C# .NET 10, Native AOT, warnings clean. Win32 through CsWin32 with `allowMarshaling: false`; hand-written native
  calls use `[LibraryImport]`, never `DllImport`.
- Energy: no timers or polling while idle, present only on change, no per-frame or per-message allocation on hot
  paths (display draws, presents, input sends, mouse motion, audio, video). Allocation tests guard this; keep them
  green.
- The SPICE connection trusts only the CA and host-subject from the `.vv` file, never the system store or hostname
  checks. The Proxmox API pins a CA when one is given, otherwise uses system trust (decision 0033).
- Codecs stay in the replaceable LGPL `spicecodec.dll`; all LGPL native code goes into that one library. Vendored C
  under `native/` is never reformatted.
- Licensing: an edit to a vendored LGPL file updates its dated `MODIFIED FILE` notice and `native/VENDORED.md`; a new
  third-party component (vendored, linked or compiled in) gets a row in [licenses/README.md](licenses/README.md) and
  its text in the `LICENSE.txt` that `tools/package.ps1` writes.
- Never guess protocol details: read the reference sources (spice-protocol, spice-gtk, spice-server, QEMU,
  pve-manager) and cite file and line in the decision record. Clone them as data; don't build or run them.
- Don't download and run external code. Native dependencies are pinned tarballs fetched by hash
  (`native/fetch.sh`); NuGet packages are pinned versions at least two weeks old.
- Flaky tests: plain reruns and reading shared state. Never spawn CPU busy-loops to provoke them. Our hot paths must
  not allocate; the .NET runtime's own allocations are not ours to chase.

## Commands

```sh
bash native/build.sh x64      # native DLLs (MSYS2 clang64 or llvm-mingw) -> native/out/win-x64
dotnet build SpiceViewer.sln
dotnet test SpiceViewer.sln   # all test projects; Spice.Viewer.Tests opens windows (needs the desktop)
pwsh tools/package.ps1        # AOT publish: release zip in artifacts/, the same files ready to run in out/win-x64/
pwsh tools/package.ps1 -Rid win-arm64
```

- Window-free test projects: `Spice.Native.Tests`, `Spice.Wire.Tests`, `Spice.Replay.Tests`, `Spice.Video.Tests`,
  `Spice.Diagnostics.Tests`, `Pve.Api.Tests`, `Pve.Term.Tests`. Run them one project at a time with `dotnet test
  tests/<Project>`.
- `native/out/` and `native/downloads/` are git-ignored. A new worktree needs them copied from the main checkout (or
  rebuilt) before the native, replay and video tests pass.
- `out/<rid>/` is the portable layout (viewer, `PveTerm.exe`, `PveLauncher\`) with a `data\` folder that survives
  rebuilds; intermediates are in `out/publish/<rid>/`.
- The Linux build box is the `vibebox` git remote (llvm-mingw cross builds, including ARM64); see
  [native/README.md](native/README.md).

### Live testing against Proxmox

- `python tools/pve-vv.py <vmid> [file.vv]` writes a fresh `.vv`; `--list`, `--term`, `--term-probe` and
  friends are described in its `--help` and in [src/PveTerm/README.md](src/PveTerm/README.md).
- The VM ID mapping, guest accounts and host names live in each agent's local notes, never in the repository.
- The viewer's log is `data\viewer.log` next to the exe (portable) or under `%LOCALAPPDATA%\SpiceViewer\`
  (installed). Read it before guessing.

## Secrets and private details

- `PVE_TOKEN_DONT_COMMIT.txt` holds the Proxmox API token. Only `python tools/pve-vv.py` reads it; never read,
  print, copy or commit it. `.vv` files contain one-time passwords and are ignored by git. Never log passwords,
  tickets or tokens.
- Keep VM IDs, hostnames, IP addresses, account names and absolute local paths out of the repository. Name test
  guests by OS; keep the real mapping in your local notes.
- New replay fixtures must be scrubbed with `tools/spicerec/scrub.py` before they are added (see
  [tools/spicerec/README.md](tools/spicerec/README.md)). Golden images change only with the human's approval.

## Where to write things

| You did | Write it in |
|---|---|
| Made a choice the spec doesn't cover | a new `docs/decisions/NNNN-*.md` and its index row |
| Got a decision from the human | the record's "Decided by" line, quoting them, with the date |
| Tested against a live guest | `docs/verification/YYYY-MM-DD-<topic>.md` and its index row |
| Reviewed code | `docs/reviews/YYYY-MM-DD-<topic>.md` and its index row; the fixer adds "Response" |
| Changed what users see or do | `tools/package/README.txt` (shipped in the zip) |
| Added a third-party component | `licenses/README.md`, `tools/package.ps1` LICENSE.txt text, `native/VENDORED.md` if vendored |

Decision numbers are assigned by the coordinator when several workers run at once; if two branches pick the same
number, the later one is renumbered at integration.
