# Tasks: Watcher Resilience, Observability, and Self-Healing

**Input**: Design documents from `specs/004-watcher-resilience/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/cli.md, quickstart.md — all present.

**Tests**: Test tasks are INCLUDED — the project constitution v1.0.0 requires unit tests for pure logic (constitution §Development Workflow) and the manual-verification gate in `quickstart.md` gates release. Every pure-logic addition in this feature has a unit-test task.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: Which user story this task belongs to (US1..US5)
- All file paths are relative to the repository root.

## Path Conventions

Single project layout inherited from features 001/002/003:

- Production code: `src/KbFix/...`
- Tests: `tests/KbFix.Tests/...`
- Specs / docs: `specs/004-watcher-resilience/...`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Sanity-check the existing solution before any new code lands. Feature 004 reuses the 001/002/003 project skeleton; no new projects, no new packages.

- [X] T001 Run `build.cmd release` and confirm the 003 baseline still builds green with no new warnings; record the current published binary size so T037 can verify there is no regression. Baseline: 11,648,549 B (≈ 11.1 MB).

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Extend the shared domain types and exit-code surface that all user stories consume. Each task here unblocks at least two user stories.

**⚠️ CRITICAL**: No user story phase may begin until this phase is complete.

- [X] T002 [P] Extend `WatcherExitReason` enum in `src/KbFix/Watcher/WatcherLoop.cs` with `CrashedUnhandled`, `StartupFailed`, `CooperativeShutdown`, `SupervisorObservedDead` — values must be string-stable for JSON serialization (consumed by T006, T020–T022).
- [X] T003 [P] Extend `InstalledState` enum in `src/KbFix/Watcher/WatcherInstallation.cs` with `SupervisorBackingOff`, `SupervisorGaveUp`, `AutostartDegraded`; leave existing enum ordinals unchanged (consumed by T025, T027).
- [X] T004 [P] Add exit codes `15 SupervisorBackingOff`, `16 SupervisorGaveUp`, `17 AutostartDegraded` to `src/KbFix/Diagnostics/ExitCodes.cs` with XML doc comments pointing at `specs/004-watcher-resilience/contracts/cli.md` (consumed by T027).
- [X] T005 Extend `WatcherInstallation` record in `src/KbFix/Watcher/WatcherInstallation.cs` with four new fields — `ScheduledTaskEntry ScheduledTask`, `AutostartEffectiveness AutostartEffectiveness`, `SupervisorState SupervisorState`, `LastExitReason? LastExitReason`; also declare the three new record types `ScheduledTaskEntry`, `SupervisorState` (enum), `AutostartEffectiveness` (enum) exactly per `data-model.md` §2–4; update the `Classify()` method to implement the new-state priority order documented in `data-model.md` §5. (Init-only properties so 003 positional constructor stays backward-compatible; Classify falls through to 003 behaviour when 004 fields sit at their defaults.)
- [X] T006 Create `LastExitReasonStore` pure reader/writer in `src/KbFix/Watcher/LastExitReasonStore.cs` — single-record JSON at `%LOCALAPPDATA%\KbFix\last-exit.json` per `data-model.md` §1; must use `System.Text.Json` with a `[JsonSerializable(typeof(LastExitReason))]` source-generated context to stay trim-safe; `Read()` must return `null` on file-absent or schema-violating input (never throw); `Write()` must be atomic (write to `.tmp` then `File.Move` with overwrite) so a crash mid-write cannot corrupt the file.
- [X] T007 [P] Unit tests for `LastExitReasonStore` in `tests/KbFix.Tests/Watcher/LastExitReasonStoreTests.cs` — cover round-trip for each `WatcherExitReason` value, graceful null-return on missing file / invalid JSON / out-of-range enum, atomic-write semantics (write, interrupt via thrown exception, verify original file intact), max-`detail`-length truncation at 200 bytes UTF-8.

**Checkpoint**: Foundation ready — shared types compile, tests pass, no runtime changes. User story phases can now begin.

---

## Phase 3: User Story 1 — Watcher survives reboots unconditionally (Priority: P1) 🎯 MVP

**Goal**: After `--install`, the watcher is running within 30 s of every subsequent sign-in (cold boot, warm reboot, sign-out/sign-in, unlock, RDP reconnect), delivered by layering a per-user Scheduled Task alongside the existing `HKCU\Run` entry. The task's At-Logon trigger is the belt-and-suspenders for cases where Run is suppressed, delayed, or overridden.

**Independent Test**: From a fresh install, run the 5×5 reboot-mode matrix in `quickstart.md` §7. Every sign-in reports `State: InstalledHealthy` within 30 s. Also verify `quickstart.md` §2: after a single `--install`, `schtasks /Query /TN "KbFix\KbFixWatcher"` returns Ready and the XML shows the correct principal, logon type, and run level.

### Implementation for User Story 1

- [X] T008 [P] [US1] Create `ScheduledTaskRegistry` in `src/KbFix/Platform/Install/ScheduledTaskRegistry.cs` — wraps `schtasks.exe` with `Create(xmlPath)`, `Delete()` (idempotent — treat "cannot find the file specified" as success), `QueryXml()` returning the task XML or null-if-absent, and `QueryVerbose()` returning the parsed LIST-format status fields (`Status`, `Last Run Time`, `Last Result`, `Next Run Time`) per research R6; shell out via `Process.Start` with `UseShellExecute=false`, `CreateNoWindow=true`, and a 10 s hard timeout per call.
- [X] T009 [US1] Add pure XML-template generator in `src/KbFix/Platform/Install/ScheduledTaskRegistry.cs` (static `BuildTaskXml(string stagedBinaryPath, string userSid)` method) — emits the full Task Scheduler XML with `<LogonTrigger>` (for the current user SID), `<Principal LogonType=InteractiveToken RunLevel=LeastPrivilege>`, `<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><StartWhenAvailable>true</StartWhenAvailable></Settings>`, `<RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>` (T018 included upfront), and `<Actions><Exec><Command>` pointing at the staged binary with `<Arguments>--watch</Arguments>`; structurally matches research R1.
- [X] T010 [P] [US1] Add new step records `CreateScheduledTaskStep(string xmlPath)`, `DeleteScheduledTaskStep`, `ExportScheduledTaskXmlStep(string destPath, string stagedPath)` to `src/KbFix/Watcher/InstallDecision.cs` beside the existing step-record hierarchy.
- [X] T011 [US1] Wire the new step types into `src/KbFix/Platform/Install/InstallExecutor.cs` — add `switch` cases that delegate to `ScheduledTaskRegistry.Create`, `ScheduledTaskRegistry.Delete`, and (for `ExportScheduledTaskXmlStep`) `File.WriteAllText(path, ScheduledTaskRegistry.BuildTaskXml(...))`; maintain the 003 pattern of returning `StepResult` with a `Note` string for the reporter.
- [X] T012 [US1] Create `SupervisorDecision` in `src/KbFix/Watcher/SupervisorDecision.cs` — pure decision function `AppendInstallSteps(List<InstallStep>, WatcherInstallation state, string invokingBinaryPath)` and `AppendUninstallSteps(List<InstallStep>, WatcherInstallation state)` that emit the new step types per the decision table in `research.md` §R8 and `data-model.md` §2; also exposes `ClassifySupervisor(WatcherInstallation) -> SupervisorState` per `data-model.md` §3.
- [X] T013 [US1] Update `InstallDecision.ComputeInstallSteps` and `ComputeUninstallSteps` in `src/KbFix/Watcher/InstallDecision.cs` to invoke `SupervisorDecision.AppendInstallSteps` / `AppendUninstallSteps` after the existing 003 steps — preserves the 003 invariant that the Run key is written first, the task second; uninstall skips the task-delete step when the task is absent (keeps 003's "nothing installed → zero steps" contract).
- [X] T014 [US1] Extend `WatcherDiscovery.Probe` in `src/KbFix/Platform/Install/WatcherDiscovery.cs` to populate the new `ScheduledTask`, `LastExitReason`, `SupervisorState`, and `AutostartEffectiveness` fields via `ScheduledTaskRegistry.Query`, `LastExitReasonStore.Read`, `SupervisorDecision.ClassifySupervisor`, and `SupervisorDecision.ClassifyAutostart`; preserve all existing 003-derived fields.
- [X] T015 [US1] Update `InstallReporter.cs` in `src/KbFix/Cli/InstallReporter.cs` — `FormatInstall` and `FormatUninstall` emit the new `task:` line with wording that handles absent / created / deleted / denied-by-policy states; install's trailer adds a "degraded — no scheduled task" variant when schtasks /Create fails.
- [X] T016 [P] [US1] Unit tests for `SupervisorDecision` in `tests/KbFix.Tests/Watcher/SupervisorDecisionTests.cs` — exhaustive coverage for both install/uninstall step emission, every `ClassifySupervisor` branch (Absent / Disabled / Healthy / RestartPending / GaveUp / Healthy-via-task-running), and every `ClassifyAutostart` branch (Effective / Degraded / NotRegistered) across the 3×3 mechanism matrix.
- [X] T017 [P] [US1] Unit test for `ScheduledTaskRegistry.BuildTaskXml` in `tests/KbFix.Tests/Platform/ScheduledTaskRegistryTests.cs` — 13 assertions verify XML structure: correct SID in both `<UserId>` positions, `<LogonType>InteractiveToken</LogonType>`, `<RunLevel>LeastPrivilege</RunLevel>`, no `HighestAvailable` / SYSTEM / Administrators, `<RestartOnFailure>` with `PT1M` / `Count 3`, `<Command>` pointing at staged binary, `<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>`, `<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>`, `\KbFix\KbFixWatcher` namespace (not root), Task Scheduler 1.2 schema declaration, and XML-escapes special characters in paths.

Also completed within US1 (originally in later phases but co-located for cohesion):
- [X] T032 [US4] Create `StartupApprovedProbe` in `src/KbFix/Platform/Install/StartupApprovedProbe.cs` — reads HKCU StartupApproved\Run; classifies as `Enabled` on absent / byte0 low bit clear, `Disabled` on byte0 low bit set; graceful on exceptions; optional registry-reader seam for testing.
- [X] T038 [P] [US4] ClassifyAutostart tests in `SupervisorDecisionTests` (covered by the 3×3 mechanism matrix).

**Checkpoint**: US1 is complete — `--install` registers both Run key and Scheduled Task; `--uninstall` removes both; reboot-mode matrix passes.

---

## Phase 4: User Story 2 — Watcher restarts itself after a crash (Priority: P1)

**Goal**: After the watcher process dies involuntarily (crash, external kill, OS-forced stop), a replacement watcher is running again within ≤ 90 s via the Scheduled Task's built-in Restart-on-failure setting. Cooperative shutdown (`--uninstall`, stop event) is distinguishable from involuntary exit and does NOT trigger a counter-spawn.

**Independent Test**: From a healthy install, kill the watcher via Task Manager. Within 90 s, `kbfix --status` reports `supervisor: healthy` and the watcher is back. Repeat 10 times per `quickstart.md` §5; at least 9 of 10 trials succeed.

### Implementation for User Story 2

- [X] T018 [US2] RestartOnFailure PT1M × Count 3 included in BuildTaskXml upfront (T009); T017 asserts exact values.
- [X] T019 [P] [US2] `SupervisorDecision.ClassifySupervisor` implemented in Phase 3 (T012) with branches per data-model §3.
- [X] T020 [US2] `AppDomain.UnhandledException` handler installed in `WatcherMain.Run` before mutex acquisition; writes `CrashedUnhandled` with exception-type + first-line message (detail truncated to 200 B via `LastExitReasonStore.Sanitize`).
- [X] T021 [US2] `WatcherMain.Run` persists the exit reason on every controllable path — `StopSignaled → CooperativeShutdown (exit 0)`, `ConfigUnrecoverable → ConfigUnrecoverable (exit 1)`, outer catch → `StartupFailed (exit 1)`. All writes wrapped in try/catch per the logging-must-never-rethrow rule.
- [X] T022 [US2] `TryDetectSupervisorObservedDead` runs at the top of `Run`. Only writes a new `SupervisorObservedDead` record when the prior reason is unrecognised (preserves the more-informative CrashedUnhandled / ConfigUnrecoverable / StartupFailed records); logs `SupervisorObservedDead` whenever the prior PID is dead. Cooperative-shutdown prior is treated as normal autostart (no signal).
- [X] T023 [P] [US2] `IWatcherLog` + `WatcherLog` gain `ProcessStartup(string previousReason)` and `SupervisorObservedDead(int previousPid)` INFO methods. `RecordingLog` test double updated to implement the new interface members.
- [X] T024 [P] [US2] ClassifySupervisor tests covered in Phase 3 (T012 / SupervisorDecisionTests).

**Checkpoint**: US2 is complete — kill the watcher, observe the task restart it within 90 s; the `last-exit.json` trail correctly distinguishes cooperative vs. crash vs. externally-killed exits.

---

## Phase 5: User Story 3 — Diagnose why the watcher is not running (Priority: P1)

**Goal**: `kbfix --status` output is self-explanatory for the three diagnostic questions from spec SC-004: "is it running right now?", "will it be running after my next sign-in?", "why did it stop last time?". New exit codes distinguish supervisor-degraded states. A `--verbose` modifier bundles enough context to paste into a bug report.

**Independent Test**: Force each of the six "not running" states from `quickstart.md` §5 (no autostart, stale-path, recent clean exit, recent crash, exceeded retry budget, first-sign-in grace) and verify the `--status` output unambiguously identifies which one applies. Exit codes match `contracts/cli.md`.

### Implementation for User Story 3

- [X] T025 [US3] `StatusReporter.Format` extended with four new lines — `task:`, `supervisor:`, `last exit:`, `effective:` — in the documented positions; label alignment widened from 3 to 4 spaces to accommodate the longer 004 labels. Existing 003 lines preserved in their original order.
- [X] T026 [US3] `StatusReporter.ExitCodeFor` maps new `InstalledState` values to codes 15/16/17.
- [X] T027 [US3] `--verbose` modifier added to Options — `Verbose` bool with default false, mutual exclusion with `--quiet` AND requiring `--status`, usage text extended per contract.
- [X] T028 [US3] `StatusReporter.FormatVerbose` + `StatusReporter.ReadLogTail` emit the three delimited blocks (`watcher.log`, `scheduled-task.xml`, `last-exit.json`), each with a `----- end -----` footer and missing-file-tolerant fallbacks.
- [X] T029 [US3] `Program.RunStatus` routes `options.Verbose` through `FormatVerbose`, pulling the log tail and task XML from their readers; non-verbose path unchanged.
- [X] T030 [P] [US3] 6 new StatusReporterTests cover task/supervisor/last-exit/effective lines, verbose snapshot structure, and missing-file tolerance.
- [X] T031 [P] [US3] 5 new OptionsTests cover `--status --verbose`, `--verbose` alone, `--install --verbose`, `--status --verbose --quiet`, and the default-false case.

**Checkpoint**: US3 is complete — `--status` and `--status --verbose` both produce the documented output for every reportable state; exit codes match the contract.

---

## Phase 6: User Story 4 — Verify autostart actually fires (Priority: P2)

**Goal**: `--install` detects, before reporting success, whether the new autostart registration will actually be *effective* at next logon — i.e. not overridden by the per-user Startup-Apps toggle or by a disabled Scheduled Task. `--status` exposes this effectiveness explicitly so the user does not have to reboot to find out.

**Independent Test**: Run the `quickstart.md` §6 scenarios — disable the Startup Apps toggle for `KbFixWatcher`; disable the Scheduled Task; observe that `--status` correctly reports `autostart effective at next logon: no` and exits 17 when both are disabled.

### Implementation for User Story 4

- [ ] T032 [P] [US4] Create `StartupApprovedProbe` in `src/KbFix/Platform/Install/StartupApprovedProbe.cs` — reads binary value `KbFixWatcher` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`; classifies as `Enabled` when the value is absent, or when byte 0 is `0x02`; classifies as `Disabled` when byte 0 is `0x03`; graceful on exceptions (return `Enabled` as a safe default, matches the Windows-Explorer semantics of "no entry = enabled"). Accept an optional `Func<RegistryKey?>` seam for unit testing.
- [X] T033 [US4] `SupervisorDecision.ClassifyAutostart` implemented in Phase 3 (T012) per the 3×3 derivation rule.
- [X] T034 [US4] `WatcherDiscovery.Probe` already populates `AutostartEffectiveness` via the probe + classifier (T014 in Phase 3).
- [X] T035 [US4] Already satisfied by the XML template — `schtasks /Create /XML ... /F` on a disabled task re-emits it as Enabled because the template declares `<Settings><Enabled>true</Enabled>`. No dedicated `EnableScheduledTaskStep` record needed; the /F semantics + the install-always-emits-Export+Create rule guarantee reenablement on every --install.
- [X] T036 [US4] `Program.RunInstall` distinguishes Scheduled-Task creation failure (degraded, exit 0 with stderr WARN) from other step failures (exit 1). Install report already flags the degraded state in its trailer via T015's "Installed (degraded — no scheduled task)" wording.
- [X] T037 [P] [US4] 8 `StartupApprovedProbeTests` cover every byte pattern (absent / zero-length / 0x02 / 0x03 / 0xFF / 0x06), missing sub-key, and factory-throws scenario. Uses a per-test temp HKCU scratch subkey so tests are side-effect-free.
- [ ] T038 [P] [US4] Unit tests for `SupervisorDecision.ClassifyAutostart` in `tests/KbFix.Tests/Watcher/SupervisorDecisionTests.cs` — cover the 3×3 matrix of (Run key: absent/present/disabled) × (Scheduled Task: absent/present/disabled), asserting Effective / Degraded / NotRegistered outputs per the rule.

**Checkpoint**: US4 is complete — install pre-flight refuses-or-degrades per policy, `--status` answers "will it fire next logon?" faithfully, exit 17 triggers only when both mechanisms are effectively disabled.

---

## Phase 7: User Story 5 — Cooperate with multi-session machines (Priority: P2)

**Goal**: Per-user isolation of the supervisor is provable by construction — the Scheduled Task's principal is the current user's SID, the Run key lives under HKCU, the staging directory is `%LOCALAPPDATA%\KbFix`, and the named mutex / event are in the `Local\` session-scoped namespace. No machine-global writes.

**Independent Test**: Unit tests assert that every per-user surface names the current user / session. Two-user manual verification (§5 of quickstart) confirms behaviour.

### Implementation for User Story 5

- [X] T039 [P] [US5] Assertions in `ScheduledTaskRegistryTests` confirm both `<UserId>` positions carry the passed SID, the XML never contains `S-1-5-18` (SYSTEM) or `S-1-5-32-544` (Admins), the `ScheduledTaskName` constant starts with `KbFix\`, and `HighestAvailable` is absent.
- [X] T040 [P] [US5] `specs/004-watcher-resilience/multiuser-audit.md` documents every write surface with its scope + verification source, plus a read-only HKLM catalog table and grep-check instructions for PR reviewers.
- [X] T041 [US5] Quickstart §11 "Multi-user verification" added, release-checklist updated to reference it.

**Checkpoint**: US5 is complete — audit artifact shows 100% per-user isolation; unit tests enforce it; release checklist requires the manual two-user trial.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Release readiness. None of these block per-story independence; they gate shipping.

- [X] T042 [P] Release build + size check: 12,078,936 B (~11.5 MB), up ~430 KB from the 003 baseline (11.1 MB). Well under SC-006's 20 MB target (and within the ≤ 15 MB stretch target). Only the pre-existing IL2008 trim warning in System.Diagnostics.FileVersionInfo; no new warnings introduced.
- [X] T043 [P] Release test suite: 140/140 pass in release build with trimming enabled. `System.Text.Json` source-gen context flows through the trimmer cleanly.
- [X] T044 Updated `README.md` — layered autostart + Scheduled Task description, new `--status --verbose` snapshot, exit codes 15/16/17 in the exit-code reference, 004 entry in the Specification section.
- [X] T045 Updated `dist-README.txt` — install.cmd description mentions the Scheduled Task layer, troubleshooting section documents the new `--status --verbose` bug-report flow, exit-code reference extended with 15/16/17.
- [ ] T046 *(User-side — not doable from this session.)* Execute the full manual-verification gate from `quickstart.md` §§2–10 on a clean Windows 11 machine: fresh install, 003 upgrade, layer-1 verification, layer-2 kill recovery (10 trials), pathological crash loop (give-up verification), Startup-Apps-toggle probe, reboot-mode matrix (5×5), `--verbose` snapshot, uninstall, policy-degraded verification. Record any failure as a bug before shipping.
- [ ] T047 *(User-side — not doable from this session.)* Verify the inherited constitutional RDP-injection gate still passes — with 004 installed, trigger an RDP-injected layout and confirm the watcher removes it within a few seconds; confirms no regression against features 001/003.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: T001 alone, no blockers.
- **Foundational (Phase 2)**: depends on Setup. T002, T003, T004 are parallelizable [P]. T005 depends on T002+T003 (uses the new enum values). T006 depends on T002 (uses `WatcherExitReason`). T007 depends on T006.
- **US1 (Phase 3)**: depends on Phase 2. T008 and T016–T017 are parallelizable after their dependencies.
- **US2 (Phase 4)**: depends on Phase 2 **and** on T009 (the XML template it extends), but can start in parallel with US1's T010–T017 if only the `WatcherMain` edits (T020–T022) are worked first.
- **US3 (Phase 5)**: depends on Phase 2 and on T014 (probe populates ScheduledTask field that `--status` reports). Does not depend on US2's exit-reason handler for initial compile, but its tests assume T021 exists to populate `last-exit.json` with the expected reasons.
- **US4 (Phase 6)**: depends on Phase 2. Independent of US2/US3 at code-change level.
- **US5 (Phase 7)**: depends on T009 (for T039's XML assertion). Otherwise independent.
- **Polish (Phase 8)**: depends on every prior phase being green.

### User Story Dependencies

- US1, US2, US3, US4, US5 each pass Phase 2 and can progress largely independently. US2 and US3 share the `last-exit.json` surface (US2 writes, US3 reads); if work splits across developers, sequence US2 before US3's `--verbose` branch is integration-tested.

### Within Each User Story

- Pure-logic units (`SupervisorDecision`, `StartupApprovedProbe` classifier) before the unpure wrappers (`ScheduledTaskRegistry`, `WatcherDiscovery` extensions).
- Unit tests land alongside their production code and are required-green before the story is considered complete.

### Parallel Opportunities

- T002, T003, T004 in Phase 2 (three different files).
- T007 can run concurrently with T006 in a TDD-style loop.
- Within US1: T008, T010, T016, T017 are all [P] (different files).
- Within US2: T019, T023, T024 are all [P].
- Within US3: T030, T031 are [P].
- Within US4: T032 (probe), T037 (probe tests), T038 (classifier tests) are [P].
- Polish T042 and T043 can run in parallel (one size-check, one full test run).

---

## Parallel Example: Phase 2 (Foundation)

```text
# Developer A:
Task T002: Extend WatcherExitReason enum.
# Developer B (simultaneously):
Task T003: Extend InstalledState enum.
# Developer C (simultaneously):
Task T004: Add exit codes 15/16/17.
# Then all three converge and Developer A continues with T005 (WatcherInstallation extensions).
```

## Parallel Example: User Story 1

```text
# One developer can land these in parallel once T005 lands:
Task T008: Create ScheduledTaskRegistry (schtasks.exe wrapper).
Task T010: Add new InstallStep records.
Task T016: SupervisorDecision unit tests (using fakes).
Task T017: ScheduledTaskRegistry XML-generator unit tests.
```

---

## Implementation Strategy

### MVP First — just US1

1. Complete Phase 1: Setup.
2. Complete Phase 2: Foundational.
3. Complete Phase 3: US1 (Watcher survives reboots).
4. **STOP and VALIDATE**: Run `quickstart.md` §2 + §7. Confirm the 5×5 reboot-mode matrix is green.
5. This is a shippable increment — autostart is now layered and robust. Users who never crash would be fully served.

### Incremental Delivery

1. Setup + Foundational → shared plumbing in place.
2. + US1 → reboots covered → **Release Candidate 1 (MVP)**.
3. + US2 → crash recovery covered → **Release Candidate 2**.
4. + US3 → diagnostics / `--verbose` / new exit codes → **Release Candidate 3**.
5. + US4 → pre-flight effectiveness probe → **Release Candidate 4**.
6. + US5 → multi-user audit + gate → **Final Release**.
7. Polish (Phase 8) is gating, not incremental — run before every RC.

### Parallel Team Strategy

Three developers, post-foundation:

- Developer 1: US1 (Phase 3) + US5 code-audit (T040).
- Developer 2: US2 (Phase 4) → US3 (Phase 5).
- Developer 3: US4 (Phase 6) + tests.
- Merge in the order US1 → US2 → US3 → US4 → US5 so downstream stories see their upstream types.

---

## Notes

- `[P]` tasks = different files, no dependencies.
- `[Story]` label maps each task to its owning user story for traceability.
- The constitution's manual RDP-verification gate is non-negotiable (T047) and supplements the unit-test suite.
- The `last-exit.json` file format is an internal contract; do not rename fields or remove values without bumping the file's own version key in a follow-up feature.
- Per the spec's Assumption: backwards compatibility for 003 installs is required — T013's `SupervisorDecision.AppendInstallSteps` must be idempotent vs. the Run key and must not kill a running watcher unnecessarily.
- Per feature-003 SC-006: binary size ≤ 20 MB (ideally ≤ 15 MB). `System.Text.Json` source-gen (T006) is mandatory to keep trimming viable.
- Commit after each completed task, or per logical group (all Phase 2 tasks as one commit is fine; individual US tasks are better).
