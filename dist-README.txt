KbFix - Windows keyboard layout cleaner and watcher
====================================================

A small Windows utility that removes keyboard layouts from the current
Windows session that are not part of your persisted (HKCU) keyboard
configuration. It targets the long-standing Windows annoyance where
Remote Desktop and a few other system-level events silently inject
extra layouts into your session even though your language settings
themselves did not change.


What is in this folder
----------------------

  kbfix.exe        The main executable. Self-contained (~12 MB), no
                   .NET runtime required on the target machine.
  install.cmd      Double-click to install the background watcher.
  uninstall.cmd    Double-click to remove the background watcher.
  status.cmd       Double-click to see whether the watcher is running.
  README.txt       This file.


Quick start (recommended)
-------------------------

1. Double-click install.cmd.

   KbFix stages itself under %LOCALAPPDATA%\KbFix\, registers
   per-user autostart via HKCU\...\Run\KbFixWatcher, and launches
   the background watcher immediately. A console window opens so
   you can read the confirmation; press any key to close it.

2. Forget about it.

   The watcher runs in the background, re-applies the layout fix
   within a couple of seconds whenever Windows injects a stray
   layout (typically after an RDP disconnect or a fast user
   switch), and starts automatically at your next Windows login.

3. Double-click status.cmd any time to see the current state.

4. Double-click uninstall.cmd to remove everything.

   The watcher stops, autostart is unregistered, and the
   %LOCALAPPDATA%\KbFix\ staging directory is cleaned up. No
   registry dust, no leftover files.

Everything happens in the current user's context. No Administrator
rights are ever required.


One-shot use (without installing)
---------------------------------

You can also run kbfix.exe directly from a terminal without installing
anything. This performs a single cleanup pass and exits:

  kbfix.exe              run the fix once
  kbfix.exe --dry-run    preview what would change without touching anything
  kbfix.exe --help       full usage
  kbfix.exe --version    print the version


Requirements
------------

  - Windows 10 (build 1809 or newer) or Windows 11.
  - A normal user account. No elevation required.


Troubleshooting
---------------

  - The watcher writes a small rolling log to
    %LOCALAPPDATA%\KbFix\watcher.log. By default only meaningful
    events are logged (start, stop, applied fix, errors, refusals,
    flap backoff). Set the KBFIX_DEBUG=1 environment variable
    before installing if you want per-cycle DEBUG output during
    troubleshooting.

  - Run status.cmd (or kbfix.exe --status) to see whether the
    watcher is healthy. Different exit codes distinguish the
    possible states so scripts can react automatically.

  - If something gets stuck in a weird state, uninstall.cmd is
    always safe to run and always leaves the system in the
    "nothing installed" state. Re-running it is harmless.

  - If you update kbfix.exe to a newer version, just run the new
    install.cmd from wherever you put the new build. It detects
    the existing install, stops the old watcher, overwrites the
    staged copy, and launches a new watcher, all in one step.


Exit codes (reference)
----------------------

  0    success, no-op, or --status: installed and healthy
  1    generic failure
  2    unsupported platform (not a Windows 10/11 host)
  3    refused (persisted layout set is empty)
  10   --status: not installed
  11   --status: installed but watcher not running
  12   --status: watcher running without autostart
  13   --status: autostart points at a stale path
  14   --status: mixed or corrupt state
  64   usage error (unknown flag)


More information
----------------

Project:    https://github.com/pavel-stupka/windows-keyboard-layout-fix
