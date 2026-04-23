# CLI Contract — Watcher Resilience (Delta vs. 003)

**Feature**: 004-watcher-resilience
**Artifact**: `kbfix.exe` (same executable as 001/002/003)
**Supersedes**: additive to `specs/003-background-watcher/contracts/cli.md`. Everything in the 003 contract still holds; this document describes only the changes.

---

## Synopsis (what changes)

```text
# No new subcommand. One new modifier on --status.

kbfix --install   [--quiet]            # behavior EXTENDED
kbfix --uninstall [--quiet]            # behavior EXTENDED
kbfix --status    [--quiet] [--verbose]  # NEW --verbose modifier
kbfix --watch                            # behavior EXTENDED (see §Watcher)
```

### Mutual exclusion (delta)

- `--verbose` is mutually exclusive with `--quiet`. Using both is a
  usage error (exit 64).
- `--verbose` may only be combined with `--status`. Combining it with
  `--install`, `--uninstall`, or `--watch` is a usage error (exit 64).
- All other 003 mutual-exclusion rules are unchanged.

---

## Exit codes — additions

Existing 001 codes (`0`, `1`, `2`, `3`, `64`) and 003 codes (`10`, `11`,
`12`, `13`, `14`) are preserved. Three new codes are added for `--status`:

| Code | Name                        | Meaning |
|------|-----------------------------|---------|
| 15   | `SupervisorBackingOff`      | Installed. Watcher not alive *right now*, but Task Scheduler has a pending retry within its Restart-on-failure budget. Typically transient. |
| 16   | `SupervisorGaveUp`          | Installed. Watcher not alive. Task Scheduler has exhausted its Restart-on-failure budget for this logon session. User action recommended: re-run `--install`. |
| 17   | `AutostartDegraded`         | Installed. Both Run key and Scheduled Task exist but both are disabled (Startup Apps toggle off and task Disabled). Watcher may be running, but will not auto-restart. |

Exit code assignment follows the `Classify()` priority in data-model §5.
When more than one condition is present, the higher-priority (more
actionable) one wins.

---

## `--install` — extension

### New pre-flight check (FR-002)

Before performing install steps, `--install` probes whether each
planned autostart mechanism will be **effective** at the next logon:

1. Run key registration — same as 003.
2. Startup-Apps toggle for `KbFixWatcher` — new, read-only probe.
3. Scheduled Task `\KbFix\KbFixWatcher` status — new, read-only probe.
4. Task Scheduler policy availability — new; attempts a
   `schtasks /Query /TN "KbFix\CanaryProbe"` on a non-existent name and
   classifies the error.

If the probes show autostart cannot be made effective on this machine:

- **User disabled Startup Apps + task creation forbidden by policy** →
  `--install` prints a warning, proceeds with Run-key-only install,
  exits 0 with a clear caveat in the report.
- **All mechanisms succeed** → normal install.
- **Run key write fails** → exit 1 (same as 003).
- **Scheduled Task creation fails for a non-policy reason** → warn,
  fall back to Run-key-only, exit 0.

### Stdout format (additions)

Additional lines in the default-verbosity install report:

```text
KbFix installer
  staged:     C:\Users\<user>\AppData\Local\KbFix\kbfix.exe
  autostart:  HKCU\Run\KbFixWatcher = "...\kbfix.exe" --watch
  task:       \KbFix\KbFixWatcher  (At logon + Restart on failure)
  effective:  yes
  watcher:    started (pid 12345)

Installed. The watcher will also start automatically at your next Windows login.
```

If the Scheduled Task could not be created (policy, schtasks missing):

```text
  task:       NOT installed — Task Scheduler denied creation (policy)
  effective:  yes (Run key only; watcher will not auto-restart after crashes)
```

### `--quiet` behaviour

Unchanged from 003 — only the final summary line is printed. If
install degraded to Run-key-only, the summary line is
`Installed (degraded — no task).` so scripts can detect the condition
without parsing verbose output.

---

## `--uninstall` — extension

### Additional steps

After the existing 003 uninstall steps, two more run:

1. `schtasks /Delete /TN "KbFix\KbFixWatcher" /F` — idempotent.
2. Delete `%LOCALAPPDATA%\KbFix\last-exit.json` and
   `%LOCALAPPDATA%\KbFix\scheduled-task.xml`.

### Stdout format (additions)

Additional lines in the default-verbosity uninstall report:

```text
KbFix uninstaller
  watcher:    stopped (cooperative shutdown)
  autostart:  HKCU\Run\KbFixWatcher  (removed)
  task:       \KbFix\KbFixWatcher  (removed)
  staged:     C:\Users\<user>\AppData\Local\KbFix\  (cleared)

Uninstalled.
```

If the task was never present:

```text
  task:       not present
```

---

## `--status` — extension

The existing output is preserved and **extended** with four new lines.
New fields appear in this order after the 003 fields:

```text
KbFix status
  watcher:          running (pid 12345)
  autostart:        registered  ("...\kbfix.exe" --watch)
  staged:           present  (...\kbfix.exe)
  task:             \KbFix\KbFixWatcher  (Ready, Next Run: 2026-04-24 07:12)
  supervisor:       healthy
  last exit:        CooperativeShutdown  at  2026-04-23T09:14:05Z  (pid 11234)
  autostart effective at next logon:  yes
  log:              C:\Users\<user>\AppData\Local\KbFix\watcher.log

State: InstalledHealthy
```

Format notes:

- `task:` — shows the task's Status and Next Run Time. If absent:
  `not installed`. If disabled: `\KbFix\KbFixWatcher  (Disabled)`.
- `supervisor:` — one of `healthy`, `restart pending (N left)`,
  `gave up (re-run --install)`, `disabled`, `absent`, or `unknown`.
  The parenthetical "(N left)" comes from Task Scheduler's remaining
  restart count — if not obtainable, omit it.
- `last exit:` — populated from `last-exit.json` when present.
  If absent (watcher never ran, or uninstalled): `last exit: (none)`.
- `autostart effective at next logon:` — one of `yes`, `no (reason)`,
  `not registered`. Reasons can name the specific override: "Startup
  Apps toggle off", "task disabled by user/policy", "both mechanisms
  disabled".

### `--status --verbose`

Extends the default output with:

1. The last ~40 lines of `watcher.log` (so a bug report captures recent
   activity). Output is delimited with `----- watcher.log (tail) -----`
   and `----- end -----`.
2. The full XML of the Scheduled Task (if present), delimited similarly.
3. The full `last-exit.json` record (pretty-printed), delimited similarly.

Every delimited section is clearly titled so the user can understand
what they are copy-pasting into a bug report. The format is
intentionally plain text and pasteable — no colors, no escape codes.

The `--verbose` modifier does NOT change the exit code — `--status` and
`--status --verbose` exit the same way for the same observed state.

### `--status --quiet`

Unchanged from 003: suppresses all the indented lines, prints only the
`State: ...` trailer and exits with the state-derived code.

---

## `--watch` — extension

`--watch` is still "internal — spawned by `--install`; not user-facing"
per 003. Its external behaviour is unchanged except:

- On startup, the process writes a `WatcherLog.ProcessStartup(...)`
  line that includes the previous `LastExitReason` (if
  `last-exit.json` exists).
- On cooperative shutdown (stop event), the process writes
  `last-exit.json` with `reason: CooperativeShutdown` and `exitCode: 0`
  before returning.
- On `ConfigUnrecoverable` (60 s grace exhausted), the process writes
  `last-exit.json` with `reason: ConfigUnrecoverable` and `exitCode: 1`
  before returning.
- On an unhandled exception, the `AppDomain.UnhandledException` handler
  writes `last-exit.json` with `reason: CrashedUnhandled`, a short
  `detail` (exception type + first-line message), and the original
  exit code before the process dies.

The set of process exit codes for `--watch` is still `{0, 1, 2}`
(success, failure, unsupported); this contract is unchanged. The JSON
file is a side channel, not an exit-code change.

---

## Graceful degradation

The spec's Edge Cases and research R3 call out that some environments
will not support the full feature. The install / status contract handles
each like so:

| Condition                                      | `--install` result | `--status` state | Exit |
|-----------------------------------------------|--------------------|--------------------|------|
| `schtasks.exe` missing (rare, LTSC)           | Degraded install; task omitted | `supervisor: absent`; exit 0 or the feature-003 code that applies | — |
| Group Policy blocks user Task Scheduler writes | Degraded install; task omitted | `task: denied by policy`; exit 17 if Startup Apps also off | 17 |
| Binary quarantined after install              | — | Watcher absent; `last exit:` likely missing | 11 / 16 |
| Staged binary moved by user                    | Stale path detected | `autostart: STALE` | 13 (unchanged from 003) |

---

## Backwards compatibility

- Every 003-era invocation produces identical behaviour except for
  the four new `--status` output lines (which previous consumers that
  only parsed the `State: ...` trailer will ignore).
- Every 003-era exit code is preserved at its existing value and
  meaning.
- The `State: ...` trailer values are additive: scripts that
  recognize only the 003 values (`InstalledHealthy`, etc.) will see
  004 additions (`SupervisorBackingOff`, `SupervisorGaveUp`,
  `AutostartDegraded`) as unrecognized states and can fall through to
  their existing error path. Exit codes 15/16/17 are similarly additive.
