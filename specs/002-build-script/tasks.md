---

description: "Task list for feature 002-build-script"
---

# Tasks: Build Command Script

**Input**: Design documents from `/specs/002-build-script/`
**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/cli.md, quickstart.md

**Tests**: Tests were not explicitly requested in the spec. Because this feature is a single Windows batch file (not a library), the validation strategy is the 11-scenario walkthrough in `quickstart.md`. No automated test project is introduced.

**Organization**: Tasks are grouped by the three user stories in `spec.md` (US1, US2, US3). Because every task ultimately edits the same single file (`D:\Projects\windows-keyboard-layout-fix\build.cmd`), tasks *within* a story run sequentially — parallel `[P]` markers are used only where a task touches a different path (e.g. validation scenarios in `quickstart.md`, polish tasks).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks).
- **[Story]**: Which user story the task belongs to (`US1`, `US2`, `US3`). Omitted for Setup, Foundational, and Polish tasks.
- Every task below includes an absolute file path in its description.

## Path Conventions

- Single project at the repository root `D:\Projects\windows-keyboard-layout-fix\`.
- The only new file introduced by this feature is `D:\Projects\windows-keyboard-layout-fix\build.cmd`.
- `D:\Projects\windows-keyboard-layout-fix\dist\` is created at runtime by the script itself and is already covered by `.gitignore`.
- All paths shown below are absolute so that every task is executable without additional context.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Land the empty script file so every later phase has something to edit, and confirm the toolchain baseline the script will rely on.

- [X] T001 Create an empty `build.cmd` stub at `D:\Projects\windows-keyboard-layout-fix\build.cmd` containing `@echo off`, `setlocal EnableExtensions EnableDelayedExpansion`, and a final `exit /b 0`. The file must have CRLF line endings and be saved as ASCII (no BOM), to match the rest of the repository's `.editorconfig`.
- [X] T002 [P] Confirm the toolchain baseline by running `dotnet --list-sdks` in a shell at `D:\Projects\windows-keyboard-layout-fix\` and verifying that at least one `8.x.y` SDK is listed. No files are edited; this task simply proves the environment the script will check for at runtime.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish the structural pieces of `build.cmd` that every user story depends on — repository-root resolution, SDK validation, unified banner/FAILED reporting, and label-based control flow — so that US1/US2/US3 only have to plug their own stages into an already-working scaffold.

**⚠️ CRITICAL**: No user story work can begin until every task in this phase is complete.

- [X] T003 In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, add repository-root detection using `set "REPO_ROOT=%~dp0"` and strip the trailing backslash; define `SOLUTION_PATH=%REPO_ROOT%\KbFix.sln`, `PROJECT_PATH=%REPO_ROOT%\src\KbFix\KbFix.csproj`, and `DEFAULT_OUTPUT_DIR=%REPO_ROOT%\dist`. Verify solution/project files exist and, if not, print `[build.cmd] FAILED at argparse: not inside a KbFix checkout` to stderr and `exit /b 1`.
- [X] T004 In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, add a `:check_sdk` section that runs `dotnet --list-sdks >nul 2>&1`, checks `ERRORLEVEL`, and on failure prints `[build.cmd] FAILED at sdk: .NET 8 SDK not found on PATH. Install from https://dot.net` to stderr and `exit /b 2`. This satisfies research decision R5 and FR-008.
- [X] T005 In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, add a `:report_ok` label that prints `[build.cmd] OK: !CONFIG_LABEL! build written to !OUTPUT_DIR! (tests: !TEST_LABEL!)` and `exit /b 0`, plus a `:fail` label that accepts a stage label in `%~1` and a reason in `%~2`, prints `[build.cmd] FAILED at %~1: %~2` to stderr, and exits with whatever code is already in `ERRORLEVEL` (fallback to `1`). Both labels are reused by every later phase; no user story may `goto :EOF` directly.
- [X] T006 In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, define default values for `CONFIGURATION=Debug`, `OUTPUT_DIR=%DEFAULT_OUTPUT_DIR%`, `DO_CLEAN=1`, `DO_TEST=0`, `HELP_ONLY=0`, `CONFIG_LABEL=Debug`, and `TEST_LABEL=skipped` at the top of the script. These variables are the runtime form of the `BuildInvocation` entity in `data-model.md`.
- [X] T007 In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, wire the execution skeleton: call `:check_sdk`, then emit a pre-build banner `[build.cmd] building !CONFIG_LABEL! -> !OUTPUT_DIR! (tests: !TEST_LABEL!)`, then `goto :EOF` with a TODO comment where the stage dispatch will land in US1. At this checkpoint the script should already validate SDK presence and print a banner even though it does not yet build anything.

**Checkpoint**: Foundation ready — `build.cmd --help`-less invocation prints the banner and SDK check; US1 can now implement the actual publish step.

---

## Phase 3: User Story 1 — One-command Debug Build (Priority: P1) 🎯 MVP

**Goal**: Running `build.cmd` with no arguments produces a runnable Debug build into `dist/` and exits with code `0`. This is the MVP increment — after this phase the script already delivers daily developer value even without the more advanced switches.

**Independent Test**: From a clean checkout on Windows with the .NET 8 SDK installed, run `build.cmd` from `D:\Projects\windows-keyboard-layout-fix\`. Verify `dist\kbfix.exe` exists, that launching it matches `dotnet run --project src\KbFix` in Debug, and that the script printed an `OK: Debug build written to ... (tests: skipped)` line and returned exit code `0`.

### Implementation for User Story 1

- [X] T008 [US1] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, add a `:stage_clean` section that, when `DO_CLEAN==1`, removes the resolved `OUTPUT_DIR` with `rmdir /s /q "%OUTPUT_DIR%"` and then recreates it with `mkdir "%OUTPUT_DIR%"`. If `rmdir` returns non-zero, call `:fail clean "unable to clear %OUTPUT_DIR% (is a file locked?)"`. Matches research decision R7.
- [X] T009 [US1] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, add a `:stage_build` section that runs `dotnet publish "%PROJECT_PATH%" -c %CONFIGURATION% -o "%OUTPUT_DIR%" --nologo` and, on non-zero `ERRORLEVEL`, calls `:fail build "dotnet publish exited with code %ERRORLEVEL%"`. Forward all stdout/stderr from `dotnet` unmodified. Matches research decisions R2, R3, and R4.
- [X] T010 [US1] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, replace the TODO dispatch from T007 with the real sequence: `call :stage_clean`, `call :stage_build`, `goto :report_ok`. At this point running `build.cmd` with no arguments must produce a working Debug drop in `dist\`.
- [X] T011 [US1] [P] Walk the quickstart **Scenario 1** (Default Debug build) at `D:\Projects\windows-keyboard-layout-fix\specs\002-build-script\quickstart.md` against a real build. Confirm the banner, the OK line, the presence of `dist\kbfix.exe`, and that launching it matches `dotnet run --project src\KbFix`. Record the result in the commit message of this story.
- [X] T012 [US1] [P] Walk quickstart **Scenario 11** (missing SDK) at `D:\Projects\windows-keyboard-layout-fix\specs\002-build-script\quickstart.md`. On a machine or shell where `dotnet` is temporarily not on `PATH` (e.g. via `set PATH=C:\Windows\System32`), run `build.cmd` and confirm the `FAILED at sdk` message and exit code `2`. If the tester's machine cannot produce this state safely, mark the scenario as SKIPPED in the commit message with the reason.

**Checkpoint**: At this point, `build.cmd` with no arguments is a fully functional, independently testable Debug build command. The MVP is shippable.

---

## Phase 4: User Story 2 — Explicit Debug / Release Selection (Priority: P1)

**Goal**: Add positional argument parsing so that `build.cmd debug` and `build.cmd release` produce the corresponding single-file self-contained publish drop, while `build.cmd` with no arguments remains equivalent to `build.cmd debug`. Invalid tokens fail fast with exit code `1` and a usage hint.

**Independent Test**: On a machine with the .NET 8 SDK installed, run `build.cmd release` and verify `dist\kbfix.exe` is a Release build; then run `build.cmd debug` and verify it is replaced with a Debug build; then run `build.cmd` alone and verify it matches `build.cmd debug`; finally run `build.cmd staging` and verify the script prints a `FAILED at argparse` line, prints usage, and exits non-zero without touching `dist\`.

### Implementation for User Story 2

- [X] T013 [US2] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, add a `:parse_args` loop that walks `%*` via `shift` and, for each token that does not start with `-` or `/`, treats it as the positional configuration. Accept `debug` and `release` case-insensitively (`if /i "%~1"=="debug"`), set `CONFIGURATION` to `Debug` or `Release` and `CONFIG_LABEL` to match, and set a `POSITIONAL_SEEN=1` flag. A second positional token must call `:fail argparse "only one configuration token is allowed"`. Matches data-model rules V-CONFIG-ENUM and V-CONFIG-ONE.
- [X] T014 [US2] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, handle an unrecognised positional token inside the `:parse_args` loop by calling `:fail argparse "unknown configuration '%~1'. Accepted: debug, release"` after printing the usage block. This satisfies FR-006 and data-model rule V-CONFIG-ENUM. (Usage printing is added in US3; until then, emit a placeholder line `see build.cmd --help` — the placeholder is replaced in T019.)
- [X] T015 [US2] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, invoke `:parse_args` before `:check_sdk` in the dispatch sequence, and confirm that the pre-build banner (from T007/T010) uses the resolved `CONFIG_LABEL`. `build.cmd release` must now produce a Release drop and `build.cmd` alone must remain equivalent to `build.cmd debug`. Idempotency (FR-007, SC-006) must still hold — re-running the same command twice must leave `dist\` in an identical state.
- [X] T016 [US2] [P] Walk quickstart **Scenarios 2, 3, and 4** at `D:\Projects\windows-keyboard-layout-fix\specs\002-build-script\quickstart.md` (explicit Release build, default-equals-Debug, invalid configuration argument). Record the three results in the commit message.

**Checkpoint**: US1 **and** US2 are both independently functional. A developer can switch between Debug and Release by changing exactly one word on the command line.

---

## Phase 5: User Story 3 — Discoverable Usage Help and Extra Switches (Priority: P2)

**Goal**: Make the script self-documenting (`--help` / `-h` / `/?`) and add the three opt-in switches promised by the spec: `--no-clean`, `--test`, and `--output <path>`. All switches must be accepted in any order relative to the positional configuration token.

**Independent Test**: Run `build.cmd --help` and verify the output lists every switch, the default configuration, and the default output directory. Exercise each switch in isolation (`--no-clean` preserves a sentinel file, `--test` runs `dotnet test` against `KbFix.sln`, `--output out\local` writes to `out\local` instead of `dist\`) and confirm observable side effects match. Run `build.cmd --test release` and `build.cmd release --test` back-to-back and confirm identical results.

### Implementation for User Story 3

- [X] T017 [US3] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, add a `:print_usage` label that echoes a stable usage block to stdout covering: synopsis (`Usage: build.cmd [debug|release] [options]`), `Default configuration: debug`, `Default output: %DEFAULT_OUTPUT_DIR%`, and one line per supported switch (`--help / -h / /?`, `--test`, `--output <path>`, `--no-clean`), plus one example per switch. The block is pure text — no variable expansion beyond the default output — and must satisfy FR-011, SC-005, and the "Usage message" section of `contracts/cli.md`.
- [X] T018 [US3] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, extend `:parse_args` to recognise `--help`, `-h`, and `/?` at any position. When any of them appear, set `HELP_ONLY=1`; after the loop finishes, if `HELP_ONLY==1`, call `:print_usage` and `exit /b 0` before `:check_sdk` runs. This satisfies data-model rule V-HELP-SHORT-CIRCUIT.
- [X] T019 [US3] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, replace the `see build.cmd --help` placeholder inserted by T014 with a proper call to `:print_usage` followed by `:fail argparse "unknown configuration '%~1'. Accepted: debug, release"`, so that usage errors print the full usage block exactly the same way `--help` does.
- [X] T020 [US3] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, extend `:parse_args` to recognise `--no-clean` (flag only, any position), setting `DO_CLEAN=0`. In `:stage_clean`, when `DO_CLEAN==0`, print `[build.cmd] skipping clean (--no-clean)` and return without touching `OUTPUT_DIR`. Satisfies FR-014.
- [X] T021 [US3] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, extend `:parse_args` to recognise `--test` (flag only, any position), setting `DO_TEST=1` and `TEST_LABEL=run`. Then add a `:stage_test` section that, when `DO_TEST==1`, runs `dotnet test "%SOLUTION_PATH%" -c %CONFIGURATION% --nologo` after a successful build and, on non-zero `ERRORLEVEL`, calls `:fail test "dotnet test exited with code %ERRORLEVEL%"`. Wire `call :stage_test` into the dispatch sequence between `:stage_build` and `:report_ok`. Satisfies FR-012 and research decision R3.
- [X] T022 [US3] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, extend `:parse_args` to recognise both `--output <path>` and `--output=<path>`. Resolve relative paths against `REPO_ROOT` using `for /f` with `%~f` (e.g. `for %%I in ("%USER_OUTPUT%") do set "OUTPUT_DIR=%%~fI"`), and reject values that resolve to `%REPO_ROOT%`, `%REPO_ROOT%\src`, `%REPO_ROOT%\tests`, or `%REPO_ROOT%\.git` via `:fail argparse "--output must not resolve to %OUTPUT_DIR%"`. An empty value after `--output` must also call `:fail argparse "--output requires a path"`. Satisfies FR-013 and data-model rules V-OUTPUT-REQ-VALUE and V-OUTPUT-NOT-REPO.
- [X] T023 [US3] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, extend `:parse_args` to reject any token that starts with `-` or `--` and is not one of the recognised switches, by calling `:fail argparse "unknown option '%~1'"` after printing usage. Satisfies data-model rule V-UNKNOWN-FLAG.
- [X] T024 [US3] In `D:\Projects\windows-keyboard-layout-fix\build.cmd`, confirm that the pre-build banner (from T007) still reflects the resolved `CONFIG_LABEL`, `OUTPUT_DIR`, and `TEST_LABEL` *after* all switches have been processed, and that `:report_ok` reads the same variables. No duplicated banner, no stale labels. Satisfies FR-016.
- [X] T025 [US3] [P] Walk quickstart **Scenarios 5, 6, 7, 8, 9, and 10** at `D:\Projects\windows-keyboard-layout-fix\specs\002-build-script\quickstart.md` (help, `--test`, `--output`, `--no-clean`, order independence, running from a non-root cwd). Record the six results in the commit message.

**Checkpoint**: All three user stories are independently functional. `build.cmd --help` lists every switch, and every switch behaves as specified in `contracts/cli.md`.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Tighten the script's first-run experience without adding behavior, and run the full quickstart validation one more time.

- [X] T026 [P] Add a top-of-file block comment to `D:\Projects\windows-keyboard-layout-fix\build.cmd` summarising its purpose (one line), the stage sequence (`argparse → sdk → clean → build → test → report`), and a pointer to `specs\002-build-script\contracts\cli.md` for the full contract. Keep the comment to ≤10 lines — the Constitution's Observability principle is satisfied by runtime output, not by commentary.
- [X] T027 [P] Confirm that `D:\Projects\windows-keyboard-layout-fix\.gitignore` already lists `dist/` (it does — line 5 of the existing file) and therefore no `.gitignore` change is required by this feature. No edit; this task exists only so the verifier confirms the assumption before closing the story.
- [X] T028 Run the **full 11-scenario walkthrough** in `D:\Projects\windows-keyboard-layout-fix\specs\002-build-script\quickstart.md` end-to-end in a single session, ticking off every scenario. Any failing scenario blocks the feature from shipping. Record the final pass/fail summary in the commit message for this phase.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup, T001–T002)**: No dependencies; can start immediately.
- **Phase 2 (Foundational, T003–T007)**: Depends on Phase 1. **Blocks every user story.**
- **Phase 3 (US1, T008–T012)**: Depends on Phase 2.
- **Phase 4 (US2, T013–T016)**: Depends on Phase 2 **and** Phase 3 (US2 extends the single-file script that US1 introduces; splitting them across parallel branches would just create merge conflicts on `build.cmd`).
- **Phase 5 (US3, T017–T025)**: Depends on Phase 2 **and** Phase 4. US3 extends the same script again with switches layered on top of the US2 argument parser.
- **Phase 6 (Polish, T026–T028)**: Depends on Phase 5 (final validation run covers scenarios added in every preceding phase).

### User Story Dependencies

- **US1 (P1)**: Can start after Phase 2. Independently shippable on its own — this is the MVP.
- **US2 (P1)**: Must be implemented after US1 because both edit the same `build.cmd` file. Still independently *testable* via Scenario 2–4 in quickstart.
- **US3 (P2)**: Must be implemented after US2 for the same reason (same file). Still independently *testable* via Scenarios 5–10 in quickstart.

### Within Each User Story

- Every implementation task inside a single user story touches `D:\Projects\windows-keyboard-layout-fix\build.cmd`. They run **sequentially** — never mark them `[P]`.
- Quickstart walkthrough tasks (e.g. T011, T012, T016, T025) are marked `[P]` because they only read the already-built script and exercise it from the shell; they can be performed in parallel by multiple reviewers or scripted into a single session.

### Parallel Opportunities

- **T001** and **T002** can run in parallel (one edits a file, the other runs a read-only shell command).
- Within US1, **T011** and **T012** can run in parallel after **T010** completes.
- Within US2, **T016** is a parallel verification step after **T013–T015** land.
- Within US3, **T025** verifies all six switch scenarios in parallel after **T017–T024** land.
- Within Polish, **T026** and **T027** can run in parallel; **T028** must run last.

---

## Parallel Example: Validating User Story 1

```text
# After T010 lands, launch the two verification tasks in parallel:
Task T011: walk Scenario 1 from specs/002-build-script/quickstart.md
Task T012: walk Scenario 11 from specs/002-build-script/quickstart.md (missing SDK)
```

## Parallel Example: Polish phase

```text
# After T025 lands, launch T026 and T027 in parallel:
Task T026: add top-of-file block comment to build.cmd
Task T027: confirm .gitignore already covers dist/
# Then run T028 sequentially to close the feature.
```

---

## Implementation Strategy

### MVP First (User Story 1 only)

1. Complete Phase 1 (Setup).
2. Complete Phase 2 (Foundational) — this is the critical blocker.
3. Complete Phase 3 (US1).
4. **STOP and VALIDATE** using quickstart Scenario 1 (and Scenario 11 if the environment permits).
5. Ship this as the first increment — a developer can already replace the hand-crafted `dotnet publish` invocation with `build.cmd`.

### Incremental Delivery

1. Ship the MVP (US1) as above.
2. Add US2 → validate Scenarios 2–4 → ship as the "Debug and Release with one script" increment.
3. Add US3 → validate Scenarios 5–10 → ship as the "self-documenting build command" increment.
4. Close with Phase 6 polish and a full 11-scenario walkthrough.

### Parallel Team Strategy

Because every implementation task edits the same single file (`build.cmd`), splitting US1/US2/US3 across multiple developers working simultaneously would cause constant merge conflicts. The realistic parallelism in this feature is:

- One developer writes the script end-to-end, phase by phase.
- A second reviewer independently runs the quickstart walkthrough tasks (T011, T012, T016, T025, T028) as the script grows.

Do **not** attempt to parallelise T003–T024 across multiple developers.

---

## Notes

- `[P]` tasks = different files **or** pure verification reads; no same-file conflicts.
- `[Story]` label maps a task to exactly one user story for traceability.
- Each user story should be independently completable and testable via the corresponding `quickstart.md` scenarios.
- Commit after each phase (or after each logical pair of tasks inside a phase) so that the MVP, the Debug/Release increment, and the switches increment each correspond to a reviewable changeset.
- Stop at any checkpoint to validate the story independently.
- Avoid: vague tasks, adding MSBuild targets or new projects, introducing PowerShell or bash variants, and any edits outside `D:\Projects\windows-keyboard-layout-fix\build.cmd` except for the allowed no-op verification reads in `quickstart.md` and `.gitignore`.
