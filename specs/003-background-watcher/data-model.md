# Phase 1 Data Model: Background Watcher, Autostart, and Slim Binary

**Feature**: 003-background-watcher
**Date**: 2026-04-13
**Depends on**: [research.md](./research.md)

This feature is behavioral more than data-rich, but there are several entities with real identity and state transitions that the implementation, the CLI contracts, and the test suite need to share a vocabulary for. They are listed here.

Existing domain entities (`SessionState`, `PersistedConfig`, `LayoutSet`, `LayoutId`, `ReconciliationPlan`, `AppliedAction`, `Outcome`) are unchanged and reused as-is.

---

## 1. `WatcherInstallation`

Represents the observable installation state for the **current user**.

### Fields

| Field | Type | Notes |
|---|---|---|
| `StagedBinaryPath` | `string?` (absolute path) | `%LOCALAPPDATA%\KbFix\kbfix.exe` if present, else `null`. |
| `StagedBinaryExists` | `bool` | True iff `StagedBinaryPath` is non-null and the file exists. |
| `AutostartEntryPresent` | `bool` | True iff `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` has value `KbFixWatcher`. |
| `AutostartEntryTarget` | `string?` | The value data (binary path + args), or `null` if absent. |
| `AutostartEntryPointsAtStaged` | `bool` | True iff `AutostartEntryTarget` quotes-match `"{StagedBinaryPath}" --watch`. |
| `WatcherRunning` | `bool` | True iff the named mutex `Local\KbFixWatcher.Instance` is currently held by *some* process in this session. |
| `WatcherPid` | `int?` | PID read from `%LOCALAPPDATA%\KbFix\watcher.pid`. Decorative only; may be stale if `WatcherRunning == false`. |

### Derived classification

The `(AutostartEntryPresent, WatcherRunning, AutostartEntryPointsAtStaged)` tuple is collapsed into a single enum for CLI reporting and exit codes:

```
InstalledState:
  NotInstalled            // autostart absent, watcher not running
  InstalledHealthy        // autostart present & points at staged path, watcher running
  InstalledNotRunning     // autostart present, watcher not running
  RunningWithoutAutostart // watcher running, autostart absent
  StalePath               // autostart present but points at a non-existent or non-staged path
  MixedOrCorrupt          // any other combination (e.g. running + stale autostart)
```

### State transitions (user-visible)

```
NotInstalled ──[--install]──► InstalledHealthy
InstalledHealthy ──[--uninstall]──► NotInstalled
InstalledNotRunning ──[--install]──► InstalledHealthy   (reuses existing autostart, starts a new watcher)
RunningWithoutAutostart ──[--install]──► InstalledHealthy  (registers autostart; does not restart the running watcher)
StalePath ──[--install]──► InstalledHealthy  (re-stages the current binary, overwrites the Run key, starts watcher)
any ──[--uninstall]──► NotInstalled  (best-effort — removes whatever it finds)
```

### Invariants

- `--install` always terminates in `InstalledHealthy` (or errors out cleanly; see CLI contracts).
- `--uninstall` always terminates in `NotInstalled`.
- `--status` never mutates any field; it is a pure read.

---

## 2. `WatcherRuntime`

The in-process state of a watcher while it is running. Not persisted across restarts.

### Fields

| Field | Type | Notes |
|---|---|---|
| `InstanceMutex` | `Mutex` (owned) | `Local\KbFixWatcher.Instance`, acquired at startup; released on shutdown. |
| `StopEvent` | `EventWaitHandle` | `Local\KbFixWatcher.StopEvent`, manual-reset, checked between poll cycles. |
| `Pid` | `int` | Current process ID; written to the PID file. |
| `LogWriter` | `IWatcherLog` | Writes timestamped events to `watcher.log`. |
| `PollInterval` | `TimeSpan` | Current sleep duration between reconciliation cycles. Starts at 2 s; see R1 idle backoff. |
| `ConsecutiveNoOps` | `int` | Counter used to stretch `PollInterval` during idle. |
| `FlapDetector` | `FlapDetector` | Sliding-window counter of non-no-op reconciliations; triggers backoff pause. |
| `Reconciler` | `ISessionReconciler` | Facade over `PersistedConfigReader` + `SessionLayoutGateway` + `ReconciliationPlan`. |

### State transitions

```
Init ──[mutex acquired]──► Running ──[StopEvent signaled]──► ShuttingDown ──► Exited
Init ──[mutex already held]──► Exited (exit code 0, log "already running")
Running ──[FlapDetector ceiling hit]──► Paused(5 min) ──► Running
Running ──[config unreadable > grace period]──► Exited (exit code Failure)
```

### Invariants

- At most one `WatcherRuntime` exists per session at any time, enforced by the mutex.
- `StopEvent` may be signaled by any process in the session; the watcher's reaction time is at most one `PollInterval`.
- The PID file MUST exist while the mutex is held and be deleted on clean shutdown; `--install` / `--status` code MUST treat the PID file as advisory only.

---

## 3. `InstallDecision`

Pure function-style entity used by `--install`, `--uninstall`, and `--status` to decide what side effects to perform, given a `WatcherInstallation` snapshot.

### Inputs

- `currentInstallation : WatcherInstallation` (freshly probed)
- `invokingBinaryPath : string` (absolute path to the `kbfix.exe` that is running this command)
- `invokingBinaryIsStaged : bool` (true iff `invokingBinaryPath == currentInstallation.StagedBinaryPath`)
- `command : {Install, Uninstall, Status}`

### Outputs

An ordered list of atomic steps, drawn from:

```
Steps:
  EnsureStagingDirectory
  CopyBinaryToStaged(sourcePath)
  WriteRunKey(targetPath)
  DeleteRunKey
  SignalStopEvent(timeoutMs)
  ForceKillWatcher(pid)
  SpawnWatcher(stagedPath)
  DeleteStagedBinary         // may skip with note if file is in use
  DeleteStagingDirectory     // best-effort
  ReportStatus               // status command only
```

### Example decisions

| Command | Input state | Step sequence |
|---|---|---|
| `--install` | `NotInstalled` | `EnsureStagingDirectory`, `CopyBinaryToStaged(invoking)`, `WriteRunKey(staged)`, `SpawnWatcher(staged)` |
| `--install` | `InstalledHealthy` (and invoking == staged) | (none — report "already installed") |
| `--install` | `InstalledHealthy` (and invoking ≠ staged; newer version) | `SignalStopEvent(3000)`, `ForceKillWatcher?`, `CopyBinaryToStaged(invoking)` (overwrite), `WriteRunKey(staged)` (overwrite), `SpawnWatcher(staged)` |
| `--install` | `StalePath` | Same as `NotInstalled` but skipping `EnsureStagingDirectory` if it already exists. |
| `--uninstall` | `InstalledHealthy` | `SignalStopEvent(3000)`, (if still running) `ForceKillWatcher(pid)`, `DeleteRunKey`, (if staged ≠ invoking) `DeleteStagedBinary`, `DeleteStagingDirectory` |
| `--uninstall` | `NotInstalled` | (none — report "nothing to do", exit 0) |
| `--status` | any | `ReportStatus` |

### Rationale for modeling as a pure decision

Lets the whole install/uninstall/status branching logic be unit-tested without touching the registry, the filesystem, or processes. The concrete executor that applies each `Step` lives in `Platform/Install/` and is exercised only by manual verification.

---

## 4. `FlapDetector`

Sliding-window counter used by the watcher to avoid fighting a misbehaving IME.

### Fields

| Field | Type | Notes |
|---|---|---|
| `Window` | `TimeSpan` | 60 seconds. |
| `Threshold` | `int` | 10 non-no-op reconciliations per window. |
| `PauseDuration` | `TimeSpan` | 5 minutes. |
| `Events` | `Queue<DateTimeOffset>` | Timestamps of recent non-no-op reconciliations, oldest first. |
| `PausedUntil` | `DateTimeOffset?` | If non-null and in the future, the watcher loop sleeps instead of reconciling. |

### Invariants

- `Events` never exceeds `Threshold` entries; oldest entries are dropped when they fall outside `Window`.
- Pure function over an injected `IClock` — fully unit-testable.

---

## 5. Filesystem layout (per user)

```
%LOCALAPPDATA%\
└── KbFix\
    ├── kbfix.exe          (staged binary)
    ├── watcher.pid        (advisory; PID of the currently-running watcher, if any)
    ├── watcher.log        (current rolling log, ≤64 KB)
    └── watcher.log.1      (one-deep rotation, written when watcher.log is rotated)
```

- All files live in `%LOCALAPPDATA%` (not `%APPDATA%`) because the binary is machine-local, not roaming.
- Directory is created on first `--install`. Deleted (best-effort) on `--uninstall`.

---

## 6. Registry layout (per user)

```
HKEY_CURRENT_USER
└── Software
    └── Microsoft
        └── Windows
            └── CurrentVersion
                └── Run
                    └── KbFixWatcher = "C:\Users\<user>\AppData\Local\KbFix\kbfix.exe" --watch
```

- Value name: `KbFixWatcher` (fixed; used as the probe key for `AutostartEntryPresent`).
- Value type: `REG_SZ`.
- Value data: the quoted staged binary path followed by ` --watch`.
- Nothing else is written to the registry by this feature. Reads of `HKCU\Keyboard Layout\Preload` and `HKCU\Control Panel\International\User Profile` already happen via existing code (`PersistedConfigReader`); they are read-only and unchanged.

---

## 7. Named kernel objects (per session)

```
Local\KbFixWatcher.Instance    // Mutex, held while watcher is running
Local\KbFixWatcher.StopEvent   // Manual-reset event, signaled by --uninstall
```

- `Local\` scope means each Windows session on a multi-user host has its own independent pair.
- Both are owned by the watcher process. Other processes only open them by name.

---

## 8. Relationships summary

```
WatcherInstallation  ──probed by──►  --install / --uninstall / --status
                     ──input to──►   InstallDecision  ──emits──►  ordered list of Steps
                                                              ──executed by──►  Platform/Install/*

WatcherRuntime       ──mutex, event, pid file──►  discovered by WatcherInstallation.probe()
                     ──owns──►   FlapDetector
                     ──owns──►   ISessionReconciler  ──delegates to──►  existing reconciliation pipeline

existing pipeline:
  PersistedConfigReader.ReadRaw
  → SessionLayoutGateway.ResolvePersisted
  → SessionLayoutGateway.ReadSession
  → ReconciliationPlan.Build
  → SessionLayoutGateway.Apply
  → SessionLayoutGateway.VerifyConverged
```

No changes are required to the existing domain model to support this feature. All new types sit above it.
