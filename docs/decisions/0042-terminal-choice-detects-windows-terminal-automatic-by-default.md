# 0042. The terminal choice detects Windows Terminal and is "Automatic" by default

- Status: Accepted
- Date: 2026-10-31
- Decided by: the human (2026-10-31): "launcher/termproxy 主動偵測環境是否有安裝 Windows Terminal 或 Windows
  Terminal Preview 或 wt.exe，主動使用" (the launcher and the termproxy helper detect Windows Terminal, Windows
  Terminal Preview or any wt.exe, and use it). The human also asked (2026-10-31, on a machine with both installed)
  that the choice between stable and Preview follow the user's own Windows settings, not a fixed "stable first"
  rule, and that each flavour found be offered as its own choice. The details below are the agent's (spec silent).

- **Context:**
  - [0038](0038-v1-pve-launcher-winui3-unpackaged-self-contained-aot.md) gave the launcher three terminal choices:
    `default` (a new console), `wt` (`wt.exe -w 0 new-tab`, found by name) and `conhost`, with `default` as the
    default. `tools/pve-vv.py --term` always opened a new console.
  - [0036](0036-v1-terminal-bridge-to-the-users-terminal-first.md) bridges to the user's terminal, and
    [../design/principles.md](../design/principles.md) asks that users keep the terminal they already use.
  - Windows Terminal ships as two packages that can be installed side by side, stable and Preview. Each declares
    the App Execution Alias `wt.exe`; only one of them owns the generic alias at a time.

- **Decision:**
  - **What is checked.** `Pve.Api.WindowsTerminalLocator.Scan` and `tools/wt_locate.py` look, each time a terminal
    opens or the launcher's choice list opens (never on a timer), at:
    - `%LOCALAPPDATA%\Microsoft\WindowsApps\Microsoft.WindowsTerminal_8wekyb3d8bbwe\wt.exe` (stable) and
      `...\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\wt.exe` (Preview): each package's own alias;
    - `%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe`, the generic alias. Its reparse data
      (`FSCTL_GET_REPARSE_POINT`, tag `IO_REPARSE_TAG_APPEXECLINK`) names the package it runs;
    - `wt.exe` in a fully qualified PATH folder (a portable or scoop copy). Relative PATH entries are skipped, and
      the WindowsApps folder's alias isn't counted twice;
    - the default terminal application, `HKCU\Console\%%Startup` `DelegationTerminal`, read only. Its value is a
      class ID that each package declares in its manifest as `com.microsoft.windows.terminal.host`.

    No process is started and the package manager isn't queried. The aliases are zero-length reparse points;
    `File.Exists` (and Python's `os.path.lexists`) report them as files.
  - **Verified on the development machine (2026-10-31),** read only:
    - both packages installed: `Microsoft.WindowsTerminal_8wekyb3d8bbwe` 1.24.12741.0 and
      `Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe` 1.26.2734.0, both declaring the alias `wt.exe`;
    - both per-package alias folders exist, and the generic `wt.exe` alias's reparse data names the Preview
      package (version 3: family, `...!App`, the target exe under `Program Files\WindowsApps`, app type `0`);
    - the class IDs, from each installed package's `AppxManifest.xml`: stable terminal host
      `{E12CFF52-A866-4C77-9A90-F570A7AA2C6B}` (console host `{2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}`); Preview
      terminal host `{86633F1F-6454-40EC-89CE-DA4EBA977EE2}` (console host `{06EC847C-C0A5-46B8-92CB-7C92F6E35CD5}`);
    - `DelegationTerminal` and `DelegationConsole` are all zeros: "Let Windows decide", which names no package;
    - `wt.exe` is a GUI-subsystem program, so starting it opens no console window of its own.

    Canary is not handled: it wasn't installed, so its package family and class ID could not be checked. A Canary
    that owns the generic alias still opens, as "Windows Terminal (wt.exe alias)".
  - **Automatic** (`auto`, the new default) opens a tab in, in this order:
    1. the Windows Terminal set as the default terminal application, if its alias exists;
    2. the generic `wt.exe` alias: the package the user turned on under Settings, "App execution aliases";
    3. the stable package's alias, then Preview's, then `wt.exe` on PATH.

    Without any of them it opens a new console, as `default` does.
  - **Explicit choices.** The settings file stores `auto`, `wt` (Windows Terminal), `wt-preview` (Windows Terminal
    Preview), `default` and `conhost`.
    - `wt` is the stable package. Without it, it is the generic alias unless that runs Preview, or else a `wt.exe`
      on PATH: before this change `wt` ran `wt.exe` by name, and this keeps a portable copy working.
    - `wt-preview` is only Preview: its own alias, or the generic alias when that runs Preview.
    - An explicit choice that isn't found is an error in the status bar ("Windows Terminal Preview was not found
      ..."); it doesn't fall back to another terminal.
    - An existing `default` stays as it is, because the user chose it. Only a missing or unknown value becomes
      `auto`.
  - **Every Windows Terminal flavour behaves like 0038's `wt`:** `wt.exe -w 0 new-tab --title TITLE -- PveTerm.exe
    console --pipe NAME --nonce HEX`, started with the full path that was found. wt.exe exits at once, so its exit
    doesn't end the hand-off and no exit code is reported. Automatic resolves before the start, and then behaves
    exactly like the host it became, including `default`'s exit-code report.
  - **The launcher's list** shows "Automatic (Windows Terminal Preview)" or "Automatic (new console)", then
    "Windows Terminal" and "Windows Terminal Preview" (each only when found, or when it is the saved choice, then
    marked "not found"), "Default terminal app (new console)" and "Console host (conhost)". Tooltips give the path
    and why Automatic picked it. The log records the choice and what it resolved to.
  - **`tools/pve-vv.py --term`** resolves the same way (`tools/wt_locate.py`; `python tools/wt_locate.py` prints
    this machine's answer, `--self-test` checks the order). `--wt PATH` uses a given wt.exe, `--console` forces a
    new console, and `--here`, `--hidden` and `-- COMMAND` are unchanged.

- **Reason:**
  - The user's own settings come first. The default terminal application is the most explicit statement of which
    terminal they want; the alias toggle is the next one, and it is also what typing `wt` runs. A fixed "stable
    first" order would open stable for someone who set up Preview.
  - Neither setting is required: on "Let Windows decide" with no alias, any installed package is better than a
    console window the user didn't ask for.
  - Full paths, not `wt.exe` by name, so the tab opens in the terminal shown in the list, whatever PATH holds.
  - Reading the generic alias's reparse data costs one file open, and it lets the list name what Automatic opens.
    A package query would need WinRT and be slower; starting `wt.exe` to ask would open a window.
  - Costs:
    - the class IDs are fixed in code (verified above). A Windows Terminal release that changed them would only
      lose step 1, and the alias would still decide;
    - a launch with Windows Terminal reports no PveTerm exit code, as `wt` already didn't.
