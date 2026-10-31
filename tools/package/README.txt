SpiceViewer {VERSION} (portable, {RID})
=====================================

A native Windows viewer for Proxmox SPICE connection files (.vv).

Requirements: Windows 10 1809 or later (Windows 11 recommended) on an x64 CPU with AVX2
(x86-64-v3: Intel Haswell or AMD Excavator/Zen and newer). No .NET runtime needed.

Usage
-----
  SpiceViewer.exe path\to\file.vv     open a connection file
  SpiceViewer.exe --register          make this copy the handler for .vv files (current user only)
  SpiceViewer.exe --unregister        undo --register
  SpiceViewer.exe --install           optional: install for the current user (see below)
  SpiceViewer.exe --uninstall         remove that installation
  SpiceViewer.exe --debug file.vv     verbose log

Proxmox connection files expire about 30-60 seconds after download; open them right away.
If the file has "delete-this-file=1" it is deleted once read.

If the guest has turned its display off (screen blanking or power saving), the window says
"The guest's display is off" instead of showing a picture. Press a key that does nothing on its
own, such as Shift, to wake it. Mouse clicks are not sent until the picture is back, so they
can't land on something you can't see. Until the guest's display first appears, the window says
"waiting for the guest's display"; this shows briefly on every connect. If it stays, the guest
may be asleep: press a key.

A Linux guest whose display has been off and back on can report its whole video buffer as its
screen, which is a little taller than the screen itself. With the guest agent (spice-vdagent)
running, the viewer trims the picture to the real screen as soon as the guest draws anything
(moving over the dock, typing, the clock changing); until then a band of old content may show
along the bottom. viewer.log says where the shown area came from ("canvas: monitor area").
The window menu's Scaling > "Trim Linux guest padding" turns this off (it is on by default and
saved for all VMs): turning it off shows the whole reported screen at once; turning it back on
trims at the guest's next redraw. Turn it off if a Linux guest's bottom rows (a panel, say) go
missing until something there changes.

Settings and log
----------------
SpiceViewer is portable: it keeps its settings (settings.json) and log (viewer.log) in a "data"
folder next to SpiceViewer.exe, created on first run. If that folder can't be written (for
example under Program Files), it uses %APPDATA%\SpiceViewer for settings and
%LOCALAPPDATA%\SpiceViewer for the log instead. Passwords are never logged. Viewers running
at the same time share viewer.log; each line shows its viewer's process ID in [brackets].

The window's size, position, maximized/full-screen state and scaling are remembered per VM
(by proxy or host plus the .vv title; files without a title are not remembered). The other
window-menu settings apply to all VMs. Delete settings.json to start over.

Installing (optional)
---------------------
"SpiceViewer.exe --install" copies the program to %LOCALAPPDATA%\Programs\SpiceViewer,
copies this copy's settings to %APPDATA%\SpiceViewer (unless settings already exist there),
and makes the installed copy the handler for .vv files. No administrator rights are needed and
no Start-menu shortcut is created. The installed copy keeps its settings in
%APPDATA%\SpiceViewer and its log in %LOCALAPPDATA%\SpiceViewer.
"SpiceViewer.exe --uninstall" removes the .vv association and the installed program, and
leaves your settings and log in place.

Keys (defaults; a .vv file may override them)
---------------------------------------------
  Ctrl+Alt+R   release keyboard and mouse back to Windows (click the window to capture again)
  Shift+F11    toggle full screen
  Ctrl+Alt+Shift+F12   show or hide the status overlay (not set by .vv files; a .vv hotkey with
               the same keys wins)
  The window menu (right-click the title bar) has Send Ctrl+Alt+Del, Send Win+L,
  scaling (fit / 1:1), when to capture system shortcuts, resize guest to window,
  "Show guest cursor in server mouse mode", "Video pacing", "Fast JPEG decoding",
  "Image compression", "Video codec", "Show status overlay", and About.

Status overlay (window menu "Show status overlay" or Ctrl+Alt+Shift+F12; off by default, saved
  for all VMs): a few lines of figures in the top-left corner of the guest picture, on a dark
  see-through background. The guest picture itself, screenshots and recordings of it are not
  changed. It shows:
    the VM title and the time the figures were taken, and the span the rates cover;
    conn   connection state, TLS, whether a proxy is used (never its address or the password),
           channels, mouse mode (client or server), whether the guest agent is connected;
    disp   the guest's screen size, and the surface behind it when that is larger (with
           "inferred" when the screen was worked out from the guest's drawing), fit or 1:1 and
           the scale;
    pres   the presentation path (direct or letterbox) and buffer size, presents per second,
           the graphics adapter ("software" for WARP or Remote Desktop's renderer);
    net    bytes and messages per second received, and the display channel's share;
    video  each video stream: codec, how it is decoded (libjpeg-turbo, graphics card through
           Media Foundation with the decoder's name, or FFmpeg on the CPU), size, frames per
           second shown, frames shown and dropped; and the video pacing setting;
    audio  playing or idle, codec, sample rate and channels, the server's latency;
    mem    the viewer's private memory and .NET heap, and the last memory trim.
  It updates once a second while its figures are changing: while data arrives, a video stream
  runs or audio plays. It also updates at once when a stream starts or ends, the mouse mode or
  guest agent changes, or the window changes mode or how the picture is fitted. A resize that
  only stretches the picture stretches the overlay with it until the next update.
  To save power, it stops updating about a second after everything goes quiet, showing the
  final figures (zero rates). It doesn't update while the window is minimized or covered.
  When the guest draws something again, or a stream or sound starts, it resumes. The time on
  its first line says when the figures were taken.
  It shows over the guest picture, the "display off" message and the disconnected screen, but
  not before the first picture (the "Connecting" text).

Video pacing (window menu, saved for all VMs): how guest video streams are timed.
  Off (default)            every frame is drawn as soon as it arrives: the lowest latency
                           and the least work.
  Smooth bursts            frames keep their spacing, at most 50 ms of added delay.
  Follow guest clock       frames and sound wait for the guest's timestamps, so they stay
                           in sync; both then run about 0.4 s late, including sound with
                           no video. Switching to or from it may cut or pause the sound once.

Fast JPEG decoding (window menu, saved for all VMs, off by default): decodes JPEG images
  and video about 12% faster at slightly lower quality: colours are a little less accurate,
  most visibly on sharp coloured edges. Takes effect at once.

Image compression (window menu, saved for all VMs):
  Server default (default)    the server picks how screen images are compressed.
  LZ4 (fast, more bandwidth)  asks the server for LZ4: quicker to decode, but uses more
                              network bandwidth; best on a fast local network. Sent at once
                              if the server supports it; going back to "Server default"
                              takes effect from the next connection.

Video codec (window menu, saved for all VMs): which codec the server should use for video.
  Guest video arrives as MJPEG, decoded on the CPU, or as H.264, VP9 or HEVC. VP9 and HEVC are
  decoded by the graphics card; H.264 by the graphics card when it can, else on the CPU (see
  below). Each codec in the menu says how this PC decodes it; codecs it can't decode are grayed
  out. VP9 and HEVC usually need Microsoft's "VP9 Video Extensions" and "HEVC Video Extensions"
  from the Microsoft Store. About lists what this PC decodes and how the last video stream was
  decoded. Windows N editions without the Media Feature Pack decode MJPEG, and H.264 on the CPU.
  Automatic (default)  asks for H.264, then VP9, then HEVC, whichever this PC decodes: much
                       less bandwidth than MJPEG, but more CPU on the server.
                       Without any of them it asks for nothing.
  Server default       asks for nothing; the server then usually uses MJPEG.
  MJPEG                asks for MJPEG.
  H.264, VP9, HEVC     asks for that codec first. SPICE servers send HEVC only through
                       spice-streaming-agent, which this viewer doesn't use.
  Decode H.264 in software when the graphics card can't (on by default): an unmodified SPICE
  server (Proxmox's included) encodes H.264 in a full-colour (4:4:4) format that graphics cards
  can't decode. It is sharp (text and thin lines keep their colour), and the viewer decodes it
  on the CPU with FFmpeg's H.264 decoder, using up to four threads; H.264 the graphics card
  takes stays on the graphics card. A PC without any H.264 decoder of its own (no graphics
  card decoder, Remote Desktop's or the basic display renderer, Windows N without the Media
  Feature Pack) still offers H.264 and decodes all of it on the CPU. On this program's test machine 1080p 4:4:4 H.264 took about as much
  CPU per frame as MJPEG of the same picture. Turn it off to spend bandwidth (MJPEG) instead of
  CPU: new video streams follow at once, and the codecs offered to the server change from the
  next connection. With it off, the viewer sees 4:4:4 on the first stream and asks for MJPEG
  for the rest of the connection (viewer.log says so). The host's spice-server could encode
  4:2:0 instead with SPICE_CONVERTER_PREFERRED_FORMAT=I420 in the VM's QEMU environment, but
  only in spice-server releases newer than 0.15.2; Proxmox's 0.15.2 ignores it.
  A change is sent at once if the server supports it and applies to the next video stream;
  going back to "Server default" takes effect from the next connection. Video streams are sent
  only when video streaming is enabled in the VM's SPICE settings.


PVE launcher (PveLauncher\PveLauncher.exe)
------------------------------------------
Lists the VMs and containers of a Proxmox VE server and opens them, so you don't download a
.vv file each time. Run PveLauncher\PveLauncher.exe; it uses SpiceViewer.exe and PveTerm.exe
from the folder above it.

  Add          a server: its address (https://host:8006; 8006 is assumed if you give no port),
               and either a user name and realm (pam, pve, ...) for password sign-in, or an
               API token ID (user@realm!tokenname). The token needs the VM.Console privilege,
               and VM.Audit for "Open console" to tell SPICE from VNC (without it, SPICE).
  CA or server certificate
               optional, PEM. Either the PVE CA certificate (/etc/pve/pve-root-ca.pem on any
               node), to trust that CA and nothing else; the server's name must then match
               its certificate. Or the server's own certificate (for example
               /etc/pve/local/pve-ssl.pem on that node), to trust exactly that certificate;
               its name is then not checked, so an address it doesn't list (an IP address,
               a DNS alias, a proxy) works. A certificate that renews must be pasted again.
               Leave it empty if the server has a certificate Windows already trusts (for
               example from Let's Encrypt); the name must then match too.
  Sign in      the password or token secret is asked for when you first connect. Tick
               "Remember in Windows Credential Manager" to keep it there (never in a file).
               Accounts with two-factor authentication are asked for the authenticator
               app's code (TOTP) or a recovery key; WebAuthn and U2F keys are not supported.
               A session stays signed in while you use it; after about 1 h 40 min without
               use, the next action signs in again (with a remembered password, only the
               code is asked for).
  Open console double-click a VM, press Enter or click "Open console". A VM whose display is
               SPICE (qxl or virtio) opens in SpiceViewer; one with another display (std, the
               default, or cirrus, vmware) opens its VNC console (below); a serial display
               opens the terminal. The connection data goes through a pipe, never a file. A
               VM that is stopped shows the server's error. Right-click a VM to choose
               SPICE or VNC yourself.
  Start        a stopped VM or container shows "Start" (button or right-click), as the web
               UI's Start does. It needs the VM.PowerMgmt privilege; when the server says
               the account lacks it, Start is shown greyed out and says so. The launcher
               follows the start task for up to 2 minutes and shows its error if the
               start fails.
  VNC          "Open VNC console" opens a VM's VNC display in a VNC viewer you install
               yourself. PveTerm.exe (no window) signs in to the VM's VNC server and offers
               the session on a one-time port on 127.0.0.1; only a program running as you
               may connect, so no password is asked. Choose the viewer at the bottom right:
                 Automatic (the default): the first installed of TigerVNC, UltraVNC,
                   TightVNC 2.x and RealVNC Viewer 7, best first. The list shows which
                   one it will open.
                 TigerVNC (recommended, https://tigervnc.org): the only one of these that
                   sends keys as scancodes (QEMU's extended key events), so every key
                   works whatever the keyboard layout, as in SpiceViewer. The others send
                   characters, and some keys or layouts may not arrive as typed.
                 UltraVNC, TightVNC 2.x, RealVNC Viewer 7: work too. RealVNC Connect 8
                   does not (it needs a RealVNC account to reach other servers).
                 None: nothing starts; the message above the list gives the address
                   (127.0.0.1::PORT) to connect your viewer to within a minute.
                 Custom command: set "vncViewer": "custom" and "vncCommand" in
                   data\launcher.json, for example
                   "vncCommand": ["C:\\Tools\\vncviewer.exe", "{hostport}"]
                   ({host}, {port}, {hostport} and {title} are filled in).
               Other consoles of the VM stay open: the session is always shared. Proxmox
               ends a VNC session after 15 idle minutes; PveTerm keeps it alive with one
               harmless update request after 10 idle minutes.
  Terminal     "Open terminal" (or right-click) opens a VM's serial console, and a container's
               console, in your terminal through PveTerm.exe. Choose where at the bottom
               right:
                 Automatic (the default): a new tab in Windows Terminal when it is
                   installed, else a new console. With both Windows Terminal and Windows
                   Terminal Preview, it takes the one set as Windows' default terminal
                   app, else the one whose wt.exe alias is turned on (Settings > Apps >
                   Advanced app settings > App execution aliases), else Windows Terminal.
                   A wt.exe on PATH (a portable copy) counts too. The list shows which
                   one it will open.
                 Windows Terminal, Windows Terminal Preview: a tab in that one (listed
                   when installed).
                 mintty: a new mintty window (found as mintty.exe on PATH, in MSYS2 or in
                   Git for Windows, installed normally or with scoop). The launcher
                   sets MSYS=enable_pcon for it, which PveTerm needs to get a real console
                   in mintty. If PveTerm fails, the window stays open to show why.
                   To use another mintty, choose mintty and click "mintty path..." next
                   to the list: the full path of mintty.exe or of its folder
                   (%VARIABLES% work), saved as "minttyPath" in data\launcher.json.
                   Empty looks for it as above. A path that no longer exists is skipped
                   with a warning and the usual places are searched.
                 Default terminal app: a new console, which Windows 11 opens in your
                   default terminal app.
                 Console host (conhost): the classic console window.
                 Custom command: set "terminal": "custom" and "terminalCommand" in
                   data\launcher.json, as a list or as one command line (split like a
                   Windows program's own; no shell runs it), for example
                   "terminalCommand": ["C:\\Tools\\term.exe", "-e", "{exe}", "console",
                     "--pipe", "{pipe}", "--nonce", "{nonce}"]
                   ({exe} is PveTerm.exe, {title} the guest's name; {pipe} and {nonce} are
                   required).
               In the terminal, type ~. at the start of a line to leave (~? lists the
               escapes). A VM's serial console can't learn the window's size: at its
               shell prompt, ~r types "stty rows R cols C" and Enter with the current size.
  Refresh      the list updates by itself every 5 seconds while the launcher's window is
               open, minimized too. It pauses while the computer sleeps and updates right
               away when it wakes up; closing the window stops it. With a password sign-in
               this keeps the session alive while the window is open. After a Start it
               checks every 2 seconds
               until the guest is running (at most 30 seconds). F5 or the Refresh button
               fetches it at once. Set "refreshSeconds" in data\launcher.json to change
               the interval (2 to 3600), or to 0 to turn the automatic refresh off.
  Sign out     forgets the session in memory. "Forget saved..." deletes the remembered
               password or token secret of the selected server, or of all servers.

The server list (no passwords) is in data\launcher.json next to SpiceViewer.exe, and the log
in data\launcher.log, following the same rules as the viewer's settings. Errors are shown above
the list; an unexpected one (a bug) is also written to the log with its details. Remembered secrets
are in Windows Credential Manager under "SpiceViewer.PVE/...".

The launcher is built on Microsoft's Windows App SDK (WinUI 3), included in PveLauncher\ so
nothing needs installing. Those files are under Microsoft's license terms, not the MIT License;
see LICENSE.txt. The viewer and PveTerm.exe don't use them.

Guest tools
-----------
Install spice-guest-tools (https://www.spice-space.org/download.html) in Windows guests for
smooth client-mode mouse, clipboard sharing and resize-to-window. Without them the viewer uses
server mouse mode: the pointer is confined to the window and shows the guest's cursor (or is
hidden, with "Show guest cursor in server mouse mode" turned off in the window menu).

Security
--------
The server certificate is checked only against the CA and host-subject in the .vv file.
The system trust store and hostname checks are never used as a fallback.

Files
-----
  SpiceViewer.exe            the viewer (MIT)
  spicecodec.dll             SPICE QUIC/LZ/GLZ decoders and FFmpeg's H.264 decoder (libavcodec and
                             libavutil; LGPL-2.1-or-later), and the LZ4 decoder (BSD-2-Clause). You may
                             replace it with your own build; its full source and build scripts are in
                             source\native (MinGW or MSVC; see its README.md). FFmpeg's source is not included: fetch.sh downloads the
                             pinned release, and LICENSE.txt names its URL, SHA-256 and build flags.
  turbojpeg.dll              libjpeg-turbo (IJG, BSD-3-Clause and zlib)
  opus.dll                   libopus (BSD-3-Clause)
  PveTerm.exe                Proxmox text consoles in your terminal, and the bridge to your VNC
                             viewer; started by the launcher (MIT)
  PveLauncher\               the PVE launcher (PveLauncher.exe, MIT) and the Windows App SDK files
                             it needs (Microsoft's license terms)
  LICENSE.txt                licenses of this program and of every component in this package
  source\native\             complete source and build scripts of the native DLLs
  data\                      settings.json, launcher.json and the logs (created on first run)
