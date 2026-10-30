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
  The window menu (right-click the title bar) has Send Ctrl+Alt+Del, Send Win+L,
  scaling (fit / 1:1), when to capture system shortcuts, resize guest to window,
  "Show guest cursor in server mouse mode", "Video pacing", "Fast JPEG decoding",
  "Image compression", "Video codec", and About.

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
  Guest video arrives as MJPEG, decoded on the CPU, or as H.264, VP9 or HEVC, decoded by the
  graphics card. A codec is offered only if this PC's graphics card and Windows can decode it
  in hardware; the others are grayed out. VP9 and HEVC usually need Microsoft's "VP9 Video
  Extensions" and "HEVC Video Extensions" from the Microsoft Store. About lists what this PC
  decodes. Windows N editions without the Media Feature Pack use MJPEG only.
  Automatic (default)  asks for H.264, then VP9, then HEVC, whichever this PC decodes in
                       hardware: much less bandwidth than MJPEG, but more CPU on the server.
                       Without hardware decoding it asks for nothing.
  Server default       asks for nothing; the server then usually uses MJPEG.
  MJPEG                asks for MJPEG.
  H.264, VP9, HEVC     asks for that codec first. SPICE servers send HEVC only through
                       spice-streaming-agent, which this viewer doesn't use.
  An unmodified SPICE server (Proxmox's included) encodes H.264, and likely VP9, in a
  full-colour (4:4:4) format that the hardware decoders used here can't take. The viewer sees this
  on the first stream and
  asks for MJPEG for the rest of the connection (viewer.log says so). The host administrator can
  set SPICE_CONVERTER_PREFERRED_FORMAT=I420 in the VM's QEMU environment to get 4:2:0 instead.
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
               API token ID (user@realm!tokenname). The token needs the VM.Console privilege.
  CA           optional. Paste the PVE CA certificate (PEM, /etc/pve/pve-root-ca.pem on any
               node) to trust that CA and nothing else. Leave it empty if the server has a
               certificate Windows already trusts (for example from Let's Encrypt). The
               server's name must match its certificate either way.
  Sign in      the password or token secret is asked for when you first connect. Tick
               "Remember in Windows Credential Manager" to keep it there (never in a file).
               Accounts with two-factor authentication are asked for the authenticator
               app's code (TOTP) or a recovery key; WebAuthn and U2F keys are not supported.
               A session stays signed in while you use it; after about 1 h 40 min without
               use, the next action signs in again (with a remembered password, only the
               code is asked for).
  Open console double-click a VM, press Enter or click "Open console": its SPICE console opens
               in SpiceViewer. The connection data goes to the viewer through a pipe, never
               a file. A VM that is stopped or has no SPICE display shows the server's error.
  Terminal     "Open terminal" (or right-click) opens a VM's serial console, and a container's
               console, in your terminal through PveTerm.exe. Choose where at the bottom
               right: the default terminal app, a Windows Terminal tab, or the classic
               console window. In the terminal, type ~. at the start of a line to leave
               (~? lists the escapes).
  Refresh      F5 or the Refresh button. The list does not update by itself.
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
  spicecodec.dll             SPICE QUIC/LZ/GLZ decoders (LGPL-2.1-or-later) and the LZ4 decoder
                             (BSD-2-Clause). You may replace it with your own build; its full source and
                             build scripts are in source\native.
  turbojpeg.dll              libjpeg-turbo (IJG, BSD-3-Clause and zlib)
  opus.dll                   libopus (BSD-3-Clause)
  PveTerm.exe                Proxmox text consoles in your terminal, started by the launcher (MIT)
  PveLauncher\               the PVE launcher (PveLauncher.exe, MIT) and the Windows App SDK files
                             it needs (Microsoft's license terms)
  LICENSE.txt                licenses of this program and of every component in this package
  source\native\             complete source and build scripts of the native DLLs
  data\                      settings.json, launcher.json and the logs (created on first run)
