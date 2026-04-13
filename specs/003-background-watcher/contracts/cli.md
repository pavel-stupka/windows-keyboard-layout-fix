# CLI Contract — Background Watcher, Install, Uninstall, Status

**Feature**: 003-background-watcher
**Artifact**: `kbfix.exe` (same executable as prior features; new subcommand flags)
**Supersedes**: adds to `specs/001-fix-keyboard-layouts/contracts/cli.md`; existing flags there remain unchanged.

This document is the authoritative CLI contract for the new subcommands. Existing behavior (`kbfix`, `kbfix --dry-run`, `kbfix --quiet`, `kbfix --help`, `kbfix --version`) is unchanged and continues to follow the 001 contract.

## Synopsis (additions)

```text
kbfix --install [--quiet]
kbfix --uninstall [--quiet]
kbfix --status [--quiet]
kbfix --watch                 (internal — spawned by --install; not user-facing)
```

Existing flags remain:

```text
kbfix [--dry-run] [--quiet] [--help] [--version]
```

## Mutual exclusion

- The four subcommand flags (`--install`, `--uninstall`, `--status`, `--watch`) are **mutually exclusive** with each other AND with `--dry-run`. Combining any two is a usage error (exit 64).
- `--quiet` MAY be combined with any of the subcommands. It suppresses the verbose informational sections (see each subcommand below).
- `--help` and `--version` still short-circuit all other flags (they print and exit 0), matching the 001 contract.

Unknown flags MUST continue to cause exit code 64 (`EX_USAGE`) with an error on stderr, as defined in 001.

---

## `--install`

### Purpose

Register the watcher to auto-start at the current user's next Windows login, stage the binary to a stable per-user location, and launch the watcher immediately in the current session.

### Preconditions

- Running on Windows (satisfies `SupportedOSPlatformVersion`).
- Current user can write to `%LOCALAPPDATA%` (essentially always true for an interactive user).
- Current user can write to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- No administrator elevation required.

### Stdout format (default verbosity)

```text
KbFix installer
  staged:   C:\Users\<user>\AppData\Local\KbFix\kbfix.exe
  autostart: HKCU\Run\KbFixWatcher = "C:\Users\<user>\AppData\Local\KbFix\kbfix.exe" --watch
  watcher:   started (pid 12345)

Installed. The watcher will also start automatically at your next Windows login.
```

If the watcher was already running and `--install` reused it (e.g. second run with no version change):

```text
KbFix installer
  staged:   C:\Users\<user>\AppData\Local\KbFix\kbfix.exe  (unchanged)
  autostart: HKCU\Run\KbFixWatcher  (unchanged)
  watcher:   already running (pid 12345)

Already installed.
```

If the invoking binary is a *newer* copy than the staged one, `--install` stops the running watcher, replaces the staged binary, rewrites the Run key, and launches a new watcher:

```text
KbFix installer
  staged:   C:\Users\<user>\AppData\Local\KbFix\kbfix.exe  (replaced)
  autostart: HKCU\Run\KbFixWatcher  (updated)
  watcher:   restarted (pid 13579)

Installed.
```

In `--quiet`, the three indented `staged:` / `autostart:` / `watcher:` lines are suppressed. Only the final `Installed.` / `Already installed.` line is printed.

### Side effects

1. Ensure directory `%LOCALAPPDATA%\KbFix\` exists.
2. If invoking binary path != staged path: copy invoking binary over staged path (overwrite).
3. If the Run key value is missing or does not match `"<staged>" --watch`: write it.
4. If no watcher currently holds `Local\KbFixWatcher.Instance`: launch `<staged> --watch` detached (no console window, no inherited streams).
5. If a watcher is running and the staged binary was replaced: signal `Local\KbFixWatcher.StopEvent`, wait up to 3 s for the mutex to release, force-kill the PID from `watcher.pid` as a fallback, then launch a new watcher.

### Exit codes

| Code | Condition | Constant (existing) |
|---|---|---|
| 0 | Installed or already installed, everything healthy. | `Success` |
| 1 | Partial failure — e.g. staged copy succeeded but Run key write failed. The stdout report still lists what was done; stderr explains the failure. | `Failure` |
| 2 | Platform unsupported (e.g. running on a non-Windows host reached by testing scaffolding). | `Unsupported` |
| 64 | Usage error (e.g. `--install --uninstall` together, or extra unknown flags). | `Usage` |

---

## `--uninstall`

### Purpose

Stop any running watcher belonging to the current user, remove the autostart entry, and delete the staged binary + staging directory (best-effort). Idempotent — safe to run when nothing is installed.

### Stdout format (default verbosity)

Fully installed → clean uninstall:

```text
KbFix uninstaller
  watcher:   stopped (pid was 12345, cooperative shutdown)
  autostart: HKCU\Run\KbFixWatcher  (removed)
  staged:   C:\Users\<user>\AppData\Local\KbFix\kbfix.exe  (deleted)

Uninstalled.
```

Nothing installed → no-op:

```text
KbFix uninstaller
  watcher:   not running
  autostart: not registered
  staged:   not present

Nothing to uninstall.
```

Partial / mixed state (e.g. watcher running but no autostart; or stale Run key pointing at a missing file):

```text
KbFix uninstaller
  watcher:   stopped (pid was 12345, forced after timeout)
  autostart: HKCU\Run\KbFixWatcher  (was stale: pointed at C:\old\path\kbfix.exe; removed)
  staged:   not present

Uninstalled.
```

In `--quiet`, the indented lines are suppressed; only `Uninstalled.` or `Nothing to uninstall.` is printed.

### Side effects (in order)

1. If a watcher is running: open `Local\KbFixWatcher.StopEvent`, `Set()` it. Wait up to 3 seconds for `Local\KbFixWatcher.Instance` to become unowned.
2. If still owned after 3 seconds AND `watcher.pid` file exists: `Process.Kill` that PID (only if the process's module path matches the staged path, as a safety check).
3. If `HKCU\Run\KbFixWatcher` exists: delete the value.
4. If `%LOCALAPPDATA%\KbFix\kbfix.exe` exists and is not the currently-invoking binary: delete it.
5. If `%LOCALAPPDATA%\KbFix\` is now empty (aside from the invoking binary case): delete the directory.
6. Delete `watcher.pid` and the log files if present.
7. Special case — `--uninstall` invoked FROM the staged binary: the currently-running process cannot delete its own executable on Windows. In that case, step 4 is skipped with a note `staged: deletion skipped (currently in use — will be cleaned up on next run from a different location)`. The Run key and watcher are still removed, so autostart will not re-launch it.

### Exit codes

| Code | Condition |
|---|---|
| 0 | Uninstalled successfully, or nothing was installed (both are success for idempotency). |
| 1 | Partial failure — at least one step failed and some state remains. stdout enumerates what succeeded; stderr explains the failure. |
| 2 | Platform unsupported. |
| 64 | Usage error. |

---

## `--status`

### Purpose

Report the installed state for the current user. Read-only — no mutation.

### Stdout format (default verbosity)

Fully installed and healthy:

```text
KbFix status
  watcher:   running (pid 12345)
  autostart: registered  ("C:\Users\<user>\AppData\Local\KbFix\kbfix.exe" --watch)
  staged:   present      (C:\Users\<user>\AppData\Local\KbFix\kbfix.exe)
  log:       C:\Users\<user>\AppData\Local\KbFix\watcher.log

State: InstalledHealthy
```

Not installed:

```text
KbFix status
  watcher:   not running
  autostart: not registered
  staged:   not present

State: NotInstalled
```

Installed but watcher not running (e.g. crashed):

```text
KbFix status
  watcher:   not running
  autostart: registered  ("C:\Users\<user>\AppData\Local\KbFix\kbfix.exe" --watch)
  staged:   present      (C:\Users\<user>\AppData\Local\KbFix\kbfix.exe)
  log:       C:\Users\<user>\AppData\Local\KbFix\watcher.log

State: InstalledNotRunning
```

Watcher running without autostart (user started `--watch` by hand):

```text
KbFix status
  watcher:   running (pid 12345)
  autostart: not registered
  staged:   present      (C:\Users\<user>\AppData\Local\KbFix\kbfix.exe)
  log:       C:\Users\<user>\AppData\Local\KbFix\watcher.log

State: RunningWithoutAutostart
```

Stale autostart (Run key present, points at a path that does not exist or is not the staged binary):

```text
KbFix status
  watcher:   not running
  autostart: registered  ("C:\old\path\kbfix.exe" --watch)  STALE
  staged:   not present

State: StalePath
```

In `--quiet`, the indented lines are suppressed; only the final `State: <name>` line is printed.

### Exit codes

`--status` uses distinct exit codes so scripts can react to the observed state without parsing stdout.

| Code | State | Meaning |
|---|---|---|
| 0 | `InstalledHealthy` | Everything is in order. |
| 1 | (unused here — reserved for actual errors, same as 001) | Genuine failure probing state. |
| 2 | `Unsupported` | Platform unsupported. |
| 10 | `NotInstalled` | Nothing is installed. |
| 11 | `InstalledNotRunning` | Autostart registered but watcher is not running right now. |
| 12 | `RunningWithoutAutostart` | Watcher running, but will not come back after login. |
| 13 | `StalePath` | Autostart present but points at a bad path. |
| 14 | `MixedOrCorrupt` | Any other combination. |
| 64 | — | Usage error. |

The new exit codes 10–14 are additions to the `ExitCodes` constants defined in `src/KbFix/Diagnostics/ExitCodes.cs`. The existing codes (0, 1, 2, 3, 64) retain their 001 contract meanings.

---

## `--watch` (internal)

### Purpose

Enters the watcher main loop. Not a user-facing command — it is spawned by `--install`. It is documented here for completeness and so `--status` and `--uninstall` can reason about what the Run key points at.

### Behavior

- At startup: acquire `Local\KbFixWatcher.Instance` mutex. If already held, log `already running` to `watcher.log` and exit 0 quietly.
- Defensively call `FreeConsole()` to detach from any inherited console handle.
- Open (or create) `Local\KbFixWatcher.StopEvent`.
- Write `watcher.pid`.
- Enter the reconciliation loop (see research R1, data-model §2): read persisted config, read session, build plan, apply, verify, sleep up to `PollInterval` on the stop event, repeat.
- On `StopEvent` signal: exit the loop, delete `watcher.pid`, release the mutex, exit 0.
- On unrecoverable error (config unreadable beyond grace period, repeated TSF interop failure, etc.): log, release mutex, exit `Failure`.

### Stdout / stderr

None in normal operation — the process is detached from any console. All output goes to `watcher.log`.

### Exit codes

| Code | Condition |
|---|---|
| 0 | Normal shutdown via stop event, or "already running" short-circuit. |
| 1 | Unrecoverable failure. |
| 2 | Platform unsupported. |

---

## Summary of new behaviors vs. 001 contract

- Four new flags added: `--install`, `--uninstall`, `--status`, `--watch`.
- New exit codes 10–14 reserved for `--status` state reporting. Codes 0, 1, 2, 3, 64 keep their 001 meanings.
- `--quiet` semantics extended: for the new subcommands, it suppresses the indented informational lines and preserves only the terminal result line.
- `--help` output is updated to mention the new flags. Old users running `kbfix --help` see the new commands listed.
- Existing invocations (`kbfix`, `kbfix --dry-run`, etc.) are byte-for-byte unchanged.
