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

- [ ] T018 [US2] Extend `BuildTaskXml` in `src/KbFix/Platform/Install/ScheduledTaskRegistry.cs` to include `<Settings><RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings>` per research R1; update the unit test in `tests/KbFix.Tests/Platform/ScheduledTaskRegistryTests.cs` (T017) to assert this subtree is present with the exact `PT1M` / `3` values.
- [ ] T019 [P] [US2] Implement `SupervisorDecision.ClassifySupervisor(WatcherInstallation) -> SupervisorState` in `src/KbFix/Watcher/SupervisorDecision.cs` per the derivation rule in `data-model.md` §3 (branches: `Absent` → `Disabled` → `Healthy` → `RestartPending` → `GaveUp` based on task presence + `Status` + `NextRunTime` + watcher-alive).
- [ ] T020 [US2] Register `AppDomain.CurrentDomain.UnhandledException` handler in `src/KbFix/Watcher/WatcherMain.cs` (before the mutex acquisition) that, given the `ExceptionObject`, builds a `LastExitReason { reason = CrashedUnhandled, exitCode = 1, timestampUtc = now, pid = Environment.ProcessId, detail = exception-type + first-line-message truncated to 200 bytes }` and writes it via `LastExitReasonStore.Write` before allowing the runtime to terminate the process.
- [ ] T021 [US2] In `WatcherMain.Run` in `src/KbFix/Watcher/WatcherMain.cs`, write `LastExitReason` on the existing controllable exit paths: `StopSignaled` → `reason = CooperativeShutdown, exitCode = 0`; `ConfigUnrecoverable` → `reason = ConfigUnrecoverable, exitCode = 1`; outer `catch` (startup failure) → `reason = StartupFailed, exitCode = 1, detail = failing-step`. Each write goes in the `finally`/`catch` immediately before the return, wrapped in try/catch (writing must never re-throw).
- [ ] T022 [US2] At the top of `WatcherMain.Run` in `src/KbFix/Watcher/WatcherMain.cs`, read `LastExitReasonStore.Read()` before touching the mutex; if the previous record exists with `reason != CooperativeShutdown` and `pid` no longer identifies a live process, write a new record with `reason = SupervisorObservedDead` and `detail = "previous-pid={old}"`; log one `ProcessStartup` line including the detected previous-run reason.
- [ ] T023 [P] [US2] Extend `WatcherLog` in `src/KbFix/Watcher/WatcherLog.cs` with three new INFO methods — `ProcessStartup(string previousReason)`, `SupervisorObservedDead(int previousPid)`, `AutostartFiredAtLogin(string mechanism)` — following the existing `Write(level, message)` pattern; update the `IWatcherLog` interface accordingly.
- [ ] T024 [P] [US2] Unit tests for `SupervisorDecision.ClassifySupervisor` in `tests/KbFix.Tests/Watcher/SupervisorDecisionTests.cs` — one test per state (Healthy / RestartPending / GaveUp / Disabled / Absent / Unknown), built from fake `WatcherInstallation` instances per the derivation-rule pseudocode in `data-model.md` §3.

**Checkpoint**: US2 is complete — kill the watcher, observe the task restart it within 90 s; the `last-exit.json` trail correctly distinguishes cooperative vs. crash vs. externally-killed exits.

---

## Phase 5: User Story 3 — Diagnose why the watcher is not running (Priority: P1)

**Goal**: `kbfix --status` output is self-explanatory for the three diagnostic questions from spec SC-004: "is it running right now?", "will it be running after my next sign-in?", "why did it stop last time?". New exit codes distinguish supervisor-degraded states. A `--verbose` modifier bundles enough context to paste into a bug report.

**Independent Test**: Force each of the six "not running" states from `quickstart.md` §5 (no autostart, stale-path, recent clean exit, recent crash, exceeded retry budget, first-sign-in grace) and verify the `--status` output unambiguously identifies which one applies. Exit codes match `contracts/cli.md`.

### Implementation for User Story 3

- [ ] T025 [US3] Extend `StatusReporter.Format` in `src/KbFix/Cli/StatusReporter.cs` with four new lines per `contracts/cli.md` §`--status`: `task:`, `supervisor:`, `last exit:`, `autostart effective at next logon:`; order and wording exactly per the contract. Keep the existing 003 lines in their existing positions so diff-consumers that pinned against the old format continue to see those lines unchanged.
- [ ] T026 [US3] Extend `StatusReporter.ExitCodeFor` in `src/KbFix/Cli/StatusReporter.cs` to map the three new `InstalledState` values — `SupervisorBackingOff → 15`, `SupervisorGaveUp → 16`, `AutostartDegraded → 17`. The 003 mappings (10–14) are preserved verbatim.
- [ ] T027 [US3] Add `--verbose` modifier handling to `src/KbFix/Cli/Options.cs` — field, parser case, mutual exclusion (`--verbose` + `--quiet` → exit 64; `--verbose` without `--status` → exit 64); extend `UsageText` to document the new modifier per `contracts/cli.md` §Synopsis.
- [ ] T028 [US3] In `src/KbFix/Cli/StatusReporter.cs`, implement the `--verbose` snapshot assembly — three delimited blocks: `----- watcher.log (tail) -----` (last 40 lines of `WatcherInstallation.LogFilePath`, missing-file tolerant), `----- scheduled-task.xml -----` (inline the XML from `ScheduledTaskRegistry.QueryXml`, missing-task tolerant), `----- last-exit.json -----` (pretty-print via `JsonSerializer.Serialize(... { WriteIndented = true })`, missing-file tolerant). Each block also has a matching `----- end -----` footer.
- [ ] T029 [US3] Route `options.Verbose` through `Program.RunStatus` in `src/KbFix/Program.cs` so the verbose branch calls into T028's snapshot assembly; quiet-mode path unchanged.
- [ ] T030 [P] [US3] Unit tests in `tests/KbFix.Tests/Cli/StatusReporterTests.cs` — cover the six reportable state combinations (healthy / not-running-but-pending / gave-up / disabled / stale-path / not-installed); assert each produces the correct exit code via `ExitCodeFor`. Verbose-mode output assembly is exercised via injected file/XML/JSON strings (pure; no filesystem).
- [ ] T031 [P] [US3] Unit tests in `tests/KbFix.Tests/Cli/OptionsTests.cs` — add cases for `--status --verbose` (OK), `--verbose --quiet` (exit 64), `--install --verbose` (exit 64), `--verbose` alone (exit 64). Leave every pre-existing test assertion unchanged.

**Checkpoint**: US3 is complete — `--status` and `--status --verbose` both produce the documented output for every reportable state; exit codes match the contract.

---

## Phase 6: User Story 4 — Verify autostart actually fires (Priority: P2)

**Goal**: `--install` detects, before reporting success, whether the new autostart registration will actually be *effective* at next logon — i.e. not overridden by the per-user Startup-Apps toggle or by a disabled Scheduled Task. `--status` exposes this effectiveness explicitly so the user does not have to reboot to find out.

**Independent Test**: Run the `quickstart.md` §6 scenarios — disable the Startup Apps toggle for `KbFixWatcher`; disable the Scheduled Task; observe that `--status` correctly reports `autostart effective at next logon: no` and exits 17 when both are disabled.

### Implementation for User Story 4

- [ ] T032 [P] [US4] Create `StartupApprovedProbe` in `src/KbFix/Platform/Install/StartupApprovedProbe.cs` — reads binary value `KbFixWatcher` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`; classifies as `Enabled` when the value is absent, or when byte 0 is `0x02`; classifies as `Disabled` when byte 0 is `0x03`; graceful on exceptions (return `Enabled` as a safe default, matches the Windows-Explorer semantics of "no entry = enabled"). Accept an optional `Func<RegistryKey?>` seam for unit testing.
- [ ] T033 [US4] Implement `SupervisorDecision.ClassifyAutostart(WatcherInstallation) -> AutostartEffectiveness` in `src/KbFix/Watcher/SupervisorDecision.cs` per the derivation rule in `data-model.md` §4 (combines Run-key-present + StartupApproved + task-present + task-enabled).
- [ ] T034 [US4] Extend `WatcherDiscovery.Probe` in `src/KbFix/Platform/Install/WatcherDiscovery.cs` to call `StartupApprovedProbe` and `SupervisorDecision.ClassifyAutostart`, populating the new `AutostartEffectiveness` field on `WatcherInstallation`.
- [ ] T035 [US4] Extend `InstallDecision.ComputeInstallSteps` in `src/KbFix/Watcher/InstallDecision.cs` with a pre-flight classification step — if `SupervisorDecision.ClassifySupervisor(state)` is `Disabled` after the planned install, re-enable the task by emitting an additional `schtasks /Change /ENABLE` step (either via a new `EnableScheduledTaskStep` record or by deleting-then-recreating the task); if `ClassifyAutostart` would be `Degraded`, emit a warning note in the install result that the `InstallReporter` surfaces to the user.
- [ ] T036 [US4] In `Program.RunInstall` in `src/KbFix/Program.cs`, if any step returns a degraded-but-non-fatal note (e.g. "Scheduled Task denied by policy"), exit 0 (install succeeded in Run-key-only mode) but emit a one-line caveat to stderr per `contracts/cli.md` §"Graceful degradation".
- [ ] T037 [P] [US4] Unit tests for `StartupApprovedProbe` in `tests/KbFix.Tests/Platform/StartupApprovedProbeTests.cs` — inject a registry-reader stub that returns the known binary patterns (`0x02 0x00 ... 0x00` enabled, `0x03 0x00 ... 0x00` disabled-by-user, value-absent, value-wrong-type); assert the classifier's output.
- [ ] T038 [P] [US4] Unit tests for `SupervisorDecision.ClassifyAutostart` in `tests/KbFix.Tests/Watcher/SupervisorDecisionTests.cs` — cover the 3×3 matrix of (Run key: absent/present/disabled) × (Scheduled Task: absent/present/disabled), asserting Effective / Degraded / NotRegistered outputs per the rule.

**Checkpoint**: US4 is complete — install pre-flight refuses-or-degrades per policy, `--status` answers "will it fire next logon?" faithfully, exit 17 triggers only when both mechanisms are effectively disabled.

---

## Phase 7: User Story 5 — Cooperate with multi-session machines (Priority: P2)

**Goal**: Per-user isolation of the supervisor is provable by construction — the Scheduled Task's principal is the current user's SID, the Run key lives under HKCU, the staging directory is `%LOCALAPPDATA%\KbFix`, and the named mutex / event are in the `Local\` session-scoped namespace. No machine-global writes.

**Independent Test**: Unit tests assert that every per-user surface names the current user / session. Two-user manual verification (§5 of quickstart) confirms behaviour.

### Implementation for User Story 5

- [ ] T039 [P] [US5] Unit test in `tests/KbFix.Tests/Platform/ScheduledTaskRegistryTests.cs` asserting that `BuildTaskXml(path, sid)` emits `<Principals><Principal><UserId>` exactly equal to the passed SID and never emits `S-1-5-18` (SYSTEM) or any other well-known SID when the caller passed a user SID. Also assert the task namespace used for registration (the constant string) starts with `\KbFix\` — not with `\` alone.
- [ ] T040 [P] [US5] Code-audit task (no production change — only a verification artifact): add `specs/004-watcher-resilience/multiuser-audit.md` listing every new file written by feature 004 with its path root, confirming every root resolves to per-user storage (`%LOCALAPPDATA%`, `HKCU`, `Local\`, user-namespace Task Scheduler). Reviewer gate for PR approval.
- [ ] T041 [US5] Append §5 of `quickstart.md` (two-user manual verification) to the release-checklist section at the bottom of `quickstart.md` so two-user isolation becomes a signed-off gate for every 004 release build.

**Checkpoint**: US5 is complete — audit artifact shows 100% per-user isolation; unit tests enforce it; release checklist requires the manual two-user trial.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Release readiness. None of these block per-story independence; they gate shipping.

- [ ] T042 [P] Run `build.cmd release` and confirm the published binary size remains ≤ 20 MB, ideally ≤ 15 MB (SC-006); compare to T001's baseline and investigate any growth > 0.5 MB.
- [ ] T043 [P] Run `build.cmd release --test` and confirm the entire test suite (001 + 002 + 003 + 004) passes green with zero trim warnings introduced by T006's `System.Text.Json` source-generator.
- [ ] T044 Update `README.md` Scope and Specification sections to mention 004 — layered autostart, self-restart via Scheduled Task, new `--status --verbose` snapshot, exit codes 15/16/17; keep the `dist/` wrapper section unchanged (install.cmd/uninstall.cmd/status.cmd behaviour is unchanged from the user's perspective).
- [ ] T045 Update `dist-README.txt` (or the template the build produces it from) to document the new `--status` output lines and exit codes; keep the overall voice aimed at non-technical end users.
- [ ] T046 Execute the full manual-verification gate from `quickstart.md` §§2–10 on a clean Windows 11 machine: fresh install, 003 upgrade, layer-1 verification, layer-2 kill recovery (10 trials), pathological crash loop (give-up verification), Startup-Apps-toggle probe, reboot-mode matrix (5×5), `--verbose` snapshot, uninstall, policy-degraded verification. Record any failure as a bug before shipping.
- [ ] T047 Verify the inherited constitutional RDP-injection gate still passes — with 004 installed, trigger an RDP-injected layout and confirm the watcher removes it within a few seconds; confirms no regression against features 001/003.

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
