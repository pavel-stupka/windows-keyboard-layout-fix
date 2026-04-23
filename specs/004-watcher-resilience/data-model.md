# Data Model — Watcher Resilience, Observability, and Self-Healing

This document enumerates the entities, fields, relationships, and state
transitions introduced or extended by feature 004. The storage surfaces
backing each entity are documented inline.

All existing entities from feature 003 (`WatcherInstallation`, `InstalledState`,
`FlapDetector` window, `WatcherLoop` outcome) are referenced but not redefined.

---

## 1. `LastExitReason` — NEW

A single-record, per-user JSON document recording why the most recent
watcher process exited. Written by the watcher just before termination
(on every controllable exit path); read by `--status` and by the next
watcher at startup.

### Fields

| Field          | Type              | Notes |
|----------------|-------------------|-------|
| `reason`       | enum string       | One of `CooperativeShutdown`, `ConfigUnrecoverable`, `CrashedUnhandled`, `StartupFailed`, `SupervisorObservedDead`. |
| `exitCode`     | int32             | The process exit code. 0 for cooperative; non-zero for any failure. Must match the observable process exit code to the extent possible. |
| `timestampUtc` | ISO-8601 UTC string| Written at the moment of exit-path entry. Format: `yyyy-MM-ddTHH:mm:ssZ`, same format as `WatcherLog`. |
| `pid`          | int32             | The process identifier of the watcher that just exited. Used by the next startup to distinguish "the previous pid still lives" (rare race) from "the previous pid is absent" (normal). |
| `detail`       | string \| null    | Optional short free-text (≤ 200 chars). Used for `CrashedUnhandled` (short exception type + message), `StartupFailed` (which step failed). Never contains a stack trace or PII. |

### Validation rules

- `reason` MUST be one of the five enum values.
- `exitCode` MUST be 0 if and only if `reason` is `CooperativeShutdown`.
- `timestampUtc` MUST be a valid ISO-8601 UTC string; older than Unix
  epoch rejected.
- `detail` is optional; MUST NOT exceed 200 bytes UTF-8.
- If any field fails validation, the consumer treats the whole record
  as absent (as if the file did not exist). The producer never writes
  an invalid record.

### State transitions (writer side)

```
  (watcher alive) --[StopSignaled via --uninstall/Ctrl+C]--> write CooperativeShutdown
  (watcher alive) --[ConfigReadFailed > 60s]------------> write ConfigUnrecoverable
  (watcher alive) --[UnhandledException in main thread]--> write CrashedUnhandled
  (watcher startup) --[mutex/log/dir init fails]---------> write StartupFailed
  (watcher alive) --[TerminateProcess from outside]------> NO WRITE (cannot intercept)
```

The `SupervisorObservedDead` reason is **not** written by the dying
watcher — it is written by the **next** watcher at startup, when the
previous `last-exit.json` record indicates a live PID but that PID no
longer exists.

### Storage

- Path: `%LOCALAPPDATA%\KbFix\last-exit.json`
- Format: UTF-8 JSON, newline-terminated, ~200 bytes.
- Overwritten (not appended) on every write.
- `--uninstall` deletes this file as part of tearing down the staging
  directory.

---

## 2. `ScheduledTaskEntry` — NEW

A snapshot of the current state of the per-user Scheduled Task at
`\KbFix\KbFixWatcher`, as observed by `schtasks.exe /Query`.

### Fields

| Field           | Type          | Source (schtasks output) |
|-----------------|---------------|--------------------------|
| `Present`       | bool          | `/Query /TN "..."` exit 0 vs. "cannot find the file" |
| `Enabled`       | bool          | `/Query /XML` → `<Settings>/<Enabled>` |
| `Status`        | enum string   | `/Query /V /FO LIST` → `Status:` field. One of `Running`, `Ready`, `Disabled`, `Unknown`. |
| `LastRunTime`   | DateTimeOffset? | `/Query /V /FO LIST` → `Last Run Time:` (parsed); null if never run |
| `LastResult`    | int?          | `/Query /V /FO LIST` → `Last Result:` (parsed) |
| `NextRunTime`   | DateTimeOffset? | `/Query /V /FO LIST` → `Next Run Time:`; null if `N/A` |
| `Principal`     | string        | `/Query /XML` → `<Principal>/<UserId>`; SID string |
| `ExecutablePath`| string        | `/Query /XML` → `<Actions>/<Exec>/<Command>`; full path to the staged binary |
| `PointsAtStaged`| bool          | Derived: `ExecutablePath == WatcherInstallation.DefaultStagedBinaryPath` (case-insensitive full-path) |

### Validation rules

- If `Present` is false, all other fields are null / default.
- If `Present` is true, `Principal` MUST equal the current user's SID
  (sanity check — if not, the task was misinstalled and must be
  recreated).
- `ExecutablePath` MUST be a full path rooted at
  `%LOCALAPPDATA%\KbFix\` for the task to count as "our" task.

### Storage

- Task Scheduler namespace: `\KbFix\KbFixWatcher`
- Definition XML archived at `%LOCALAPPDATA%\KbFix\scheduled-task.xml`
  for auditability and for idempotent re-creation.
- Both are removed by `--uninstall`.

### State transitions (install action side)

```
  Absent -----------[--install]---> Present, Enabled, PointsAtStaged
  Present, stale path ---[--install]---> Present, Enabled, PointsAtStaged (re-created with new XML)
  Present, Disabled --[--install]---> Present, Enabled (re-enabled)
  Present -------[--uninstall]---> Absent
  Absent --------[--uninstall]---> Absent (no-op)
```

---

## 3. `SupervisorState` — NEW enum

A classification of the supervisor's (Task Scheduler's) current
behaviour, derived from `ScheduledTaskEntry` and the live watcher
process state.

### Values

| Value            | Meaning |
|------------------|---------|
| `Healthy`        | Watcher running OR task Status is Ready with a Next Run Time in the future. |
| `RestartPending` | Watcher not alive; task Status is Ready; Next Run Time is set (within the restart window). |
| `GaveUp`         | Watcher not alive; task Status is Ready; Next Run Time is `N/A` (Restart-on-failure budget exhausted). |
| `Disabled`       | Task Status is Disabled (by user or policy). |
| `Absent`         | Task is not present at all. |
| `Unknown`        | schtasks query failed for reasons other than task-absent (e.g. policy blocking query). |

### Derivation rule

```
  alive = WatcherInstallation.WatcherRunning
  task  = ScheduledTaskEntry

  if not task.Present         -> Absent
  elif task.Status == Disabled -> Disabled
  elif alive                   -> Healthy
  elif task.NextRunTime != null-> RestartPending
  else                         -> GaveUp
```

`Unknown` is only produced when the probe itself throws — it is a
diagnostic state, not a normal one.

### Exit-code mapping (for `--status`)

| SupervisorState   | Exit code | Meaning (user-facing) |
|-------------------|-----------|-----------------------|
| `Healthy`         | 0         | All good |
| `RestartPending`  | 15 (new)  | Watcher down but restart scheduled; recover passively |
| `GaveUp`          | 16 (new)  | Re-arm with another `--install` |
| `Disabled`        | 17 (new) or 12 | If Run key also gone, use 17; else fall back to 12 (RunningWithoutAutostart) |
| `Absent`          | inherits feature-003 codes | No new code |
| `Unknown`         | 14 (MixedOrCorrupt) | Reuses existing code |

---

## 4. `AutostartEffectiveness` — NEW enum

Answers the question "will the watcher come back next time I sign in?"
without requiring a reboot. Distinct from the feature-003 `InstalledState`
because it considers the Startup-Apps-toggle override, which 003 did
not model.

### Values

| Value          | Meaning |
|----------------|---------|
| `Effective`    | At least one autostart mechanism (Run key or Scheduled Task) is registered AND will fire at next logon. |
| `Degraded`     | Autostart entries exist but ALL are disabled (e.g. Startup Apps toggle off + Scheduled Task disabled). |
| `NotRegistered`| Neither mechanism is registered. |

### Derivation rule

```
  runEnabled  = RunKeyPresent && StartupApprovedProbe.Enabled
  taskEnabled = ScheduledTaskEntry.Present && ScheduledTaskEntry.Enabled

  if runEnabled || taskEnabled       -> Effective
  elif RunKeyPresent || ScheduledTaskEntry.Present -> Degraded
  else                               -> NotRegistered
```

`StartupApprovedProbe` is the new `Platform/Install/StartupApprovedProbe.cs`
reader described in plan.md; it returns `Enabled` when the Run key is
absent OR when the StartupApproved binary value is absent OR when
byte 0 is `0x02`.

---

## 5. `WatcherInstallation` — EXTENDED

The feature-003 snapshot record gains four new fields. All existing
fields retain their semantics.

### New fields

| Field                   | Type                | Source |
|-------------------------|---------------------|--------|
| `ScheduledTask`         | `ScheduledTaskEntry`| `ScheduledTaskRegistry.Query()` |
| `AutostartEffectiveness`| enum                | `SupervisorDecision.ClassifyAutostart(this)` |
| `SupervisorState`       | enum                | `SupervisorDecision.ClassifySupervisor(this)` |
| `LastExitReason`        | `LastExitReason?`   | `LastExitReasonStore.Read()` |

### Existing `Classify()` extension

The existing `Classify()` method (003 → `InstalledState`) is unchanged
in semantics but now consults the new fields for the new states:

- New state value `InstalledState.SupervisorBackingOff` (maps to exit 15).
- New state value `InstalledState.SupervisorGaveUp` (maps to exit 16).
- New state value `InstalledState.AutostartDegraded` (maps to exit 17).

The feature-003 states (`InstalledHealthy`, `InstalledNotRunning`,
`NotInstalled`, `StalePath`, `RunningWithoutAutostart`, `MixedOrCorrupt`)
are preserved byte-for-byte; existing exit codes (10–14) are
unchanged.

### Classification priority

When multiple conditions could apply, the classifier picks the **most
specific / most actionable** state:

```
  StalePath     (13) > AutostartDegraded  (17) >
  SupervisorGaveUp (16) > SupervisorBackingOff (15) >
  InstalledHealthy (0) > InstalledNotRunning (11) >
  RunningWithoutAutostart (12) > MixedOrCorrupt (14) > NotInstalled (10)
```

Rationale: a stale path is always the actionable thing to tell the
user about first; giveup is more urgent than backing-off; healthy
supersedes any "not yet" state.

---

## 6. `InstalledState` — EXTENDED enum

Adds three values to the existing 003 enum. Existing values are
preserved at their existing integer ordinals to preserve exit-code
parity for any consumer that pinned against them.

```csharp
internal enum InstalledState
{
    // --- 003 values, unchanged ---
    NotInstalled,
    InstalledHealthy,
    InstalledNotRunning,
    RunningWithoutAutostart,
    StalePath,
    MixedOrCorrupt,

    // --- 004 additions ---
    SupervisorBackingOff,
    SupervisorGaveUp,
    AutostartDegraded,
}
```

---

## 7. `WatcherExitReason` — EXTENDED enum

The existing feature-003 enum (`StopSignaled`, `ConfigUnrecoverable`)
gains entries that describe the new "write before die" paths. Values
match the `LastExitReason.reason` JSON string verbatim.

```csharp
internal enum WatcherExitReason
{
    // --- 003 values, unchanged ---
    StopSignaled,        // cooperative (WatcherLoop return)
    ConfigUnrecoverable, // 60s grace exhausted (WatcherLoop return)

    // --- 004 additions ---
    CrashedUnhandled,    // AppDomain.UnhandledException observed; process terminating
    StartupFailed,       // WatcherMain.Run never reached the loop
    CooperativeShutdown, // Alias of StopSignaled with explicit intent; used for the JSON reason string
    SupervisorObservedDead, // Next startup observed previous run absent without a clean record
}
```

`CooperativeShutdown` and `StopSignaled` refer to the same physical
exit path (stop-event set → loop returns `StopSignaled` → `WatcherMain`
writes the JSON with reason `CooperativeShutdown`). The split exists
because the WatcherLoop's enum is internal and the JSON string is an
external contract; aligning them reduces consumer-side branching.

---

## 8. Filesystem & registry surface summary

All writes made by feature 004 beyond what feature 003 already does.
`--uninstall` removes each of these completely.

### New writes (feature 004)

| Path | When written | Owner |
|------|--------------|-------|
| `%LOCALAPPDATA%\KbFix\last-exit.json` | On controllable watcher exit paths | Watcher |
| `%LOCALAPPDATA%\KbFix\scheduled-task.xml` | `--install` | Installer |
| Task Scheduler `\KbFix\KbFixWatcher` | `--install` | Installer (via schtasks.exe) |

### Preserved writes (feature 003, unchanged)

| Path | Notes |
|------|-------|
| `%LOCALAPPDATA%\KbFix\kbfix.exe` | Staged binary |
| `%LOCALAPPDATA%\KbFix\watcher.log` | Rolling log |
| `%LOCALAPPDATA%\KbFix\watcher.log.1` | Rotated log |
| `%LOCALAPPDATA%\KbFix\watcher.pid` | Advisory PID |
| `HKCU\...\Run\KbFixWatcher` | Run-key autostart |
| `Local\KbFixWatcher.Instance` | Named mutex (kernel object) |
| `Local\KbFixWatcher.StopEvent` | Named event (kernel object) |

### New *reads* (feature 004; never written)

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run\KbFixWatcher` (binary value) — Startup-Apps-toggle probe.

No changes to the feature-001 or -003 read surface.

---

## 9. Deletion semantics for `--uninstall`

In order:

1. Signal stop event (cooperative shutdown) — unchanged from 003.
2. Force-kill if still running after 3 s — unchanged from 003.
3. Delete `HKCU\Run\KbFixWatcher` — unchanged from 003.
4. **Delete Scheduled Task `\KbFix\KbFixWatcher`** — NEW; idempotent.
5. Delete `%LOCALAPPDATA%\KbFix\scheduled-task.xml` — NEW; best effort.
6. Delete `%LOCALAPPDATA%\KbFix\last-exit.json` — NEW; best effort.
7. Delete staged binary (with 003's self-uninstall rename-for-reboot-delete fallback) — unchanged.
8. Delete staging directory if empty — unchanged.

After `--uninstall`, nothing on the machine will ever resurrect the
watcher. The user is back to square zero.
