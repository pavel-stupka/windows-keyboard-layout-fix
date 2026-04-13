---
description: "Task list for feature 001-fix-keyboard-layouts"
---

# Tasks: Fix Windows Session Keyboard Layouts

**Input**: Design documents from `/specs/001-fix-keyboard-layouts/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/cli.md, quickstart.md

**Tests**: Unit tests are INCLUDED. The project constitution requires unit
tests for the pure reconciliation logic, and the plan formalises this with
xUnit. Tests for pure-domain logic and CLI option parsing are part of the
relevant story phase. End-to-end Windows-API tests are NOT included; manual
RDP verification (per `quickstart.md`) is mandatory before release.

**Organization**: Tasks are grouped by user story so each story can be
implemented, tested, and demoed independently.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- File paths are absolute from repo root

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the .NET solution, the single application project, and the test project. No domain code yet.

- [X] T001 Create the solution file `KbFix.sln` at repo root and add empty projects `src/KbFix/KbFix.csproj` (Console App, net8.0, win-x64) and `tests/KbFix.Tests/KbFix.Tests.csproj` (xUnit). Wire the test project to reference `src/KbFix/KbFix.csproj`.
- [X] T002 [P] In `src/KbFix/KbFix.csproj` set `<TargetFramework>net8.0</TargetFramework>`, `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, `<OutputType>Exe</OutputType>`, `<Nullable>enable</Nullable>`, `<LangVersion>12</LangVersion>`, `<PublishSingleFile>true</PublishSingleFile>`, `<SelfContained>true</SelfContained>`, `<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>`, `<AssemblyName>kbfix</AssemblyName>`, `<RootNamespace>KbFix</RootNamespace>`.
- [X] T003 [P] In `tests/KbFix.Tests/KbFix.Tests.csproj` add the `xunit`, `xunit.runner.visualstudio`, and `Microsoft.NET.Test.Sdk` package references; set `<TargetFramework>net8.0</TargetFramework>` and `<Nullable>enable</Nullable>`.
- [X] T004 [P] Add a root `.editorconfig` enforcing 4-space indent, `file_scoped_namespaces = true`, and `dotnet_diagnostic.CS8618.severity = error` (non-nullable init enforcement).

**Checkpoint**: `dotnet build` succeeds at the repo root with two empty projects.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Pure value types, exit-code constants, and the platform interop declarations and read-side gateways. These are needed by every user story (US1, US2, US3 all consume the same domain types and the same platform reads).

**⚠️ CRITICAL**: No user-story phase may start until this phase is complete.

- [X] T005 [P] Create `src/KbFix/Diagnostics/ExitCodes.cs` with public const ints `Success = 0`, `Failure = 1`, `Unsupported = 2`, `Refused = 3`, `Usage = 64`, exactly matching `contracts/cli.md`.
- [X] T006 [P] Create `src/KbFix/Domain/LayoutId.cs` as a `readonly record struct LayoutId(ushort LangId, string Klid, Guid? ProfileGuid)` with: validation in a static factory `Create(...)` (LangId != 0, Klid is exactly 8 hex digits), value-equality on all three fields, a stable `Compare(LayoutId other)` ordering by `(LangId, Klid)`, and a `ToString()` returning `"{LangId:X4} {Klid}"`.
- [X] T007 [P] Create `src/KbFix/Domain/LayoutSet.cs` as an immutable wrapper around `IReadOnlySet<LayoutId>` with methods `Difference(LayoutSet other) : LayoutSet`, `Contains(LayoutId id) : bool`, `Count : int`, and an `IEnumerable<LayoutId> Sorted()` returning items sorted via `LayoutId.Compare`. Constructor must defensively copy.
- [X] T008 [P] [US1] Add `tests/KbFix.Tests/Domain/LayoutSetTests.cs` covering: empty set, duplicate-id rejection in factory, `Difference` symmetry edge cases, `Contains` true/false, and `Sorted()` deterministic ordering.
- [X] T009 Create `src/KbFix/Platform/Win32Interop.cs` with `[DllImport("user32.dll")]` declarations for `GetKeyboardLayoutList(int nBuff, IntPtr[] lpList)`, `LoadKeyboardLayout(string pwszKLID, uint Flags)`, `UnloadKeyboardLayout(IntPtr hkl)`, `ActivateKeyboardLayout(IntPtr hkl, uint Flags)`. Mark the file `internal static`. No business logic.
- [X] T010 Create `src/KbFix/Platform/TsfInterop.cs` with the COM interop declarations needed for `ITfInputProcessorProfileMgr` (`EnumProfiles`, `ActivateProfile`, `DeactivateProfile`, `GetActiveProfile`) and `ITfInputProcessorProfiles` (`ChangeCurrentLanguage`). Include the `TF_INPUTPROCESSORPROFILE` struct, the `GUID_TFCAT_TIP_KEYBOARD` category constant, and the `TF_IPP_FLAG_ACTIVE` flag. Mark the file `internal static`. No business logic. **Implementation note**: Both TSF interfaces are declared and the code attempts the manager interface first; on the dev machine it falls through to the legacy `GetKeyboardLayoutList` path because the manager IID returned `E_NOINTERFACE` here. A new `KeyboardLayoutCatalog` was added to map HKLs ↔ KLIDs via `HKLM\…\Keyboard Layouts`.
- [X] T011 Create `src/KbFix/Platform/PersistedConfigReader.cs` with `public static PersistedConfig Read()` that opens `HKCU\Keyboard Layout\Preload` (read-only via `Microsoft.Win32.Registry.CurrentUser.OpenSubKey(..., writable: false)`), reads each value as a 4-digit-hex KLID string, applies `HKCU\Keyboard Layout\Substitutes` overrides, builds a `LayoutSet`, and returns a `PersistedConfig { Layouts = set, ReadAt = DateTime.UtcNow }`. Throws `PlatformNotSupportedException` if `Preload` is missing.

**Checkpoint**: Foundation ready — every user story can now proceed in parallel against this layer. The `Domain/` folder has zero references to `System.Runtime.InteropServices` or `Microsoft.Win32`, keeping it pure-and-testable.

---

## Phase 3: User Story 1 — One-shot session cleanup (Priority: P1) 🎯 MVP

**Goal**: A user double-clicks `kbfix.exe`, and any session-only keyboard layouts are removed while persisted ones stay. Single happy path, no flags.

**Independent Test**: With an RDP-polluted session, run `kbfix.exe`, observe the session layout list shrink to match `HKCU\Keyboard Layout\Preload`, and verify exit code 0.

### Domain (pure, unit-tested) for User Story 1

- [X] T012 [P] [US1] Create `src/KbFix/Domain/PersistedConfig.cs` as a `sealed record PersistedConfig(LayoutSet Layouts, DateTime ReadAt)` with no behaviour beyond holding values.
- [X] T013 [P] [US1] Create `src/KbFix/Domain/SessionState.cs` as a `sealed record SessionState(LayoutSet Layouts, LayoutId ActiveLayout, DateTime ReadAt)` with a constructor-time invariant `Layouts.Contains(ActiveLayout)` (throw `ArgumentException` otherwise).
- [X] T014 [P] [US1] Create `src/KbFix/Domain/Action.cs` defining `enum ActionKind { SwitchActive, Deactivate }` and `sealed record PlannedAction(ActionKind Kind, LayoutId LayoutId)` plus `sealed record AppliedAction(PlannedAction Planned, bool Succeeded, string? Failure)`.
- [X] T015 [US1] Create `src/KbFix/Domain/ReconciliationPlan.cs` with a static factory `Build(PersistedConfig persisted, SessionState session) : ReconciliationPlan` implementing exactly the rules in `data-model.md` §ReconciliationPlan: refuse on empty persisted; compute `toRemove = session.Layouts.Difference(persisted.Layouts)` sorted; if `toRemove` contains `session.ActiveLayout`, find a fallback (first persisted layout that is also in session, sorted), set `MustSwitchFirst`; refuse with reason if no fallback exists; set `NoOp` when `toRemove` is empty. Expose `IReadOnlyList<PlannedAction> Actions` derived from `MustSwitchFirst` (if any) followed by Deactivate actions in `toRemove` order.
- [X] T016 [P] [US1] Create `tests/KbFix.Tests/Domain/ReconciliationPlanTests.cs` with one `[Fact]` per branch: (a) no-op when sets equal, (b) simple removal when extras present and active layout is persisted, (c) switch-first when active layout is in removal set and a persisted fallback exists, (d) refuse when persisted set is empty, (e) refuse when no persisted fallback in session, (f) idempotency: feeding the post-apply state back into `Build` yields a no-op plan, (g) deterministic action ordering. Each test asserts on `NoOp`, `Refused`, `Actions`, and `MustSwitchFirst`.

### Platform (Windows-touching) for User Story 1

- [X] T017 [US1] Add to `src/KbFix/Platform/SessionLayoutGateway.cs` a `public SessionState ReadSession()` method that enumerates active keyboard-category profiles, translates each into a `LayoutId`, determines the foreground active profile, and returns a populated `SessionState`. **Implementation deviation**: on the dev machine the modern TSF manager interface returns `E_NOINTERFACE`, so the actual read path uses `GetKeyboardLayoutList` + `KeyboardLayoutCatalog` to normalise HKLs to KLIDs. Manager-interface support is wired and used preferentially when available.
- [X] T018 [US1] Extend `src/KbFix/Platform/SessionLayoutGateway.cs` with `public AppliedAction Apply(PlannedAction action)`. Both code paths (manager interface and legacy HKL) are implemented. Failures are wrapped into `AppliedAction.Failure` rather than thrown.
- [X] T019 [US1] Extend `src/KbFix/Platform/SessionLayoutGateway.cs` with `public bool VerifyConverged(PersistedConfig persisted)`.

### CLI / orchestration for User Story 1

- [X] T020 [US1] Create `src/KbFix/Cli/Options.cs`. **Implementation note**: implemented with the full US2 flag set up-front (DryRun/Quiet/Help/Version) to avoid double-rewriting.
- [X] T021 [US1] Create `src/KbFix/Cli/Reporter.cs` returning the four-section report. `Outcome` enum lives in `Domain/Action.cs`.
- [X] T022 [US1] Create `src/KbFix/Program.cs` as the entry point. Marked `[STAThread]` and explicitly calls `CoInitializeEx(COINIT_APARTMENTTHREADED)` because TSF requires STA.
- [X] T023 [US1] Failure handling in `Program.cs` writes a typed error to stderr; with `KBFIX_DEBUG=1` it also dumps the stack.

**Checkpoint**: At this point `kbfix.exe` (built from `src/KbFix`) reproduces the SPECIFICATION.md scenario end-to-end on a real polluted session and is ready for the manual RDP verification in `quickstart.md`. **MVP achieved.**

---

## Phase 4: User Story 2 — Preview without changes (Priority: P2)

**Goal**: User can run `kbfix.exe --dry-run` to see what would change without touching the session.

**Independent Test**: With a polluted session, run `kbfix.exe --dry-run`; report lists the planned removals prefixed `(dry-run)` and the session is unchanged afterwards; exit code 0.

- [X] T024 [US2] Done in T020 (parser already includes all flags).
- [X] T025 [US2] Done in T022 (`--help` and `--version` short-circuit in `Program.Main`).
- [X] T026 [US2] Done in T022 (dry-run synthesises succeeded `AppliedAction`s and skips `VerifyConverged`).
- [X] T027 [US2] Done in T021 (Reporter prefixes `(dry-run)` and `DRY-RUN:`).
- [X] T028 [US2] Done in T021 (Reporter omits Persisted/Session sections in quiet mode).
- [X] T029 [P] [US2] Add `tests/KbFix.Tests/Cli/OptionsTests.cs` covering all flag aliases and the `--bogus` usage-error path.

**Checkpoint**: User Stories 1 and 2 both fully functional. The MVP plus a safe preview path.

---

## Phase 5: User Story 3 — Clear diagnostic output (Priority: P3)

**Goal**: The report is genuinely useful for support: human-readable layout names, identified failure step on errors, stable formatting tested by example.

**Independent Test**: Run `kbfix.exe` (any mode) and confirm the output contains all four sections, layout lines include a non-cryptic name where Windows provides one, and a forced failure produces a report identifying the failing step plus a non-zero exit.

- [X] T030 [US3] `Reporter.ResolveLayoutName` reads `HKLM\…\Keyboard Layouts\<KLID>\Layout Text`; failures collapse to an empty string.
- [X] T031 [US3] `Reporter.FormatResult` includes the failing action in the `Result: FAILED at <Kind> <LayoutId>: …` line on failure outcome.
- [X] T032 [P] [US3] `tests/KbFix.Tests/Cli/ReporterTests.cs` covers all six golden-output cases.

**Checkpoint**: All three user stories independently functional and reportable.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Ship-readiness items that span the whole utility.

- [X] T033 [P] `README.md` written at repo root.
- [X] T034 `dotnet test` reports **34 / 34 passing** across `LayoutSetTests`, `ReconciliationPlanTests`, `OptionsTests`, `ReporterTests`.
- [X] T035 `dotnet publish` produced `dist/kbfix.exe` (~88 MB single self-contained file). Smoke-tested on the build machine: `--help`, `--version`, `--dry-run`, `--dry-run --quiet` all behave per the contract. The dev machine doesn't have the RDP-injected variant of the bug, but `kbfix --dry-run` correctly identifies an equivalent session-only artifact (`0x04050405` Czech default + an unmapped `0x04090405` substitute), proposes the right `SwitchActive → Deactivate × 2` plan, and exits 0.
- [ ] T036 **DEFERRED**: manual RDP verification on a polluted target session. This task cannot be executed by the implementing agent — it requires a real RDP login on a machine that exhibits the SPECIFICATION.md bug. Pavel must run this before tagging v1.0.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1, T001–T004)**: no dependencies — start immediately.
- **Foundational (Phase 2, T005–T011)**: depends on Setup. Blocks all user-story phases.
- **User Story 1 (Phase 3, T012–T023)**: depends on Foundational. Delivers the MVP.
- **User Story 2 (Phase 4, T024–T029)**: depends on User Story 1 (extends `Options.cs`, `Program.cs`, `Reporter.cs`).
- **User Story 3 (Phase 5, T030–T032)**: depends on User Story 1 (extends `Reporter.cs`). Independent of US2 — can run in parallel with US2 if staffed.
- **Polish (Phase 6, T033–T036)**: depends on whichever stories you intend to ship.

### Within User Story 1

- T012, T013, T014 (pure records) can run in parallel.
- T015 (`ReconciliationPlan.Build`) depends on T012, T013, T014.
- T016 (unit tests) depends on T015 and on the records.
- T017 (`ReadSession`) depends on T010, T009 (interop) and T013.
- T018 (`Apply`) depends on T010, T009, T014.
- T019 (`VerifyConverged`) depends on T017, T011.
- T020 (Options) is independent of the domain — can be done at any time after Setup.
- T021 (Reporter) depends on T012, T013, T014, T015 (knows Outcome and AppliedAction).
- T022 (Program.cs orchestrator) depends on T011, T015, T017, T018, T019, T020, T021.
- T023 builds on T022.

### Parallel Opportunities

- All four Setup tasks marked [P] (T002, T003, T004) can run together once T001 has created the projects.
- All four Foundational tasks marked [P] (T005, T006, T007, T008) can run in parallel — they touch separate files.
- Within User Story 1, T012/T013/T014 are mutually parallel; T016 (tests) is parallel to T017/T018/T019 (platform), since they touch different folders and depend on the already-completed T015.
- User Story 2 and User Story 3 can be developed in parallel by different team members once User Story 1 is complete (US2 owns `Options.cs` + dry-run flow in `Program.cs`; US3 owns `Reporter.cs` name resolution and failure formatting). Coordination point is `Reporter.cs`, which both phases extend — pick an order locally if one developer.

---

## Parallel Example: Foundational Phase

```bash
# After T001 sets up projects, launch in parallel:
Task: "T005 ExitCodes constants in src/KbFix/Diagnostics/ExitCodes.cs"
Task: "T006 LayoutId record struct in src/KbFix/Domain/LayoutId.cs"
Task: "T007 LayoutSet immutable wrapper in src/KbFix/Domain/LayoutSet.cs"
Task: "T008 LayoutSet unit tests in tests/KbFix.Tests/Domain/LayoutSetTests.cs"
```

## Parallel Example: User Story 1 Domain

```bash
# After Foundational is done, launch in parallel:
Task: "T012 PersistedConfig record in src/KbFix/Domain/PersistedConfig.cs"
Task: "T013 SessionState record in src/KbFix/Domain/SessionState.cs"
Task: "T014 Action records in src/KbFix/Domain/Action.cs"
# Then sequentially:
Task: "T015 ReconciliationPlan.Build in src/KbFix/Domain/ReconciliationPlan.cs"
Task: "T016 ReconciliationPlan unit tests in tests/KbFix.Tests/Domain/ReconciliationPlanTests.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 only)

1. Phase 1 Setup (T001–T004).
2. Phase 2 Foundational (T005–T011).
3. Phase 3 User Story 1 (T012–T023).
4. Run `dotnet test` (T034 is the formal task; you can run it informally here).
5. Run `dotnet publish` per `quickstart.md` and exercise the binary by hand on a polluted session.
6. **STOP and validate**. If the manual RDP test passes, you have a shippable v1 that already solves the entire SPECIFICATION.md problem.

### Incremental Delivery

1. MVP (Setup → Foundational → US1) → tag a `v0.1` build internally.
2. Add US2 (`--dry-run`) → tag `v0.2`.
3. Add US3 (name resolution + failure detail) → tag `v0.3`.
4. Polish (T033–T036) → tag `v1.0` after the manual RDP verification gate.

Each tagged build is independently usable and addresses the bug; later builds only improve ergonomics.

### Parallel Team Strategy

With two developers after Foundational completes:
- Developer A: User Story 1 (the orchestrator and platform interop) — the critical path.
- Developer B: starts on the pure-domain parts of US1 (T012–T016) and then moves to US2 once US1's `Options.cs`/`Reporter.cs` skeletons exist.

US3 can be picked up by either developer once US1 is functional; it touches only `Reporter.cs`.

---

## Notes

- Tests in this plan cover the **pure** layer (Domain) and the **CLI parsing/formatting** layer. They do NOT mock the Windows TSF surface — that surface is verified by the mandatory manual RDP procedure (T036).
- The `Domain/` folder must remain free of `System.Runtime.InteropServices` and `Microsoft.Win32` references; if a task seems to need them in `Domain/`, it belongs in `Platform/` instead.
- Never add a task that writes to `HKCU\Keyboard Layout\Preload` — the constitution and FR-006 forbid it.
- Commit after each completed task or logical group; the project's git extension hook will offer to commit at every speckit phase boundary.
