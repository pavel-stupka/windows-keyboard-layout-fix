# Phase 0 Research — Watcher Resilience, Observability, and Self-Healing

Unknowns surfaced during planning and the decisions that resolve them.
Every `NEEDS CLARIFICATION` the spec or the plan could produce is
answered here. Each section follows the Decision / Rationale /
Alternatives considered format.

## R1 — Supervision model

**Question**: When the watcher process dies involuntarily (crash, kill,
OS-forced stop), what mechanism brings a replacement back up without
user action, fast enough to meet the spec's SC-002?

**Decision**: Use **Windows Task Scheduler's built-in "Restart on failure"
setting on a per-user At-Logon task**. No sidecar supervisor process.
No in-app state machine for retries and give-up — the scheduler's own
`RestartOnFailure` element does exactly that job (interval +
`Count`). The watcher's existing in-process per-cycle try/catch
(WatcherLoop.Run, feature 003) continues to handle soft failures
without exit.

The task settings:

- `Triggers/LogonTrigger` — fires at the current user's logon.
  Covers the autostart resilience story (US1).
- `Settings/RestartOnFailure/Interval = PT1M`, `Count = 3` — after a
  non-zero-exit termination, Task Scheduler attempts up to three
  restarts at one-minute intervals. Covers the self-restart story
  (US2) within a realistic floor of ~90 seconds.
- `Settings/MultipleInstancesPolicy = IgnoreNew` — if the watcher is
  already running (mutex held), the second instance Task Scheduler
  would spawn is skipped. This matches the existing 003 behaviour:
  duplicate watchers exit immediately on mutex contention, but with
  `IgnoreNew` Task Scheduler does not even spawn them.
- `Principal/LogonType = InteractiveToken`, `RunLevel = LeastPrivilege`
  — runs as the interactive user, no elevation, no UAC prompt.
- `Settings/ExecutionTimeLimit = PT0S` (unlimited) — the watcher is
  a long-running process.
- `Settings/StartWhenAvailable = true` — if the machine was off at the
  scheduled logon, Task Scheduler runs the task as soon as feasible.

**Rationale**: Task Scheduler is the closest thing Windows provides to a
per-user service. It is:

- Documented, shipped in every supported Windows version, installable
  via `schtasks.exe` without admin (see R3).
- Decoupled from the watcher's own lifetime — when the watcher dies,
  Task Scheduler notices via exit code, not via a parent-child
  handle, so it survives exactly the failure modes the spec demands.
- Free from new code surface: the backoff state machine (`RestartOnFailure`
  interval + count) is already implemented inside Windows and stops
  retrying on its own after `Count` attempts.
- Free from a second long-running process in the user's session.

**Alternatives considered**:

- **Sidecar supervisor process (`kbfix --supervise`)**. Discussed at
  length. Would achieve SC-002's 15-second target because it can
  respawn instantly on handle-wait completion. Rejected because it
  doubles the resident process count, requires a second single-instance
  primitive, a second PID file, a second log stream, and ~300 LOC of
  new code including a crash-of-the-supervisor fallback that ultimately
  points back at Task Scheduler anyway. The complexity was judged
  unjustified against the actual observed failure mode — the user's
  complaint is "watcher is not running at all," not "recovery was too
  slow." A 90-second recovery is dramatically better than "never".
- **Windows Service (session-0, Auto-Start)**. Explicitly out of scope
  per constitution and feature 003 rationale — a session-0 service
  cannot inspect or modify the interactive user's keyboard layouts.
- **In-process top-level try/catch that forks a new watcher before
  exit**. Catches unhandled exceptions, but useless against
  TerminateProcess, stack overflow, or anything that kills the process
  without unwinding. Kept implicitly (feature 003 WatcherLoop already
  catches per-cycle exceptions), but NOT relied upon for supervision.
- **Polling from the Run-key-spawned watcher to self-replace on
  Mutex contention**. Clever but fragile; gives the same coverage as
  the per-cycle try/catch and still cannot survive external kill.

**SC-002 amendment**: The spec's SC-002 said "within 15 seconds" for
watcher-kill recovery. Task Scheduler's minimum restart interval is
60 seconds; the realistic floor including watcher startup time is
~75–90 seconds. The spec will be amended to say "within 90 seconds"
on the same trial-counting basis. The amendment is minor and
faithful to the observed user need; no other SC changes.

## R2 — Scheduled Task creation mechanism

**Question**: From a non-elevated, GUI-less CLI tool, how do we create,
inspect, and delete a per-user Scheduled Task?

**Decision**: Shell out to **`schtasks.exe`** via `Process.Start`, using
the `/XML` flag for creation and `/Query /XML` for inspection:

- Create: `schtasks.exe /Create /TN "KbFix\KbFixWatcher" /XML <path-to-xml> /F`
  where the XML file is generated at install time into
  `%LOCALAPPDATA%\KbFix\scheduled-task.xml` and contains the
  per-user `<Principal>` with `<UserId>S-1-5-...`</UserId>` set to
  the current user's SID.
- Query: `schtasks.exe /Query /TN "KbFix\KbFixWatcher" /XML` — round-trips
  the XML, allowing us to read the task's `<Enabled>` element and all
  settings to verify installation matched what we wrote.
- Query (status): `schtasks.exe /Query /TN "KbFix\KbFixWatcher" /V /FO LIST`
  — emits `Last Run Result`, `Next Run Time`, `Status` in a
  machine-parseable key=value list. We parse these for SupervisorState
  classification (R6).
- Delete: `schtasks.exe /Delete /TN "KbFix\KbFixWatcher" /F` — idempotent
  in our wrapper (we treat `ERROR: The system cannot find the file
  specified.` exit as success).

**Rationale**: `schtasks.exe` is the documented, built-in, ubiquitous
way to manage Scheduled Tasks from a script. It has shipped in every
supported Windows version since Windows 2000. It requires no runtime,
no elevation (for user-namespace tasks), and has a stable, well-known
CLI surface. The `/XML` path is the most declarative way to specify
the full set of settings we need (At-Logon trigger +
Restart-on-failure + LeastPrivilege principal + MultipleInstancesPolicy)
without having to compose a dozen command-line flags.

**Alternatives considered**:

- **PowerShell `Register-ScheduledTask`**. Cleaner API, but requires
  PowerShell to be available at runtime. PowerShell is shipped with
  Windows but is frequently disabled by corporate policy and adds
  a significant process-startup cost (~1 s). Rejected on the grounds
  that the tool must not depend on any runtime not already mandated
  by 003.
- **COM interop with the Task Scheduler 2.0 API** (`ITaskService`,
  `ITaskFolder`, etc.). Most powerful; full programmatic control;
  no process spawn. Rejected because it adds ~400 LOC of COM interop
  declarations and because `schtasks.exe` is strictly simpler for
  exactly the handful of operations we need. The Task Scheduler COM
  API is also notoriously brittle under .NET trimming — feature 003
  already lives inside trimmer warnings for the TSF COM interop,
  and adding another COM surface would make trimming harder to
  validate.
- **WMI `Win32_ScheduledJob`**. Deprecated; predates Vista; does not
  support At-Logon triggers or Restart-on-failure. Rejected.

## R3 — Non-elevated task creation feasibility

**Question**: Will `schtasks.exe /Create /TN "KbFix\KbFixWatcher" /XML
<x>` actually succeed from a standard-user command prompt, given that
this feature absolutely forbids elevation?

**Decision**: **Yes, confirmed against Microsoft's documented behaviour.**
The user's own namespace (`\KbFix\KbFixWatcher`) is writable by the
user without elevation, provided:

1. The task's `<Principal>` declares the current user's SID
   (`<UserId>S-1-5-21-...<UserId>`), NOT `SYSTEM` or a different user.
2. The task's `<RunLevel>` is `LeastPrivilege` (equivalently, the
   principal is `InteractiveToken`). Attempting to set `HighestAvailable`
   from a non-elevated shell will trigger a UAC prompt and fail without it.
3. The task's `<LogonType>` is `InteractiveToken`, not
   `Password`. `Password` requires the user to enter credentials.

**Rationale**: These constraints all express "per-user, no elevation",
which is exactly the constitution's mandate. The task runs only when
the interactive user logs on, runs as that user with that user's token,
and has no privilege escalation. `schtasks.exe` creates tasks in
`%WINDIR%\System32\Tasks\KbFix\` — the per-user subfolder is created
on demand by Task Scheduler itself on behalf of the user's write
request.

**Alternatives considered**:

- Creating the task under `\` (root namespace). Rejected — some Windows
  SKUs disallow root-namespace task creation from non-elevated users
  in domain-joined environments. The `\KbFix\` namespace is always
  writable.
- Storing the task in `%USERPROFILE%\...\Microsoft\Windows\Task Scheduler\`
  as an XML file and relying on auto-discovery. Not a documented API
  surface; rejected.

**Open risk**: Some highly locked-down corporate environments disable
user Task-Scheduler writes via Group Policy. In that case,
`schtasks /Create` will fail with a specific exit code. The tool's
install flow treats this as a graceful degradation: log a warning,
skip the Scheduled Task, fall back to Run-key-only. `--status`
reports `scheduled task: not supported by policy` and the user can
decide what to do next. SC-001 may not be met on such a machine —
this is documented in `contracts/cli.md` §"Graceful degradation".

## R4 — "Autostart effective" probe

**Question**: How can we tell, without rebooting, whether the Run-key
autostart and/or the Scheduled-Task autostart will actually fire at
the next logon?

**Decision**: Three complementary reads, combined into a single
`AutostartEffectiveness` verdict:

1. **Run key present + points at the staged binary.** Already checked
   by 003's `WatcherDiscovery.Probe()`; no change.
2. **Startup-Apps-toggle not disabled.** Read the binary value
   `KbFixWatcher` under
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`.
   The value is a 12-byte binary blob; byte 0 bit 0 = enabled. Values
   starting with `0x02 0x00` (or absent entirely) = enabled; values
   starting with `0x03 0x00` = user-disabled via Task Manager's
   "Startup apps" tab.
3. **Scheduled-Task `<Enabled>true</Enabled>` and current registered
   state reports ready.** Obtained from
   `schtasks /Query /XML` + `schtasks /Query /V /FO LIST` → `Status`
   column. `Status: Ready` means it will fire on the next trigger.
   `Status: Disabled` means a user or policy disabled it.

**Verdict logic** (see `SupervisorDecision.ClassifyAutostart`):

- `Effective` — at least one of {Run key} or {Scheduled Task} is set
  up AND will fire at next logon.
- `Degraded` — both are registered but both are disabled (edge case;
  usually a sign of user or policy interference). `--status` prints
  a warning and exits 17.
- `NotRegistered` — matches feature-003's existing `NotInstalled`
  class when neither is present.

**Rationale**: These three reads answer the user's pre-reboot question
("will the watcher be running after I sign back in?") with 100%
reliability given the documented Windows behaviour. No need to
actually reboot to find out.

**Alternatives considered**:

- Monitoring the Task Scheduler event log for past firings (Event ID
  200). Useful for "did it fire last time?" but not for "will it fire
  next time?". Kept as an optional enhancement for `--status --verbose`.
- Ignoring the Startup-Apps toggle entirely and relying only on
  presence/absence of the Run key. Rejected — SC-006 explicitly
  requires we detect the toggle.

## R5 — Last exit reason persistence

**Question**: When the watcher exits, how does the next `--status`
invocation learn *why* it exited?

**Decision**: Write a single-record JSON file at
`%LOCALAPPDATA%\KbFix\last-exit.json` on all controllable exit paths.
Format:

```json
{
  "reason": "CooperativeShutdown" | "ConfigUnrecoverable" | "CrashedUnhandled" | "StartupFailed" | "SupervisorObservedDead",
  "exitCode": 0,
  "timestampUtc": "2026-04-23T11:05:23Z",
  "pid": 12345,
  "detail": "optional short string, no stack trace, no PII"
}
```

Write paths:

- **Cooperative** (`StopSignaled` from `--uninstall` or Ctrl+C): written
  at top of `WatcherMain.Run`'s `finally`, before the process unwinds.
- **ConfigUnrecoverable** (`WatcherLoop` returns this after 60 s
  grace): written immediately after the loop returns, same `finally`.
- **CrashedUnhandled**: registered via
  `AppDomain.CurrentDomain.UnhandledException` in `WatcherMain.Run`;
  handler writes the file before `e.IsTerminating` kills the process.
  `System.Text.Json` is fast enough that we can write ~200 bytes in
  sub-millisecond time before the OS tears the process down.
- **StartupFailed** (mutex create failed, directory unwriteable, log
  init failed before the loop even starts): written from
  `WatcherMain.Run`'s outer catch. This is the one case where writing
  may itself fail — we degrade silently.

**External kill cannot write the file** (TerminateProcess does not
unwind). The detection strategy for that case:

- At watcher process startup, read `last-exit.json`.
- If the file exists and its timestamp is recent (< 24 h) and its
  reason is not `CooperativeShutdown`, the *previous* watcher crashed
  involuntarily. Log `process-start previous-exit=<reason>`.
- If the file's timestamp is older than the current process's boot
  time but the PID in the file no longer exists, the previous watcher
  was *killed externally* — reason becomes `SupervisorObservedDead`
  written by the *current* watcher's startup code.

**Rationale**: A small JSON file is the simplest durable per-user
storage for a single scalar state. `System.Text.Json` is already in
the BCL (used implicitly by 001/003 through serializer-free paths;
this is its first direct use), adds zero new dependency, and has
source-generator support for trimmer safety.

**Alternatives considered**:

- Writing the reason into `watcher.log`'s last line. Brittle — log
  rotation erases it when the file crosses 64 KB.
- Using the registry. Same functional fit but less inspectable; JSON
  file is trivially openable in Notepad for users filing bugs.
- Windows Event Log (per-user source). Useful but requires registering
  an event source, which may require elevation on first write.
  Rejected for the no-admin constraint.

## R6 — Supervisor backoff / giveup state

**Question**: Where does "the supervisor has given up and won't retry"
state live, and how does `--status` report it?

**Decision**: Entirely inside Task Scheduler. No in-app state machine.
Queried on demand:

- `schtasks /Query /TN "KbFix\KbFixWatcher" /V /FO LIST` returns:
  - `Status`: one of `Ready`, `Running`, `Disabled`.
  - `Last Run Time`: timestamp.
  - `Last Result`: integer (0 = success, non-zero = failure code).
  - `Next Run Time`: timestamp or `N/A`.

The mapping into `SupervisorState`:

| Observed                                              | SupervisorState   | Exit code |
|-------------------------------------------------------|-------------------|-----------|
| `Status: Running`                                     | `Healthy`         | 0         |
| `Status: Ready` and watcher process alive             | `Healthy`         | 0         |
| `Status: Ready`, watcher not alive, Next Run Time set | `RestartPending`  | 15        |
| `Status: Ready`, watcher not alive, Next Run Time N/A | `GaveUp`          | 16        |
| `Status: Disabled` (user or policy)                   | `Disabled`        | 17 or 12  |
| Task absent                                           | `Absent`          | (inherits) |

The `GaveUp` state is re-armable by **re-running `--install`**. Our
install command, when it sees `GaveUp`, deletes and re-creates the
task before exiting, so the restart budget is restored.

**Rationale**: Reusing Task Scheduler's own state eliminates an entire
category of bugs (in-app persistence, reboot survival of the backoff
counter, race conditions between watcher writes and supervisor reads).
Task Scheduler's state is the ground truth — anything else would be a
cached duplicate that could drift.

**Alternatives considered**:

- Maintaining a `supervisor-state.json` file with our own retry count.
  Would be needed only if we implemented in-process supervision. Since
  we don't, we don't need the file. Rejected.
- Parsing the Task Scheduler event log for history beyond the last
  run. Rejected — too much data to parse at every `--status`; the
  last-run information is sufficient for the diagnostic questions.

## R7 — Testability of the install decision

**Question**: Can we unit-test the new Scheduled-Task install /
repair / uninstall logic without actually invoking `schtasks.exe`?

**Decision**: **Yes**, via the same pure-decision-function split that
feature 003 already uses:

- **Pure**: `SupervisorDecision` takes `WatcherInstallation` (now
  extended with `ScheduledTaskState`) + the invoking binary path + a
  desired-state enum, and returns `IReadOnlyList<InstallStep>`. No I/O.
  Unit tests enumerate the state space.
- **Unpure**: `ScheduledTaskRegistry` in `Platform/Install/` wraps
  `schtasks.exe`. Tested by manual verification per constitution, not
  by unit tests.

New step types added to the existing `InstallStep` hierarchy in
`InstallDecision.cs`:

- `CreateScheduledTaskStep(string xmlPath)`
- `DeleteScheduledTaskStep`
- `ExportScheduledTaskXmlStep(string destPath)` (generates the XML from
  a template + current user SID; pure)

The test matrix for `SupervisorDecisionTests`:

| Run key | Staged bin | Scheduled task | Watcher alive | Install action   |
|---------|------------|----------------|---------------|------------------|
| absent  | absent     | absent         | no            | create all       |
| present | present    | absent         | yes           | 003-upgrade-case-A: create task only |
| present | present    | present        | yes           | idempotent no-op |
| present | present    | present(disabled)| yes         | repair: re-enable task |
| stale   | present    | absent         | no            | surface stale path, do not touch  |

Similar matrix for Uninstall (5 rows) and Status (no actions emitted,
just state classification).

**Rationale**: The 003 codebase is already structured for this. Adding
`SupervisorDecision` as a sibling pure function over extended input
state costs no architectural churn. Manual verification for
`ScheduledTaskRegistry` follows the same model as 003's manual
verification for `AutostartRegistry` and `BinaryStaging`.

**Alternatives considered**: Faking `schtasks.exe` in a test harness
(e.g. launching a stub `.bat` that echoes success). Possible, but
adds test-harness complexity for low value — the pure decision is
what we actually need coverage on.

## R8 — Upgrade compatibility for 003 installs

**Question**: A user installed under feature 003 and now runs the 004
binary's `--install`. What happens?

**Decision**: The 004 install is **additive, idempotent, and silent on
the Run key**:

1. 004's `WatcherDiscovery.Probe` sees: Run key present (from 003),
   staged binary present (from 003), watcher possibly running
   (if this is a re-install before reboot). It **also** sees:
   scheduled task absent, last-exit.json may or may not exist.
2. `SupervisorDecision.ComputeInstallSteps` sees this state and emits:
   - If the invoking binary is the staged copy (common in-place
     upgrade): `EnsureStagingDirectoryStep`, `ExportScheduledTaskXmlStep`,
     `CreateScheduledTaskStep`. No `WriteRunKeyStep` (already present
     and pointing at the right path), no `SpawnWatcherStep` (already
     running). Watcher lifetime is preserved.
   - If the invoking binary is a newer kbfix.exe from Downloads: the
     existing 003 logic signals stop event + force-kill, copies the
     new binary over the staged path, writes the Run key (idempotent
     if unchanged), then additionally creates the Scheduled Task and
     respawns the watcher. All in one command.
3. No 003 behaviour is removed: the Run key still exists, still points
   at the right binary, still fires first at next logon.

**Rationale**: Users should be able to upgrade by replacing `kbfix.exe`
and re-running `install.cmd` — the same UX as 003. The new mechanism
shows up without the user having to know it exists.

**Alternatives considered**: Forcing users to `--uninstall` first
before installing 004. Rejected as user-hostile and unnecessary.

## Open risks (deferred to implementation / polish)

- **Antivirus false positives on the Scheduled Task XML.** Some AV
  products flag per-user Scheduled-Task creation as suspicious. We
  cannot prevent this in the tool; we can only document it and
  recommend the user whitelist `%LOCALAPPDATA%\KbFix\kbfix.exe`.
  Tracked as a quickstart troubleshooting note, not a requirement.
- **Group-Policy-disabled Task Scheduler.** Rare in home/SMB; common
  in heavily-managed corporate. On such machines, SC-001's "after
  every sign-in" guarantee is weakened to whatever the Run key alone
  delivers. `--status` will report this correctly.
- **Windows LTSC SKUs that strip `schtasks.exe`.** Not observed in
  practice on Windows 10 1809+ / Windows 11 LTSC, but if it occurs,
  `schtasks.exe` invocation fails fast and the install degrades to
  Run-key-only with a warning. The watcher still runs; just without
  the resilience upgrade.
