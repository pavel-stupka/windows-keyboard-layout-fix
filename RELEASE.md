# KbFix 0.1.0

The first public release of **KbFix**, a small Windows utility that fixes the
long-standing Windows annoyance where Remote Desktop (and a few other
system-level events) silently inject extra keyboard layouts into your session
even though your language settings never changed.

## What's in the download

`kbfix-0.1.0-win-x64.zip` contains a single folder `kbfix-0.1.0-win-x64/`
with five files — unzip it anywhere and use it immediately:

| File            | What it does                                                                      |
|-----------------|-----------------------------------------------------------------------------------|
| `kbfix.exe`     | Self-contained ~11 MB Windows x64 binary. **No .NET runtime required.**           |
| `install.cmd`   | Double-click to install the background watcher.                                   |
| `uninstall.cmd` | Double-click to stop the watcher and remove everything.                           |
| `status.cmd`    | Double-click to see whether the watcher is running.                               |
| `README.txt`    | Quick start, troubleshooting, and the full exit-code reference for scripting.    |

## Quick start

1. Download `kbfix-0.1.0-win-x64.zip` below, unzip it, and drop the
   `kbfix-0.1.0-win-x64/` folder somewhere you own — `C:\Tools\KbFix\`, your
   Documents folder, anywhere.
2. Double-click **`install.cmd`**. A console window opens, KbFix stages itself
   under `%LOCALAPPDATA%\KbFix\`, registers per-user autostart via
   `HKCU\...\Run\KbFixWatcher`, and launches the background watcher. Press any
   key to close the window.
3. Forget about it. From now on, stray layouts injected by RDP disconnects,
   fast-user-switching, or misbehaving IMEs disappear within a couple of
   seconds — automatically, for the rest of the current session and at every
   subsequent Windows login.
4. Double-click **`status.cmd`** any time to see whether the watcher is healthy.
5. Double-click **`uninstall.cmd`** if you ever want to remove it. The watcher
   stops, autostart is unregistered, and the staging directory is cleaned up.
   No registry dust, no leftover files.

**No terminal needed. No Administrator rights ever. Everything is per-user
and fully reversible.**

## What it actually does

KbFix reads your persisted Windows language configuration (the layouts you
chose in **Settings → Time & language → Language & region**) and enumerates
the layouts currently loaded in your session. Any layouts in the session that
are **not** in your persisted configuration are unloaded via the documented
Win32 input-locale APIs. That's the entire job.

The background watcher does exactly the same thing, on a short polling
interval, for as long as you're logged in. If something re-injects a stray
layout, it's gone again within a few seconds.

## System requirements

- Windows 10 build 1809 or newer, or Windows 11
- x64
- A normal user account (no Administrator elevation required at any point)
- No .NET runtime on the target machine — `kbfix.exe` is self-contained

## Technical highlights

- **~11 MB** self-contained executable thanks to full IL trimming + size-tuning
  publish properties. Down from the ~80 MB baseline you'd get from a naïve
  `dotnet publish --self-contained` — a ~7× reduction.
- **Zero third-party NuGet dependencies.** Only `Microsoft.Win32.Registry` from
  the BCL is referenced.
- **COM interop survives `TrimMode=full`** — the TSF interfaces and all Win32
  P/Invoke code paths exercised by the tool are reachable from static code, so
  no `[DynamicDependency]` annotations were needed.
- **Per-session single-instance** via a named `Local\KbFixWatcher.Instance`
  mutex. On a multi-user host, each logged-in user gets their own independent
  watcher with no cross-session interference.
- **Idle backoff.** 2 s → 5 s → 10 s polling when nothing changes, snap back
  to 2 s on any activity.
- **Flap protection.** If something keeps re-injecting the same layout more
  than ten times in a minute, the watcher pauses for five minutes to avoid
  fighting a misbehaving injector.

## Advanced / troubleshooting

- Watcher log: `%LOCALAPPDATA%\KbFix\watcher.log` (size-bounded at 64 KB with
  one-deep rotation to `watcher.log.1`).
- Set `KBFIX_DEBUG=1` in your environment **before** running `install.cmd`
  to enable verbose per-poll-cycle logging in `watcher.log` for troubleshooting.
- The `--status` command uses distinct exit codes so scripts can react: `0`
  installed + healthy, `10` not installed, `11` installed but watcher not
  running, `12` watcher running without autostart, `13` autostart points at a
  stale path, `14` mixed/corrupt state. See `README.txt` or the project
  [README](https://github.com/pavel-stupka/windows-keyboard-layout-fix#exit-codes)
  for the full table.

## Verifying the download

MD5 checksums of the release archive and of every file shipped inside it:

```
b5fa62dccbd62cb1c95847ebcded758a  kbfix-0.1.0-win-x64.zip

189bb4a4855b804690bc166bfddf9d7d  kbfix.exe
5e2188ae3a95ec9e00842497609da6a9  install.cmd
27081996c34755e37623d88c6972ba0a  uninstall.cmd
162d919f4862d6e4b88e500aba6174f2  status.cmd
fe9657777a2f08fbac5aedfc0fbc039b  README.txt
```

File sizes:

```
kbfix-0.1.0-win-x64.zip   5,103,314 bytes
kbfix.exe                11,648,549 bytes
install.cmd                      88 bytes
uninstall.cmd                    90 bytes
status.cmd                       87 bytes
README.txt                    3,973 bytes
```

On Windows you can verify either the ZIP or an individual file with:

```powershell
Get-FileHash .\kbfix-0.1.0-win-x64.zip -Algorithm MD5
Get-FileHash .\kbfix.exe -Algorithm MD5
```

(If you prefer a stronger hash, `Get-FileHash` defaults to SHA-256 when the
`-Algorithm` flag is omitted.)

## Source and specs

Full source, feature specifications, and implementation plans live in the
repository under `src/`, `tests/`, and `specs/`. KbFix 0.1.0 covers three
feature specs:

- [`specs/001-fix-keyboard-layouts/`](https://github.com/pavel-stupka/windows-keyboard-layout-fix/tree/main/specs/001-fix-keyboard-layouts)
  — the one-shot fix and its CLI contract.
- [`specs/002-build-script/`](https://github.com/pavel-stupka/windows-keyboard-layout-fix/tree/main/specs/002-build-script)
  — the `build.cmd` release entry point.
- [`specs/003-background-watcher/`](https://github.com/pavel-stupka/windows-keyboard-layout-fix/tree/main/specs/003-background-watcher)
  — the background watcher, `--install` / `--uninstall` / `--status`
  commands, and the trimming work that produced the ~11 MB binary.

Built with [Claude Code](https://claude.com/claude-code) driven by the
[GitHub Spec Kit](https://github.com/github/spec-kit) workflow.
