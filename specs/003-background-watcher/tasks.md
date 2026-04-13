---
description: "Task list for 003-background-watcher"
---

# Tasks: Background Watcher, Autostart, and Slim Binary

**Input**: Design documents from `/specs/003-background-watcher/`
**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md), [data-model.md](./data-model.md), [contracts/cli.md](./contracts/cli.md), [quickstart.md](./quickstart.md)

**Tests**: Unit tests for pure logic are REQUIRED by the project constitution (§Development Workflow). End-to-end tests that touch real Win32/registry/filesystem are replaced by manual verification per `quickstart.md`.

**Organization**: Tasks are grouped by user story so each story can be implemented, tested, and demoed independently.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: different file, no dependency on an earlier incomplete task — safe to run in parallel
- **[Story]**: `[US1]`, `[US2]`, `[US3]`, `[US4]` — maps to user stories in spec.md
- Setup, Foundational, and Polish tasks have no story label
- Every task includes the exact file path

## Path conventions

Single-project C# layout (from plan.md §Project Structure). Paths are relative to repo root:

- `src/KbFix/` — production code
- `tests/KbFix.Tests/` — xUnit tests
- `specs/003-background-watcher/` — design docs

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the directory skeleton and exit-code plumbing that every later phase depends on. No behavior changes yet.

- [X] T001 Create new folders `src/KbFix/Watcher/`, `src/KbFix/Platform/Install/`, and `tests/KbFix.Tests/Watcher/` so later tasks can drop files into them without racing
- [X] T002 [P] Add new exit-code constants (`InstalledHealthy = 0`, `InstalledNotRunning = 11`, `RunningWithoutAutostart = 12`, `StalePath = 13`, `MixedOrCorrupt = 14`, and `NotInstalled = 10`) to `src/KbFix/Diagnostics/ExitCodes.cs` while preserving existing values (`Success=0`, `Failure=1`, `Unsupported=2`, `Refused=3`, `Usage=64`) exactly per contract
- [X] T003 [P] Add `FreeConsole` P/Invoke (`[DllImport("kernel32.dll")] internal static extern bool FreeConsole();`) to `src/KbFix/Platform/Win32Interop.cs` with `[SupportedOSPlatform("windows")]`

**Checkpoint**: Directories exist; exit-code surface is ready; FreeConsole is callable.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Build the shared types (entities, CLI parsing, discovery, decision function) that all three command phases (US1/US2/US3) consume. Without these, none of the user stories compile.

**⚠️ CRITICAL**: No user-story work can begin until this phase is complete.

### CLI surface

- [X] T004 Extend `src/KbFix/Cli/Options.cs` to parse the four new flags (`--install`, `--uninstall`, `--status`, `--watch`), enforce mutual exclusion with each other and with `--dry-run`, and expose them on the `Options` record; update `UsageText` to list them per `contracts/cli.md`; keep the existing `--dry-run`, `--quiet`, `--help`, `--version`, and unknown-flag-exit-64 behavior byte-identical

### Entities & decision function

- [X] T005 [P] Create `src/KbFix/Watcher/WatcherInstallation.cs` as a read-only record with fields `StagedBinaryPath`, `StagedBinaryExists`, `AutostartEntryPresent`, `AutostartEntryTarget`, `AutostartEntryPointsAtStaged`, `WatcherRunning`, `WatcherPid`, and an `InstalledState` enum member, exactly as specified in data-model.md §1
- [X] T006 [P] Create `src/KbFix/Watcher/InstallDecision.cs` as a pure static class that takes a `WatcherInstallation` snapshot + invoking-binary path + command kind (Install/Uninstall/Status) and returns an ordered list of `Step` values (`EnsureStagingDirectory`, `CopyBinaryToStaged`, `WriteRunKey`, `DeleteRunKey`, `SignalStopEvent`, `ForceKillWatcher`, `SpawnWatcher`, `DeleteStagedBinary`, `DeleteStagingDirectory`, `ReportStatus`) per data-model.md §3; contains NO I/O, no registry, no process — pure data in, pure data out
- [X] T007 [P] Create `src/KbFix/Platform/Install/WatcherDiscovery.cs` that probes the current `WatcherInstallation` state: opens `Local\KbFixWatcher.Instance` mutex via `Mutex.TryOpenExisting`, reads `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\KbFixWatcher`, checks `%LOCALAPPDATA%\KbFix\kbfix.exe`, reads `%LOCALAPPDATA%\KbFix\watcher.pid`, and returns a fully populated `WatcherInstallation` record; this is the one component all three commands call to see the world

### Tests for decision function (REQUIRED — pure logic, per constitution)

- [X] T008 [P] Create `tests/KbFix.Tests/Watcher/InstallDecisionTests.cs` covering the full decision table in data-model.md §3 (9+ scenarios: NotInstalled→Install, InstalledHealthy-same-path→Install, InstalledHealthy-different-path→Install, StalePath→Install, RunningWithoutAutostart→Install, InstalledHealthy→Uninstall, NotInstalled→Uninstall, MixedOrCorrupt→Uninstall, any→Status) — each case asserts the exact ordered step list the `InstallDecision` function emits

### Program dispatch skeleton

- [X] T009 Modify `src/KbFix/Program.cs` to branch on the new `Options` fields (`Install`, `Uninstall`, `Status`, `Watch`) BEFORE the existing one-shot flow begins; for now each new branch returns a stub `NotImplementedException` that will be filled in by later phases; the existing one-shot path (when none of the new flags is set) must remain byte-identical and still run under `[STAThread]`

**Checkpoint**: Program compiles. Decision function is unit-tested and passing. Discovery can probe a live machine. US1/US2/US3 can now be implemented in parallel against these shared types.

---

## Phase 3: User Story 1 — Install and forget (Priority: P1) 🎯 MVP

**Goal**: `kbfix --install` stages the binary, registers HKCU\Run, launches a detached watcher, and the watcher reliably removes RDP-injected layouts within a few seconds. The watcher survives reboot and starts automatically at the next login.

**Independent test**: Run `kbfix --install` in a fresh user session. Confirm `kbfix --status` reports `InstalledHealthy` (once US3 lands — until then, verify manually via `tasklist`, `reg query`, and `%LOCALAPPDATA%\KbFix\`). Open RDP, disconnect, and confirm stray layouts disappear within a few seconds. Reboot, log back in, confirm the watcher is running again. No admin prompt at any point.

### Watcher loop — pure logic (unit-tested)

- [X] T010 [P] [US1] Create `src/KbFix/Watcher/ISessionReconciler.cs` defining a minimal interface with two methods: `ReconcileOnce()` returning `(int actionsApplied, bool refused, string? failureReason)`, and `Dispose()`. This is the seam the `WatcherLoop` is unit-tested against
- [X] T011 [P] [US1] Create `src/KbFix/Watcher/FlapDetector.cs` implementing the sliding-window counter described in data-model.md §4 (`Window=60s`, `Threshold=10`, `PauseDuration=5min`), with an injected `Func<DateTimeOffset>` clock so tests can drive it deterministically
- [X] T012 [P] [US1] Create `tests/KbFix.Tests/Watcher/FlapDetectorTests.cs` covering: (a) under threshold → never pauses, (b) exactly threshold within window → pauses for 5 minutes, (c) pause expires → detector accepts new events, (d) events older than window are evicted, (e) `Record` is idempotent at the microsecond scale
- [X] T013 [P] [US1] Create `src/KbFix/Watcher/WatcherLog.cs` that writes timestamped (ISO-8601 UTC) single-line events to `%LOCALAPPDATA%\KbFix\watcher.log`, rotates to `watcher.log.1` when the file exceeds 64 KB (one-deep rotation), and exposes methods `Start`, `Stop`, `ReconcileNoOp`, `ReconcileApplied(int count)`, `ReconcileFailed(string reason)`, `FlapBackoff`, `ConfigReadFailed`, `SessionEmptyRefused` matching the table in quickstart.md §9
- [X] T014 [US1] Create `src/KbFix/Watcher/WatcherLoop.cs`: the pure poll-and-reconcile loop parameterized over `ISessionReconciler`, `FlapDetector`, `IWatcherLog`, `IClock`, `EventWaitHandle stopEvent`; idle-backoff logic (2 s → 5 s → 10 s after consecutive no-ops, snap back to 2 s on any non-no-op); sleep is `stopEvent.WaitOne(pollInterval)` so the stop signal interrupts immediately; no Win32, no filesystem, no registry — depends on T010, T011, T013
- [X] T015 [P] [US1] Create `tests/KbFix.Tests/Watcher/WatcherLoopTests.cs` covering: (a) loop applies reconciliation each tick against a fake `ISessionReconciler`, (b) consecutive no-ops stretch the poll interval, (c) a non-no-op snaps the interval back to 2 s, (d) the flap detector pauses the loop after the threshold, (e) signaling the stop event exits the loop within one interval, (f) an `ISessionReconciler` exception is logged and the loop continues, (g) repeated config-read failures beyond the grace period exit the loop cleanly

### Watcher entry point and reconciliation adapter

- [X] T016 [US1] Create `src/KbFix/Watcher/SessionReconciler.cs` that implements `ISessionReconciler` by composing the existing `PersistedConfigReader.ReadRaw` + `SessionLayoutGateway.ResolvePersisted` + `SessionLayoutGateway.ReadSession` + `ReconciliationPlan.Build` + `SessionLayoutGateway.Apply` + `SessionLayoutGateway.VerifyConverged` flow — this is a direct translation of the existing one-shot code path in `src/KbFix/Program.cs:44-146` into a reusable service; `Dispose()` disposes the underlying `SessionLayoutGateway`
- [X] T017 [US1] Create `src/KbFix/Watcher/WatcherMain.cs` — the `--watch` entry point: defensively calls `Win32Interop.FreeConsole()`; acquires `Mutex.OpenExisting` or creates `Local\KbFixWatcher.Instance` with `initiallyOwned: true`; on `AbandonedMutexException` or already-held it logs "already running" and exits 0; opens/creates `Local\KbFixWatcher.StopEvent` (manual-reset); writes PID to `%LOCALAPPDATA%\KbFix\watcher.pid`; constructs `SessionReconciler`, `FlapDetector`, `WatcherLog`, `WatcherLoop` and runs it; on shutdown deletes the PID file, releases the mutex, returns 0; on unrecoverable error returns `ExitCodes.Failure`; depends on T014, T016

### Install executors (Win32/registry/filesystem)

- [X] T018 [P] [US1] Create `src/KbFix/Platform/Install/BinaryStaging.cs` with `EnsureStagingDirectory()`, `CopyBinaryToStaged(string sourcePath)`, and `GetStagedBinaryPath()` — target directory is `%LOCALAPPDATA%\KbFix\`, target file is `kbfix.exe`; copies are overwriting and best-effort atomic (`File.Copy(src, dst, overwrite: true)`)
- [X] T019 [P] [US1] Create `src/KbFix/Platform/Install/AutostartRegistry.cs` with `TryReadRunKey()` (returns the current value data for `HKCU\...\Run\KbFixWatcher` or null), `WriteRunKey(string stagedBinaryPath)` (writes `"<path>" --watch` as `REG_SZ`), `DeleteRunKey()`, and `RunKeyTargetMatches(string stagedBinaryPath)` for idempotent detection; uses `Microsoft.Win32.Registry` with no new dependencies
- [X] T020 [P] [US1] Create `src/KbFix/Platform/Install/WatcherLauncher.cs` with `SpawnDetached(string stagedBinaryPath)` that invokes `Process.Start` with `UseShellExecute=false`, `CreateNoWindow=true`, no redirected streams, no `WaitForExit`, `WorkingDirectory` set to the staging directory, and `Arguments = "--watch"`; disposes the returned `Process` handle immediately
- [X] T021 [US1] Create `src/KbFix/Platform/Install/InstallExecutor.cs` with a method `Apply(IReadOnlyList<InstallDecision.Step> steps, StepContext ctx)` that executes each step in order using `BinaryStaging`, `AutostartRegistry`, `WatcherLauncher`, and a `StepContext` carrying the invoking binary path; for US1 only the *install* step kinds (`EnsureStagingDirectory`, `CopyBinaryToStaged`, `WriteRunKey`, `SpawnWatcher`) must be wired up — stub the uninstall/status step kinds with `NotImplementedException` (US2 will wire them); each applied step is reported back to the caller as a small result record so `Program.cs` can emit the stdout contract

### Program wiring for --install and --watch

- [X] T022 [US1] Fill in the `--install` branch of `src/KbFix/Program.cs`: resolve the invoking binary path (`Environment.ProcessPath`), call `WatcherDiscovery.Probe()`, call `InstallDecision.ComputeInstallSteps`, pass the result to `InstallExecutor.Apply`, then emit the stdout report from `contracts/cli.md` §`--install` (staged / autostart / watcher lines + final "Installed." or "Already installed."); `--quiet` suppresses the indented lines; exit code 0 on success, `ExitCodes.Failure` on any step failure; depends on T004, T007, T006, T018, T019, T020, T021
- [X] T023 [US1] Fill in the `--watch` branch of `src/KbFix/Program.cs`: call `WatcherMain.Run()` and return its exit code; depends on T017
- [X] T024 [US1] Update `src/KbFix/Cli/Options.cs` `UsageText` to document the new `--install` and `--watch` flags (the others will be documented alongside their story), matching the contract in `contracts/cli.md`

### Manual verification (required by constitution)

- [ ] T025 [US1] **USER-DRIVEN**: Manual verification per quickstart.md §2 and §3. Requires a real interactive Windows 11 session + RDP client — cannot be executed from the implementation agent's environment. Run `build.cmd release` on your machine, run the produced `kbfix.exe --install`, verify `%LOCALAPPDATA%\KbFix\kbfix.exe` exists, verify `HKCU\Run\KbFixWatcher` points at it, verify the watcher process is running (`tasklist`), verify `%LOCALAPPDATA%\KbFix\watcher.log` has a `start` line, open and disconnect an RDP session, verify a stray layout disappears within a few seconds, reboot, verify the watcher restarted at login. Run `kbfix --uninstall` afterwards to clean up.

**Checkpoint**: User Story 1 is fully functional. The tool installs, the watcher runs, RDP injections are fixed automatically, and autostart survives reboots. US1 can be shipped as the MVP even before US2, US3, or US4 land.

---

## Phase 4: User Story 2 — Clean uninstall (Priority: P1)

**Goal**: `kbfix --uninstall` stops any running watcher belonging to the current user, removes the Run key, deletes the staged binary, and leaves the system in the `NotInstalled` state. Idempotent — safe to run when nothing is installed.

**Independent test**: Starting from the US1-installed state, run `kbfix --uninstall`. Confirm the watcher process is gone, `HKCU\Run\KbFixWatcher` is absent, `%LOCALAPPDATA%\KbFix\` is gone, and a reboot does not bring the watcher back. Then run `kbfix --uninstall` a second time and confirm it reports "Nothing to uninstall" with exit 0.

### Uninstall primitives

- [X] T026 [P] [US2] Extend `src/KbFix/Platform/Install/WatcherDiscovery.cs` with `SignalStopEvent(TimeSpan waitForShutdown)` that opens `Local\KbFixWatcher.StopEvent` via `EventWaitHandle.TryOpenExisting`, calls `Set()`, then waits up to `waitForShutdown` for `Local\KbFixWatcher.Instance` to become unowned (poll `Mutex.TryOpenExisting` with a short interval), returning `true` if the mutex was released, `false` on timeout
- [X] T027 [P] [US2] Add `ForceKillWatcher(int pid, string expectedModulePath)` to `src/KbFix/Platform/Install/WatcherDiscovery.cs` that looks up the process by PID, confirms its `MainModule.FileName` matches `expectedModulePath` (safety check — never kill an arbitrary PID from a stale file), then calls `Process.Kill(entireProcessTree: false)`; returns false silently if the process is already gone
- [X] T028 [P] [US2] Extend `src/KbFix/Watcher/WatcherMain.cs` to register a handler that calls `stopEvent.Set()` on `Console.CancelKeyPress` / `AppDomain.CurrentDomain.ProcessExit` so a kill-by-signal still triggers clean shutdown; ensures T026's cooperative-stop path works from both `--uninstall` and external tooling

### Wire uninstall steps into the executor

- [X] T029 [US2] Replace the `NotImplementedException` stubs for uninstall steps in `src/KbFix/Platform/Install/InstallExecutor.cs` with real implementations: `SignalStopEvent` calls T026; `ForceKillWatcher` calls T027 (only if T026 returned false); `DeleteRunKey` calls `AutostartRegistry.DeleteRunKey`; `DeleteStagedBinary` deletes `%LOCALAPPDATA%\KbFix\kbfix.exe` unless it is the currently-running exe (in which case skip with a note in the step result); `DeleteStagingDirectory` deletes `%LOCALAPPDATA%\KbFix\` if empty (best-effort, swallow `IOException`); also deletes `watcher.pid` and both log files as part of the staging-directory cleanup
- [X] T030 [US2] Fill in the `--uninstall` branch of `src/KbFix/Program.cs`: resolve the invoking binary path, call `WatcherDiscovery.Probe()`, call `InstallDecision.ComputeUninstallSteps`, pass to `InstallExecutor.Apply`, emit the stdout report from `contracts/cli.md` §`--uninstall` (watcher / autostart / staged lines + final "Uninstalled." or "Nothing to uninstall."); `--quiet` suppresses the indented lines; exit 0 on clean uninstall and on already-empty, `ExitCodes.Failure` on partial failure; depends on T007, T026, T027, T029

### Update option help

- [X] T031 [P] [US2] Extend `src/KbFix/Cli/Options.cs` `UsageText` with the `--uninstall` documentation line from `contracts/cli.md` §`--uninstall`

### Manual verification

- [ ] T032 [US2] **USER-DRIVEN**: Manual verification per quickstart.md §6. From the US1-installed state, run `kbfix --uninstall`, verify watcher is gone, Run key is gone, `%LOCALAPPDATA%\KbFix\` is gone; run `kbfix --uninstall` again and confirm "Nothing to uninstall" exit 0; reboot and confirm watcher does NOT start. Cannot be executed from the implementation agent's environment.

**Checkpoint**: Users can install and cleanly uninstall. US1 + US2 together form a complete install/uninstall cycle.

---

## Phase 5: User Story 3 — Inspect current state (Priority: P2)

**Goal**: `kbfix --status` prints a human-readable snapshot of the installed state for the current user and returns a distinct exit code for each classification, so both humans and scripts can tell "healthy," "partially installed," and "not installed" apart.

**Independent test**: Exercise `kbfix --status` in each of: (a) fresh state → `NotInstalled` (code 10), (b) after `--install` → `InstalledHealthy` (code 0), (c) after killing the watcher in Task Manager → `InstalledNotRunning` (code 11), (d) after deleting the Run key by hand while watcher is running → `RunningWithoutAutostart` (code 12), (e) after corrupting the Run key to point at a nonexistent path → `StalePath` (code 13).

- [X] T033 [P] [US3] Create `src/KbFix/Cli/StatusReporter.cs` with a pure function `Format(WatcherInstallation state, bool quiet) -> string` that emits the exact text blocks from `contracts/cli.md` §`--status` for each of the six `InstalledState` values, including the `log:` line pointing at `%LOCALAPPDATA%\KbFix\watcher.log`
- [X] T034 [P] [US3] Create `tests/KbFix.Tests/Watcher/StatusReporterTests.cs` covering each `InstalledState` value: snapshot-test the output string against the exact examples in `contracts/cli.md` §`--status`, verify `--quiet` suppresses the indented lines, verify `State: <name>` appears on the final line for scripting
- [X] T035 [US3] Fill in the `--status` branch of `src/KbFix/Program.cs`: call `WatcherDiscovery.Probe()`, pass the result to `StatusReporter.Format`, write to stdout, and return the exit code that matches the `InstalledState` (0/10/11/12/13/14) per `contracts/cli.md` §`--status`; depends on T007, T033
- [X] T036 [P] [US3] Extend `src/KbFix/Cli/Options.cs` `UsageText` with the `--status` documentation line from `contracts/cli.md`
- [X] T037 [US3] Manual verification per quickstart.md §4: drive each of the five state scenarios above and confirm the printed output matches `contracts/cli.md` and the exit code matches the table; verify `kbfix --status --quiet` prints only the `State: <name>` line. **Partially verified by implementation agent**: `--status` was smoke-tested from the build directory and produced `State: NotInstalled` with exit code 10, matching `contracts/cli.md`. The other four states (InstalledHealthy, InstalledNotRunning, RunningWithoutAutostart, StalePath) require the user to actually `--install`, kill the watcher, etc. on their own session — covered by the user-driven T025 / T032 / T046 loop.

**Checkpoint**: All three commands work. Users can install, inspect, and uninstall. The feature's core behavior is complete; US4 is size-only.

---

## Phase 6: User Story 4 — Smaller download (Priority: P2)

**Goal**: The published `kbfix.exe` shrinks from 80+ MB to ≤20 MB (ideally <15 MB) without breaking any prior behavior.

**Independent test**: Run `build.cmd release`, measure the produced binary, copy it to a clean Windows 11 machine with no developer tools, and run all subcommands (one-shot, `--dry-run`, `--install`, `--status`, `--uninstall`). Everything works.

- [X] T038 [US4] Modify `src/KbFix/KbFix.csproj` to add the trim + size-knob property group from research.md §R6: `PublishTrimmed=true`, `TrimMode=full`, `DebuggerSupport=false`, `StackTraceSupport=false`, `EventSourceSupport=false`, `HttpActivityPropagationSupport=false`, `UseSystemResourceKeys=true`, `MetadataUpdaterSupport=false`; keep existing `PublishSingleFile`, `SelfContained`, `IncludeNativeLibrariesForSelfExtract`, `InvariantGlobalization` unchanged
- [X] T039 [US4] Replace the `Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()` call at `src/KbFix/Program.cs:34` with a trim-safe alternative (e.g. `typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"`) so the trimmed build produces zero trim warnings
- [X] T040 [US4] Run `build.cmd release`, measure the size of the produced `kbfix.exe`, and confirm it is ≤20 MB; if above the target, inspect `dotnet publish` output for retained trim warnings and add `[DynamicDependency]` on `TsfInterop` COM interface types only where a retained method is actually required by a failed run (do not over-annotate speculatively). **Result: 11,644,453 bytes ≈ 11.1 MB** — comfortably under the 20 MB ceiling and under the <15 MB stretch target. Roughly a 6.7× reduction from the 80+ MB baseline. Trim publish emitted one harmless warning (`IL2008` in System.Diagnostics.FileVersionInfo's substitution XML — referencing an unused resource key, no functional impact). Smoke-tested the trimmed binary with `--version`, `--help`, `--status`, and `--dry-run` against the real user session — all work; TSF COM interop + Win32 P/Invoke all resolved correctly under `TrimMode=full`. No `[DynamicDependency]` annotations were required.
- [X] T041 [US4] Run the entire existing `tests/KbFix.Tests` suite against a non-trimmed `dotnet test` build to confirm SC-008 (all prior tests continue to pass); this uses the debug build, not the release build, so trimming does not affect it — this task is just a regression gate. **Result: 72/72 passing** (18 original 001 tests + 5 existing Options + ~16 InstallDecision + 7 FlapDetector + 7 WatcherLoop + 7 StatusReporter + misc).
- [ ] T042 [US4] **USER-DRIVEN**: Clean-machine verification per quickstart.md §1. Copy the trimmed `kbfix.exe` (under `dist/`) to a Windows 11 host with no .NET runtime or developer tools, run `kbfix`, `kbfix --dry-run`, `kbfix --install`, `kbfix --status`, and `kbfix --uninstall`, and confirm all five commands work and produce the expected stdout from `contracts/cli.md`. Cannot be executed from the implementation agent's environment (the current dev machine has the .NET SDK installed so it is not a valid proxy for a clean target).

**Checkpoint**: All four user stories complete. A user on any Windows 11 machine can download a ≤20 MB `kbfix.exe`, run `--install`, and never think about keyboard layouts again.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: One-shot regression gate, docs, and final cleanup.

- [X] T043 [P] Regression-test the byte-for-byte-identical one-shot path from quickstart.md §8: run `kbfix`, `kbfix --dry-run`, `kbfix --quiet`, `kbfix --help`, `kbfix --version` and compare stdout to the 001 CLI contract (`specs/001-fix-keyboard-layouts/contracts/cli.md`); any divergence is a regression and must be fixed. **Result: regression caught and fixed.** The original Phase 6 trim-safe version change produced `kbfix 0.1.0.0` (four-part AssemblyName.Version), which violated the 001 "semver" contract. Fixed by switching to `Assembly.GetName().Version.ToString(3)` which emits `kbfix 0.1.0`. All other one-shot outputs match the 001 contract verbatim (Persisted/Session/Actions/Result four-section format, `Result: NO-OP — session already matches persisted set.`, `Result: DRY-RUN: no changes needed.`, `--quiet` suppresses Persisted/Session sections).
- [X] T044 [P] Update `src/KbFix/Cli/Options.cs` `UsageText` so `--help` shows all new flags together in a coherent block (prior tasks added them individually — this task just audits the text for consistency and ordering per `contracts/cli.md` §Synopsis). **Audited**: UsageText presents the Synopsis as four `kbfix ...` lines (one-shot + `--install` + `--uninstall` + `--status`) and groups options under "One-shot options:" and "Background-watcher commands (per-user, no elevation):", matching the shape of `contracts/cli.md` §Synopsis. `--watch` is intentionally omitted from the help block because the contract designates it as internal/not-user-facing.
- [X] T045 [P] Update README.md (if it exists and mentions CLI usage) to advertise the new `--install` / `--uninstall` / `--status` flags and the ~15 MB binary; skip this task entirely if README.md does not cover CLI usage. **Done**: `README.md` now documents the background-watcher commands (install/status/uninstall), the new `--status` exit codes 10–14, and the shrunken ~12 MB binary. The prior "v1 scope" paragraph that said "no background watcher" was rewritten to describe the new two-mode (one-shot + watcher) scope.
- [ ] T046 **USER-DRIVEN**: Full quickstart.md walkthrough. Execute every section §1 through §9 end-to-end on a fresh Windows 11 machine, confirming every assertion (size target, install, RDP verification, status states, reinstall, uninstall, idempotency, one-shot regression, troubleshooting log entries). Cannot be executed from the implementation agent's environment — requires a real interactive user session, a real RDP round trip, and (ideally) a clean Windows 11 host for the "no runtime installed" portion. Any failure here is a release blocker.

---

## Dependencies & Execution Order

### Phase order

1. **Phase 1 (Setup)** — no dependencies. Start immediately.
2. **Phase 2 (Foundational)** — depends on Phase 1. **Blocks** all user-story phases.
3. **Phase 3 (US1)** — depends on Phase 2. MVP.
4. **Phase 4 (US2)** — depends on Phase 2. Can start in parallel with Phase 3 if two developers are available, but practical ordering is Phase 3 first because US2 manually verifies the uninstall of a state produced by US1.
5. **Phase 5 (US3)** — depends on Phase 2. Can run in parallel with Phase 3 / Phase 4 once Foundational is done, because `WatcherDiscovery` is the only shared runtime dependency and it lives in Phase 2.
6. **Phase 6 (US4)** — depends on Phases 3, 4, 5 being code-complete (not necessarily manually verified) because the trimmed build must exercise all code paths at runtime to catch trim-removed methods. Concretely: run T038–T042 after T011–T037 have all landed, so a single trimmed build validates everything at once.
7. **Phase 7 (Polish)** — depends on all prior phases.

### Within each phase

- Tasks marked `[P]` touch different files AND have no dependency on any other incomplete task in the same phase — they can be done in parallel.
- Tasks without `[P]` either touch the same file as a sibling task or depend on a prior task's output.

### Critical chains

- **Program.cs dispatch chain**: T004 → T009 → {T022, T023 (US1), T030 (US2), T035 (US3)}. All four mode branches land in the same file, so they are sequenced not parallel.
- **InstallExecutor chain**: T021 (US1, install steps) → T029 (US2, uninstall steps). Same file, extended twice.
- **WatcherDiscovery chain**: T007 (US0 Foundational) → T026 + T027 (US2 extensions). Same file, extended.
- **Options.cs chain**: T004 → T024 (US1) → T031 (US2) → T036 (US3) → T044 (Polish). Same file, `UsageText` grows as each story lands.

---

## Parallel opportunities

### Phase 1 (Setup)

- T002 and T003 after T001 → parallel.

### Phase 2 (Foundational)

- T005, T006, T007 → parallel (three different files).
- T008 depends on T006, so it runs after. It is alone in its file and has no other blockers within this phase.
- T004 and T009 are sequential (T009 needs the parsed fields T004 exposes).

### Phase 3 (US1)

- T010, T011, T013, T018, T019, T020 → all parallel (six different files).
- T012 can start as soon as T011 lands.
- T015 can start as soon as T014 lands.
- T014 depends on T010 + T011 + T013.
- T016 depends on T010 + the existing 001 reconciliation code (no new task for that).
- T017 depends on T014 + T016.
- T021 depends on T018 + T019 + T020 + T006 + T007.
- T022 depends on T021 + T007 + T006 + T004.
- T023 depends on T017 + T004.

```text
US1 critical path:  T010/T011/T013  →  T014  →  T017  →  T023
                                                \
                                                 T016 ↗
US1 install path:   T018/T019/T020  →  T021  →  T022
```

### Phase 4 (US2)

- T026, T027, T028, T031 → parallel.
- T029 depends on T026 + T027 + T021 (the latter is from US1).
- T030 depends on T029 + T007.

### Phase 5 (US3)

- T033, T034, T036 → parallel.
- T035 depends on T033 + T007.

### Phase 6 (US4)

- T038 and T039 → parallel (two different files).
- T040, T041, T042 → sequential (they each measure/verify the output of the previous).

### Phase 7 (Polish)

- T043, T044, T045 → parallel.
- T046 is the final gate.

---

## Implementation Strategy

### MVP path (ship US1 only)

1. Phase 1 (T001–T003)
2. Phase 2 (T004–T009)
3. Phase 3 / US1 (T010–T025)
4. Stop and validate against the US1 independent test.
5. Optional: also land Phase 6 / US4 (T038–T042) for a smaller MVP binary, though shipping an 80 MB MVP is acceptable if the size work is deferred.

Users can `--install` and enjoy auto-fixing RDP injections. They cannot `--uninstall` or `--status` yet (those branches in `Program.cs` still throw `NotImplementedException`), so the MVP documentation should tell them to use Task Manager + `reg delete` if they need to back out.

### Incremental delivery

1. **Release 1**: Phase 1 + 2 + 3 (US1). MVP — install works.
2. **Release 2**: + Phase 4 (US2). Full install/uninstall cycle.
3. **Release 3**: + Phase 5 (US3). Status command for troubleshooting.
4. **Release 4**: + Phase 6 (US4). Shrunk binary.
5. **Release 5**: + Phase 7. Polished, documented, regression-verified.

Or, if a single developer is implementing everything, just go top-to-bottom: Phase 1 → 2 → 3 → 4 → 5 → 6 → 7. Same result, no release cadence.

### Parallel team strategy

After Phase 2 completes:

- Developer A: Phase 3 (US1) — the watcher + install path.
- Developer B: Phase 4 (US2) — the uninstall path (blocked on T021 from US1 for the `InstallExecutor` file; until then, B can scaffold T026, T027, T028).
- Developer C: Phase 5 (US3) — the status command (fully independent).

Then a single developer owns Phases 6 and 7.

---

## Notes

- `[P]` means different file, no dependency on an earlier incomplete task.
- `[Story]` label maps each task to its user story for traceability.
- Unit tests are required for pure logic only (constitution §Development Workflow). Win32/registry/filesystem paths are verified manually via `quickstart.md`.
- Commit after each task or logical group. Never skip hooks or bypass signing unless the user explicitly asks.
- Do not add comments to code for the purposes of tracking feature provenance ("added for 003-background-watcher") — such comments rot. PR descriptions and commit messages are where that history lives.
- The existing one-shot path is sacred: any regression discovered during Phase 7 T043 is a release blocker and must be fixed before shipping.
