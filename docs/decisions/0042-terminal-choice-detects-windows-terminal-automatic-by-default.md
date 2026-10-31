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

## Amendment (2026-10-31): mintty and a custom command

- Decided by: the human (2026-10-31, through agentdesk, recorded in 0038's answers): "mintty works with
  `MSYS=enable_pcon`; the launcher gets a mintty choice (setting that variable) and custom command templates."
  The details below are the agent's (spec silent), not yet reviewed.
- **Choices.** The settings file also stores `mintty` and `custom`. The list shows "mintty (MSYS2 (scoop))" and the
  like when one is found, else "mintty (not found)" (amended after review R3,
  [../reviews/2026-10-31-launcher-refresh-mintty.md](../reviews/2026-10-31-launcher-refresh-mintty.md): always
  listed, so choosing it reaches "mintty path…" for an install detection misses), and "Custom command (launcher.json)"
  when `terminalCommand` is set (or chosen). Automatic is unchanged: it never picks mintty.
- **Where mintty is looked for** (`Pve.Api.MinttyLocator`, file checks only, each time a terminal opens or the list
  opens): `mintty.exe` in a fully qualified PATH folder; MSYS2 in scoop (`%SCOOP%` or `%USERPROFILE%\scoop`, then
  `%SCOOP_GLOBAL%` or `%ProgramData%\scoop`, `apps\msys2\current\usr\bin`); `%SystemDrive%\msys64\usr\bin`; Git for
  Windows in scoop (`apps\git\current\usr\bin`), `%ProgramFiles%\Git\usr\bin`, `%LOCALAPPDATA%\Programs\Git\usr\bin`.
  The first found is used; its path is in the tooltip and the log. Cygwin's mintty is not looked for (its runtime
  reads `CYGWIN`, not `MSYS`). Verified on the development machine (2026-10-31, read only): scoop's MSYS2 and Git
  both have `usr\bin\mintty.exe`; MSYS2's is mintty 3.8.3, whose man page and `--help` text list `-t/--title`,
  `-h/--hold never|start|error|always` and `-e/--exec`.
- **`minttyPath`** (amended 2026-10-31; decided by the human through agentdesk D-9ef18eff723c, "提供彈性": a mintty
  path setting; the details are the agent's, spec silent, not yet reviewed). `launcher.json` may name the mintty to
  use; absent, empty or blank means detection as above (`Pve.Api.MinttyLocator.Find`):
  - set from the window with "mintty path…" next to the terminal list (shown while mintty is the choice or a path is
    set), or by editing the file. The value is kept as written and checked when it is used;
  - surrounding quotes and spaces are dropped and `%VARIABLE%`s expanded (an unknown one stays as written). The
    result must be a full path: to a file (any name, so a renamed or wrapped mintty, or wsltty's, works), or to a
    folder with `mintty.exe` in it. A relative path is refused rather than resolved against the launcher's folder,
    so a path never depends on where the launcher was started from;
  - a usable path comes first, before what detection finds (listed once); the terminal list shows it as
    "mintty (minttyPath)";
  - a path that can't be used doesn't block the launch: the reason is logged, detection takes over, and the status
    bar says which mintty opened and why ("minttyPath "…" was not found; looking for mintty as usual"). The list's
    tooltip shows the same reason. The dialog itself refuses a path that can't be used;
  - no file picker: the WinUI picker in an unpackaged app needs window interop that this change couldn't try on the
    desktop. Pasting a path covers it; a picker can follow.
- **mintty's command:** `mintty.exe --title TITLE --hold error --exec PveTerm.exe console --pipe NAME --nonce HEX`,
  started without ShellExecute so the environment can be set:
  - `MSYS` is the user's own value with ` enable_pcon` added last (the runtime reads its options in order, so this
    wins over a `disable_pcon` for this window only). Without it the MSYS runtime gives a native program pipes, not a
    console, and PveTerm can't run (the human's finding). mintty 3.x also has `--pcon on`; it isn't used, since an
    older mintty would refuse an option it doesn't know;
  - `MSYS2_ARG_CONV_EXCL=*` and `MSYS_NO_PATHCONV=1`, so the runtime doesn't rewrite an argument as a POSIX path;
  - `--hold error` keeps the window open when PveTerm exits with an error, so its message can be read;
  - mintty runs PveTerm, so mintty's exit before the hand-off ends it (as conhost's does); its exit code is mintty's,
    so none is reported.
- **Custom command** (`terminalCommand` in launcher.json, with `"terminal": "custom"`):
  - a list (program, then arguments), or one string split as the Microsoft C runtime splits a command line
    (`Pve.Api.TerminalCommand.Split`: white space separates, quotes group, `""` in quotes is a quote, backslashes
    count only before a quote). A string is written back as a string;
  - placeholders `{exe}` (PveTerm.exe's full path), `{pipe}`, `{nonce}` and `{title}`, as `tools/pve-vv.py --term
    ... -- COMMAND` (which now fills `{title}` too). They are filled in one pass, so a guest named `{nonce}` stays
    text. `{title}` loses control characters, `;` and `"`, as Windows Terminal's title does, so a command that runs
    wt.exe can't be split by it;
  - no shell: the first element is the program (found by CreateProcess's rules), the others are its arguments;
  - a command that is empty, or lacks `{pipe}` or `{nonce}`, is refused before the descriptor is requested;
  - the program may hand the command on and exit (as wt.exe does), so its exit doesn't end the hand-off; the
    30-second deadline does. No exit code is reported.
- Amended after review R4 ([../reviews/2026-10-31-launcher-answers.md](../reviews/2026-10-31-launcher-answers.md)):
  when a custom command's program is `wt.exe` or `wt`, `{exe}` is filled with the bridge path's `;` escaped as `\;`,
  as the built-in Windows Terminal choice does.
