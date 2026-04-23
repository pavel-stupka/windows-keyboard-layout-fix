# Implementation Plan: Watcher Resilience, Observability, and Self-Healing

**Branch**: `004-watcher-resilience` | **Date**: 2026-04-23 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/004-watcher-resilience/spec.md`

## Summary

Harden the per-user background watcher delivered in feature 003 so that
"installed" genuinely means "running after every sign-in and after every
crash, without the user touching anything." Two layered autostart
mechanisms replace the single `HKCU\Run` entry: the existing Run value
(fast path, unchanged) plus a per-user Scheduled Task registered at
`\KbFix\KbFixWatcher` with an **At log on** trigger and the built-in
**Restart on failure** setting. The Scheduled Task provides both the
belt-and-suspenders autostart (when Run is suppressed by Startup Apps,
Group Policy, or a third-party startup manager) and the out-of-process
supervisor (when the watcher crashes or is killed). No sidecar process,
no Windows Service, no elevation. Exit reasons are extended so
cooperative shutdown (`--uninstall`, stop event) is distinguishable from
crashes, and the last exit reason is persisted to a small JSON file that
`--status` reads. `--status` grows new fields and new exit codes that
cover the supervisor's own state (healthy / backing off / gave up) and a
synthetic "autostart effective" probe. `--install` gains a pre-flight
check that detects Startup-Apps-toggle-disabled entries before the user
discovers the problem after a reboot. `--uninstall` tears down both the
Run key and the Scheduled Task. All existing one-shot and 003-watcher
behaviour is preserved byte-for-byte.

## Technical Context

**Language/Version**: C# 12 on .NET 8 (LTS), target `net8.0-windows10.0.17763.0`, unchanged from features 001/002/003.
**Primary Dependencies**: existing Win32/TSF interop (unchanged); `Microsoft.Win32.Registry` (unchanged); `System.Diagnostics.Process` for `schtasks.exe` invocation; `System.Text.Json` (already in BCL, used for last-exit-reason JSON persistence). **No new NuGet packages.**
**Storage**:
- Filesystem (per-user, additive to 003): `%LOCALAPPDATA%\KbFix\last-exit.json` (~200 bytes, single-record JSON: reason, exit code, timestamp, pid). `%LOCALAPPDATA%\KbFix\scheduled-task.xml` (~2 KB, exported Task XML for auditability and reinstall). Existing `watcher.log` / `watcher.log.1` / `watcher.pid` / `kbfix.exe` unchanged.
- Registry (per-user, unchanged from 003): `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\KbFixWatcher`. Additionally *read only* (never write): `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run` binary value `KbFixWatcher` (2 bytes; bit 0 = enabled) for the Startup-Apps-toggle probe.
- Task Scheduler (per-user, new): task `\KbFix\KbFixWatcher` in the user's scheduler namespace, installed via `schtasks.exe /Create /XML` (XML definition file). Decision recorded in research R2.

**Testing**: xUnit under `tests/KbFix.Tests`. New pure-logic units: `SupervisorDecision` (augments `InstallDecision` with the Scheduled-Task install/repair/uninstall steps), `LastExitReasonStore` (pure serializer over an injected file-system seam), `AutostartProbe` (pure classifier over an injected registry reader). Manual verification per constitution for schtasks invocation, Scheduled-Task firing across reboot modes, and cross-reboot behaviour — see [quickstart.md](./quickstart.md).

**Target Platform**: Windows 10 1809 and Windows 11 desktop, including RDP sessions and fast user switching. Unchanged from 003. `schtasks.exe` ships in every supported Windows version; no extra dependency.

**Project Type**: Desktop CLI utility, single project (`src/KbFix/`), unchanged from 001/002/003.

**Performance Goals**:
- Watcher restart after **crash or unhandled exception within the watcher process**: existing 003 WatcherLoop already catches per-cycle exceptions and backs off → no visible downtime in the common case.
- Watcher restart after **external termination** (Task Manager End Task, antivirus kill, policy): new Scheduled Task's *Restart on failure* restarts in ≤ 90 s. (See research R1 — Task Scheduler's minimum restart interval is 1 minute; 90 s covers restart interval + typical .NET self-contained startup.)
- Watcher present at next sign-in: ≤ 30 s after shell is responsive (SC-001). Either Run key fires (common case, typically ≤ 5 s) or the Scheduled Task At-logon trigger fires (fallback, typically ≤ 30 s).
- `--install` with new pre-flight check: ≤ 8 s on a typical machine (adds ≤ 3 s over 003's 5 s baseline for the `schtasks /Create` + `/Query` roundtrips).
- `--uninstall`: ≤ 8 s (003's 5 s baseline + `schtasks /Delete`).
- `--status`: ≤ 2 s (003's 1 s baseline + one `schtasks /Query /XML` read + one registry read).
- Watcher idle CPU and memory footprint: unchanged from 003 (the Scheduled Task is dormant between triggers; no resident supervisor process is added).

**Constraints**:
- No administrator elevation at any point (FR-005, constitution § Technical Constraints). Hard constraint — `schtasks.exe` run by a non-elevated user can create per-user tasks in its own namespace. See research R3.
- No new third-party NuGet packages (FR-017).
- Published binary size stays ≤ 20 MB, ideally ≤ 15 MB (003's SC-006 still applies). The new `System.Text.Json` usage is trivial and already in the trimmed closure via the BCL — negligible size impact.
- All existing 003 behaviour must remain byte-for-byte compatible. A user who installed under 003 and upgrades by re-running `--install` must transition cleanly (FR-017, FR-019, spec Assumptions last bullet).
- `schtasks.exe` is the only acceptable way to install the Scheduled Task — the tool MUST NOT require PowerShell, .NET SDK, or any other external runtime on the target machine (FR-017, constitution § Distribution).

**Scope/Scale**: Unchanged from 003 — single-user, single-machine, ≤ 1 watcher process, ≤ 1 Scheduled Task entry, ≤ 1 Run key entry. The last-exit-reason file is a single record, overwritten on every watcher exit.

**Spec refinement resolved in Phase 0**: SC-002's original "15 seconds" target is not achievable with Windows Task Scheduler (minimum restart interval = 60 s) and would require a sidecar supervisor process whose complexity is not justified by the observed failure mode. Amended to **90 seconds** and reflected in the spec via a one-line edit (see research R1 for rationale). All other SCs hold as written.

All unknowns resolved in Phase 0 — see [research.md](./research.md). No `NEEDS CLARIFICATION` markers remain.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

The project constitution is `.specify/memory/constitution.md` v1.0.0. Each Core Principle is evaluated against this feature.

### I. Single Purpose & Simplicity

**Status: PASS.**

This feature is a reliability and observability hardening of the existing Phase 2 resident watcher (feature 003). It introduces exactly two new mechanisms — a per-user Scheduled Task and a last-exit-reason JSON file — and the minimum plumbing to install, query, tear down, and surface them. No GUI, no tray icon, no plugin system, no configuration UI, no telemetry. The scope of the code change is additive: `SupervisorDecision`, `ScheduledTaskRegistry`, `StartupApprovedProbe`, `LastExitReasonStore`, and extensions to the existing `InstallDecision`, `WatcherMain`, `WatcherDiscovery`, `StatusReporter`, and `Options`. The public CLI surface grows by zero new subcommands (all enhancements hang off existing `--install` / `--uninstall` / `--status`) plus one extension: `--status --verbose` for the diagnostic snapshot (FR-016). We deferred a sidecar supervisor process precisely because it would violate this principle.

### II. User Configuration Is the Source of Truth

**Status: PASS.**

Nothing in this feature reads or writes the user's persisted keyboard configuration. The Scheduled Task merely launches `kbfix.exe --watch`; the watcher itself still reads `HKCU\Control Panel\International\User Profile` (via `PersistedConfigReader`) verbatim on every poll cycle. The Startup-Apps-toggle probe is read-only and touches a single well-documented HKCU value. The last-exit-reason file records the reason code and a timestamp, nothing about the user's layouts. No new caches, no inference, no guessing.

### III. Safe & Reversible Operations

**Status: PASS.**

- *Never remove or alter persisted settings.* No new write paths to any language/locale registry key.
- *Never leave the session with zero usable input layouts.* No change to the reconciliation pipeline; existing `Refused` handling still in effect.
- *Best-effort rollback / fail-closed.* The install's pre-flight check (FR-002) refuses with a clear message when autostart cannot be made effective, rather than silently registering a broken state. Scheduled-Task creation is wrapped in a try/catch that, on failure, cleans up anything it partially installed and falls back to Run-key-only. `--uninstall` removes the Scheduled Task even if creating it originally failed (idempotent delete).
- *Idempotent.* `SupervisorDecision.ComputeInstallSteps` produces the same step list given the same observed state, whether the Scheduled Task is already present or not. `schtasks /Delete` is idempotent at the exit-code level — we treat "task not found" as success. The last-exit-reason file is overwritten, not appended; same input produces same output.
- *Registry edits only when no API path exists.* The Startup-Approved probe is read-only. The Run key is unchanged. The Scheduled Task uses `schtasks.exe`, which IS the documented Microsoft API for user-mode scheduled task management.

### IV. Native Windows Integration

**Status: PASS.**

All new OS interaction goes through documented Microsoft-provided mechanisms: `schtasks.exe` (shipped in Windows; user-mode tasks do not require admin), the per-user Run key (003 unchanged), the per-user Task Scheduler namespace, and the `StartupApproved\Run` binary value (documented as the backing store for the Startup Apps toggle). No undocumented APIs. No P/Invoke additions. No third-party runtimes.

### V. Observability & Diagnosability

**Status: PASS — strongly aligned.**

This feature is in large part an observability upgrade. New `--status` fields (last lifecycle transition with timestamp, supervisor state, autostart-effective flag, log path) directly serve Principle V. New lifecycle log lines (process start with exit reason of previous run, supervisor restart observed at log-in, task-run result classifier) mean the log now answers "why did it stop?" without a debugger. The diagnostic snapshot `--status --verbose` bundles status + last N log lines + scheduled-task XML dump into one paste-ready blob.

### Constitution Check verdict

**PASS — no exceptions to record.** Complexity Tracking table below is empty.

### Re-check after Phase 1

Re-evaluated after writing `research.md`, `data-model.md`, `contracts/cli.md`, and `quickstart.md`. All five principles still pass. The design stays additive; the only new write surfaces outside of 003's are the per-user Scheduled Task entry and the `last-exit.json` file, both of which `--uninstall` removes.

## Project Structure

### Documentation (this feature)

```text
specs/004-watcher-resilience/
├── plan.md              # This file
├── research.md          # Phase 0 — R1..R8 decisions and rationale
├── data-model.md        # Phase 1 — new entities (LastExitReason, ScheduledTaskEntry, SupervisorState, AutostartEffectiveness)
├── quickstart.md        # Phase 1 — build, install, verify (all reboot modes + kill recovery), uninstall
├── contracts/
│   └── cli.md           # Phase 1 — CLI contract deltas vs. 003 (new exit codes, new --status lines, --verbose)
├── checklists/
│   └── requirements.md  # Spec quality checklist (already passing)
└── tasks.md             # Phase 2 — produced by /speckit.tasks, NOT by /speckit.plan
```

### Source Code (repository root)

```text
src/
└── KbFix/
    ├── Program.cs                      # MODIFY: on --watch mode, install unhandled-exception handler that persists LastExitReason before crash; on --install / --uninstall / --status, route through extended executors.
    ├── KbFix.csproj                    # no change
    ├── Cli/
    │   ├── Options.cs                  # MODIFY: add --verbose modifier for --status (no new subcommand).
    │   ├── Reporter.cs                 # no change
    │   ├── InstallReporter.cs          # MODIFY: report new SupervisorState + AutostartEffectiveness + LastExitReason + scheduled-task fields.
    │   └── StatusReporter.cs           # MODIFY: same — new fields + --verbose snapshot assembly.
    ├── Diagnostics/
    │   └── ExitCodes.cs                # MODIFY: add codes 15 (degraded — supervisor backing off), 16 (degraded — supervisor gave up), 17 (degraded — autostart not effective). Codes 10..14 preserved.
    ├── Domain/                         # no change — reconciliation pipeline untouched.
    ├── Platform/
    │   ├── Win32Interop.cs             # no change
    │   ├── KeyboardLayoutCatalog.cs    # no change
    │   ├── PersistedConfigReader.cs    # no change
    │   ├── SessionLayoutGateway.cs     # no change
    │   ├── TsfInterop.cs               # no change
    │   └── Install/
    │       ├── AutostartRegistry.cs        # no change
    │       ├── BinaryStaging.cs            # no change
    │       ├── InstallExecutor.cs          # MODIFY: handle new step types (CreateScheduledTaskStep, DeleteScheduledTaskStep, ProbeAutostartEffectivenessStep, ReadLastExitReasonStep).
    │       ├── WatcherDiscovery.cs         # MODIFY: populate new WatcherInstallation fields (ScheduledTaskState, AutostartEffective, LastExitReason).
    │       ├── WatcherLauncher.cs          # no change
    │       ├── ScheduledTaskRegistry.cs    # NEW — schtasks.exe wrapper: Create / Delete / Query, all run as current user, idempotent.
    │       └── StartupApprovedProbe.cs     # NEW — reads HKCU StartupApproved\Run\KbFixWatcher and classifies enabled/disabled.
    └── Watcher/
        ├── WatcherMain.cs                  # MODIFY: install AppDomain UnhandledException handler that writes LastExitReason before process dies; on cooperative StopSignaled, write reason = CooperativeShutdown.
        ├── WatcherLoop.cs                  # no change
        ├── ISessionReconciler.cs           # no change
        ├── SessionReconciler.cs            # no change
        ├── FlapDetector.cs                 # no change
        ├── InstallDecision.cs              # MODIFY: call SupervisorDecision to append Scheduled-Task install / uninstall steps to the existing step list; unchanged for the staging/Run-key steps.
        ├── SupervisorDecision.cs           # NEW — pure decision function for Scheduled-Task install/repair/delete and for classifying SupervisorState.
        ├── WatcherInstallation.cs          # MODIFY: add ScheduledTaskState, AutostartEffective, LastExitReason fields + Classify() extensions for new states.
        ├── WatcherLog.cs                   # MODIFY: add new event methods — ProcessStartup(reason), SupervisorObserved(taskResult), AutostartFiredAtLogin(mechanism).
        └── LastExitReasonStore.cs          # NEW — pure JSON serializer (System.Text.Json source-gen compatible) reading/writing %LOCALAPPDATA%\KbFix\last-exit.json.

tests/
└── KbFix.Tests/
    ├── KbFix.Tests.csproj              # no change
    ├── (existing 003 tests — unchanged)
    ├── Watcher/
    │   ├── SupervisorDecisionTests.cs          # NEW — exhaustive decision-table coverage: fresh install, 003 upgrade, task present + run key absent, task absent + run key present, etc.
    │   └── LastExitReasonStoreTests.cs         # NEW — round-trip serialization, graceful handling of missing / corrupt file.
    └── Platform/
        └── StartupApprovedProbeTests.cs        # NEW — classify a set of known HKCU binary values (0x02 0x00... enabled, 0x03 0x00... disabled by user, etc.) via injected registry reader.
```

**Structure Decision**: Single-project layout (Option 1) preserved from 001/002/003. All new code is additive: two new files under `Watcher/` (`SupervisorDecision.cs`, `LastExitReasonStore.cs`) and two under `Platform/Install/` (`ScheduledTaskRegistry.cs`, `StartupApprovedProbe.cs`). The MODIFY list covers only narrowly-scoped edits — adding new step types to `InstallExecutor`, new fields to `WatcherInstallation`, new exit codes, one new `--verbose` modifier. No existing file is rewritten. The pure/unpure separation the codebase already uses (`Watcher/` = pure logic, `Platform/` = Win32/registry/process) is preserved: `SupervisorDecision` and `LastExitReasonStore` are pure; `ScheduledTaskRegistry` and `StartupApprovedProbe` are the two new unpure helpers.

## Complexity Tracking

No constitutional violations. Table intentionally empty.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| _(none)_  | _(none)_   | _(none)_                             |

## Phase 0: Outline & Research — ✅ Complete

Artifact: [research.md](./research.md). Resolved:

- **R1**: Supervision model = Scheduled Task's built-in *Restart on failure* (60 s × 3 attempts). Sidecar supervisor process rejected. Implies SC-002 amended from 15 s to 90 s.
- **R2**: Scheduled Task creation mechanism = `schtasks.exe /Create /XML <path>` (XML definition file). PowerShell's `Register-ScheduledTask` rejected because PowerShell is out of scope for runtime dependencies.
- **R3**: `schtasks.exe` from a non-elevated user successfully creates per-user At-Logon tasks with Restart-on-failure settings, provided the task is in the user's own namespace (`\KbFix\KbFixWatcher`) and runs as LIMITED (no RunLevel=Highest). Verified in Windows 10 1809+ and Windows 11.
- **R4**: "Autostart effective" probe = read `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run` binary value `KbFixWatcher` (bit 0 = enabled); combined with `schtasks /Query /XML <taskname>` for the Scheduled Task's `Enabled` element.
- **R5**: Last exit reason persistence = `%LOCALAPPDATA%\KbFix\last-exit.json`, ~200 bytes, written on all exit paths (cooperative, config-unreadable, unhandled exception via AppDomain handler). Process kill (TerminateProcess) cannot write the file — detected as "process absent without corresponding clean exit record" in `--status`.
- **R6**: Backoff/giveup state = entirely inside Task Scheduler's Restart-on-failure engine; no in-app state machine. `schtasks /Query /V /FO LIST` reports the last run result and next run time, which `--status` classifies into SupervisorState {Healthy, RestartPending, GaveUp, Unknown}.
- **R7**: Testability of Scheduled-Task install decision = the decision function (`SupervisorDecision`) takes `WatcherInstallation` + desired state and returns an ordered step list, no I/O. `ScheduledTaskRegistry` (unpure) is covered by manual verification per constitution.
- **R8**: Upgrade compatibility = existing 003 installs have the Run key; re-running `--install` in 004 adds the Scheduled Task alongside it, leaves the Run key alone, and triggers no extra watcher spawn (existing mutex holds). See SupervisorDecision decision table rows "003-upgrade-case-A/B".

## Phase 1: Design & Contracts — ✅ Complete

Artifacts:

- [data-model.md](./data-model.md) — 4 new entities (`LastExitReason`, `ScheduledTaskEntry`, `SupervisorState`, `AutostartEffectiveness`), 2 extended (`WatcherInstallation`, `InstalledState`); state transitions for the watcher process's exit-reason flow and for the supervisor's restart/giveup flow.
- [contracts/cli.md](./contracts/cli.md) — delta vs. 003 contract: 3 new exit codes (15, 16, 17), 4 new `--status` lines (`last exit`, `supervisor`, `autostart effective`, `scheduled task`), 1 new modifier (`--verbose` applies to `--status` only), updated usage text.
- [quickstart.md](./quickstart.md) — build + manual verification covering: fresh install, 003→004 upgrade, kill recovery (layer 2), Startup-Apps-toggle disabled, sign-out/sign-in loop, RDP reconnect, uninstall completeness check.

Agent context update (Phase 1 step 3): run `.specify/scripts/bash/update-agent-context.sh claude` so `CLAUDE.md` records that 004 adds Task Scheduler (via `schtasks.exe`) and `StartupApproved\Run` to the tool's active dependencies, and `%LOCALAPPDATA%\KbFix\last-exit.json` and `scheduled-task.xml` to the filesystem surface. Done at the end of /speckit.plan.

## Phase 2: Not covered by this command

`/speckit.plan` stops after Phase 1. `/speckit.tasks` will produce `tasks.md` next, decomposing this plan into sequenced implementation tasks mapped to the structure above.
