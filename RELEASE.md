# KbFix 0.1.1

A reliability and observability release of **KbFix**, the small Windows
utility that fixes the long-standing Windows annoyance where Remote
Desktop (and a few other system-level events) silently inject extra
keyboard layouts into your session even though your language settings
never changed.

**0.1.1 is a drop-in upgrade from 0.1.0.** If you are already running
0.1.0, just download the new ZIP, extract over the old copy, and run
`install.cmd` — it detects the existing install, adds the new autostart
layer, and keeps your running watcher alive through the upgrade.

## What's new since 0.1.0

- **Two-layer autostart.** The existing `HKCU\...\Run\KbFixWatcher`
  entry is preserved; a per-user Scheduled Task
  `\KbFix\KbFixWatcher` (At logon + Restart on failure) is added
  alongside it. If the Run key is ever suppressed — by the "Startup
  apps" toggle in Task Manager, by Group Policy, or by a third-party
  startup manager — the Scheduled Task brings the watcher up at next
  sign-in. Still per-user, still no elevation.
- **Automatic self-restart.** If the watcher process is ever killed
  (Task Manager "End task", antivirus, OS-forced stop), the Scheduled
  Task's Restart-on-failure setting brings a fresh watcher back within
  ~90 seconds, without any user action.
- **Extended `--status` output.** New lines answer the three diagnostic
  questions explicitly: *is the watcher running?*, *will it be running
  after my next sign-in?*, and *why did it stop last time?*. The status
  output now shows the scheduled-task state, the supervisor state
  (healthy / restart pending / gave up / disabled), the parsed
  last-exit record (reason + timestamp + pid), and an
  "effective at next logon" verdict derived from both autostart
  mechanisms plus the Startup-Apps toggle.
- **`--status --verbose` bug-report snapshot.** A new modifier that
  bundles the status output, the last ~40 lines of `watcher.log`,
  the scheduled-task XML, and the pretty-printed `last-exit.json`
  into a single paste-ready block.
- **Three new exit codes** on `--status`: `15` supervisor backing off
  (restart pending), `16` supervisor gave up (re-run `--install` to
  re-arm), `17` autostart mechanisms registered but all disabled at
  next logon. The existing 001/003 codes (0/1/2/3/10/11/12/13/14/64)
  are unchanged.
- **`last-exit.json`.** The watcher now persists the most-recent exit
  reason to `%LOCALAPPDATA%\KbFix\last-exit.json` — cooperative shutdown,
  config-unreadable, or unhandled exception (via an
  `AppDomain.UnhandledException` handler that runs before the runtime
  terminates the process). The next watcher reads it at startup and
  logs the previous-exit reason on its first log line.

None of the 0.1.0 behaviour changes. The one-shot `kbfix` command, the
`install.cmd` / `uninstall.cmd` / `status.cmd` wrappers, the no-admin
guarantee, the clean-uninstall guarantee, and the
~11 MB single-file binary are all preserved.

## What's in the download

`kbfix-0.1.1-win-x64.zip` contains a single folder `kbfix-0.1.1-win-x64/`
with five files — unzip it anywhere and use it immediately:

| File            | What it does                                                                            |
|-----------------|-----------------------------------------------------------------------------------------|
| `kbfix.exe`     | Self-contained ~11.5 MB Windows x64 binary. **No .NET runtime required.**               |
| `install.cmd`   | Double-click to install the background watcher (Run key + Scheduled Task).              |
| `uninstall.cmd` | Double-click to stop the watcher and tear down both autostart mechanisms.                |
| `status.cmd`    | Double-click to see watcher / task / supervisor state and the previous-run exit reason. |
| `README.txt`    | Quick start, troubleshooting, and the full exit-code reference for scripting.           |

## Quick start

1. Download `kbfix-0.1.1-win-x64.zip` below, unzip it, and drop the
   `kbfix-0.1.1-win-x64/` folder somewhere you own — `C:\Tools\KbFix\`,
   your Documents folder, anywhere.
2. Double-click **`install.cmd`**. A console window opens, KbFix stages
   itself under `%LOCALAPPDATA%\KbFix\`, registers both autostart
   mechanisms (the `HKCU\Run\KbFixWatcher` key and the
   `\KbFix\KbFixWatcher` Scheduled Task), and launches the background
   watcher. Press any key to close the window.
3. Forget about it. From now on, stray layouts injected by RDP
   disconnects, fast-user-switching, or misbehaving IMEs disappear
   within a couple of seconds — automatically, for the rest of the
   current session and at every subsequent Windows login. If the
   watcher is ever killed, a fresh one is back within ~90 seconds.
4. Double-click **`status.cmd`** any time to see whether the watcher
   is healthy, whether autostart will fire at next logon, and why the
   watcher stopped last time (if it did).
5. Double-click **`uninstall.cmd`** if you ever want to remove it.
   The watcher stops, both autostart mechanisms are unregistered, and
   the staging directory is cleaned up. No registry dust, no leftover
   files, no scheduled-task residue.

**No terminal needed. No Administrator rights ever. Everything is per-user
and fully reversible.**

## What it actually does

KbFix reads your persisted Windows language configuration (the layouts
you chose in **Settings → Time & language → Language & region**) and
enumerates the layouts currently loaded in your session. Any layouts
in the session that are **not** in your persisted configuration are
unloaded via the documented Win32 input-locale APIs. That's the entire
job.

The background watcher does exactly the same thing, on a short polling
interval, for as long as you're logged in. If something re-injects a
stray layout, it's gone again within a few seconds.

0.1.1 adds a per-user **Scheduled Task** whose only purpose is to make
sure the watcher is running — at every logon (At-logon trigger) and
within a bounded time after any crash or external termination
(Restart-on-failure: every 1 minute, up to 3 times per logon session).
The task runs as the interactive user with `LeastPrivilege`;
no elevation is ever requested.

## System requirements

- Windows 10 build 1809 or newer, or Windows 11
- x64
- A normal user account (no Administrator elevation required at any point)
- No .NET runtime on the target machine — `kbfix.exe` is self-contained
- `schtasks.exe` available on `PATH` (ships in every supported Windows
  version; the install falls back gracefully to Run-key-only if a
  locked-down environment blocks user-namespace Scheduled Tasks)

## Technical highlights

- **~11.5 MB** self-contained executable thanks to full IL trimming +
  size-tuning publish properties. Up by ~430 KB from 0.1.0; still well
  under the 20 MB spec target and far below the ~80 MB naïve baseline.
- **Zero third-party NuGet dependencies.** Only `Microsoft.Win32.Registry`
  from the BCL is referenced, same as 0.1.0.
- **COM interop survives `TrimMode=full`** — the TSF interfaces and all
  Win32 P/Invoke code paths exercised by the tool are reachable from
  static code; `System.Text.Json` source-gen keeps the new
  `last-exit.json` serialisation trim-safe.
- **Supervision delegated to the OS.** The Scheduled Task's built-in
  `RestartOnFailure` setting is the entire retry/give-up state machine
  — no in-app backoff counters, no second resident process, no cache
  that could drift from reality.
- **Per-session single-instance** via a named `Local\KbFixWatcher.Instance`
  mutex (unchanged from 0.1.0). On a multi-user host, each logged-in
  user gets their own independent watcher with no cross-session
  interference; the Scheduled Task is per-user by construction
  (`<Principal><UserId>` carries the interactive user's SID).
- **Idle backoff** unchanged: 2 s → 5 s → 10 s polling when nothing
  changes, snap back to 2 s on any activity.
- **Flap protection** unchanged: if something keeps re-injecting the
  same layout more than ten times in a minute, the watcher pauses for
  five minutes to avoid fighting a misbehaving injector.
- **`AppDomain.UnhandledException` handler** writes
  `last-exit.json` before the runtime terminates a crashing watcher,
  so the next run — brought back by the Scheduled Task — can log
  *why* the previous run died.

## Advanced / troubleshooting

- Watcher log: `%LOCALAPPDATA%\KbFix\watcher.log` (size-bounded at
  64 KB with one-deep rotation to `watcher.log.1`). New in 0.1.1: a
  `process-startup previous-exit=<reason>` line at the top of every
  run, plus `supervisor-observed-dead` when a previous run was killed
  externally.
- Last-exit record: `%LOCALAPPDATA%\KbFix\last-exit.json` (~200 bytes,
  overwritten on every controllable exit). Contains the reason code
  (`CooperativeShutdown`, `ConfigUnrecoverable`, `CrashedUnhandled`,
  `StartupFailed`, or `SupervisorObservedDead`), the exit code, an
  ISO-8601 UTC timestamp, the pid, and a short free-text detail.
- Scheduled-task definition archived at
  `%LOCALAPPDATA%\KbFix\scheduled-task.xml` for auditability.
- Set `KBFIX_DEBUG=1` in your environment **before** running
  `install.cmd` to enable verbose per-poll-cycle logging in
  `watcher.log` for troubleshooting.
- `kbfix.exe --status --verbose` bundles the status output, the tail of
  `watcher.log`, the scheduled-task XML, and the `last-exit.json` into
  one paste-ready block — copy it into a bug report and you have
  everything a maintainer needs.
- The `--status` command uses distinct exit codes so scripts can react:
  `0` installed + healthy, `10` not installed, `11` installed but
  watcher not running, `12` watcher running without autostart, `13`
  autostart points at a stale path, `14` mixed/corrupt state, `15`
  supervisor backing off, `16` supervisor gave up, `17` autostart
  mechanisms registered but all disabled. See `README.txt` or the
  project
  [README](https://github.com/pavel-stupka/windows-keyboard-layout-fix#exit-codes)
  for the full table.

## Verifying the download

MD5 checksums of the release archive and of every file shipped inside it:

```
b6a18aa6b82fbfcd255da7097b95e38b  kbfix-0.1.1-win-x64.zip

90ef0de60bf778c3e7f77b13a9e967e0  kbfix.exe
5e2188ae3a95ec9e00842497609da6a9  install.cmd
27081996c34755e37623d88c6972ba0a  uninstall.cmd
162d919f4862d6e4b88e500aba6174f2  status.cmd
9b84d6f8f847b395e4ebbb118c82fa6f  README.txt
```

File sizes:

```
kbfix-0.1.1-win-x64.zip   5,423,355 bytes
kbfix.exe                12,079,795 bytes
install.cmd                      88 bytes
uninstall.cmd                    90 bytes
status.cmd                       87 bytes
README.txt                    4,952 bytes
```

On Windows you can verify either the ZIP or an individual file with:

```powershell
Get-FileHash .\kbfix-0.1.1-win-x64.zip -Algorithm MD5
Get-FileHash .\kbfix.exe -Algorithm MD5
```

(If you prefer a stronger hash, `Get-FileHash` defaults to SHA-256 when
the `-Algorithm` flag is omitted.)

## Source and specs

Full source, feature specifications, and implementation plans live in
the repository under `src/`, `tests/`, and `specs/`. KbFix 0.1.1 covers
four feature specs:

- [`specs/001-fix-keyboard-layouts/`](https://github.com/pavel-stupka/windows-keyboard-layout-fix/tree/main/specs/001-fix-keyboard-layouts)
  — the one-shot fix and its CLI contract.
- [`specs/002-build-script/`](https://github.com/pavel-stupka/windows-keyboard-layout-fix/tree/main/specs/002-build-script)
  — the `build.cmd` release entry point.
- [`specs/003-background-watcher/`](https://github.com/pavel-stupka/windows-keyboard-layout-fix/tree/main/specs/003-background-watcher)
  — the background watcher, `--install` / `--uninstall` / `--status`
  commands, and the trimming work that produced the ~11 MB binary.
- [`specs/004-watcher-resilience/`](https://github.com/pavel-stupka/windows-keyboard-layout-fix/tree/main/specs/004-watcher-resilience)
  — layered autostart via the per-user Scheduled Task, automatic
  self-restart after crashes, the extended `--status` output,
  `--status --verbose`, and exit codes 15–17. New in 0.1.1.

Built with [Claude Code](https://claude.com/claude-code) driven by the
[GitHub Spec Kit](https://github.com/github/spec-kit) workflow. Features
001–003 were produced by Claude Opus 4.6 (1M context); feature 004 by
Claude Opus 4.7 (1M context).
