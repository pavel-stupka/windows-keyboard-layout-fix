# Implementation Plan: Build Command Script

**Branch**: `002-build-script` | **Date**: 2026-04-13 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/002-build-script/spec.md`

## Summary

Introduce a single `build.cmd` at the repository root that produces a runnable build of the KbFix utility into a top-level `dist/` folder. The script accepts a positional configuration argument (`debug` — default — or `release`), plus a small set of GNU-style switches (`--help` / `/?`, `--test`, `--output <path>`, `--no-clean`), validates that the .NET 8 SDK is present, cleans the output directory, invokes `dotnet publish` on `KbFix.sln` for the selected configuration, optionally runs `dotnet test` against `tests/KbFix.Tests`, and surfaces every step's outcome via its exit code. The script is implemented as a Windows batch file (`.cmd`) — no new runtime dependencies, no third-party tooling — and delegates all actual compilation to the existing .NET toolchain already assumed by the project. The `dist/` folder is treated as disposable and script-owned; it is already in `.gitignore`.

## Technical Context

**Language/Version**: Windows batch (`cmd.exe`) for `build.cmd`; builds C# 12 on .NET 8 (LTS), unchanged from the existing project.
**Primary Dependencies**: .NET 8 SDK (`dotnet` CLI) on `PATH`. No new NuGet packages, no new scripting runtimes. The existing `KbFix.sln`, `src/KbFix/KbFix.csproj`, and `tests/KbFix.Tests/KbFix.Tests.csproj` are reused as-is.
**Storage**: Filesystem only. The script reads nothing persistent, writes only to the output directory (`dist/` by default) and to stdout/stderr.
**Testing**: Existing `xunit` test project under `tests/KbFix.Tests`, invoked via `dotnet test` when the `--test` switch is passed. The build script itself is a shell-thin wrapper; its behavior is validated by the quickstart scenarios rather than by a unit test harness for batch files.
**Target Platform**: Windows 10 / Windows 11 developer machines with the .NET 8 SDK installed. The script is not expected to run on Linux/macOS — matches the project's Windows-only nature.
**Project Type**: Single desktop CLI utility (unchanged). This feature adds a repository-root helper script, not a new source project.
**Performance Goals**: Not a user-facing runtime concern. Build wall-clock is dominated by `dotnet publish`; the script adds only argument parsing, a directory clean, and one or two `dotnet` invocations — target overhead well under one second on top of the underlying tool.
**Constraints**: Must work from any current working directory (locates itself via `%~dp0`). Must not require Administrator elevation. Must not introduce any dependency beyond `cmd.exe` and the already-required .NET 8 SDK. Must leave `.gitignore`, `.editorconfig`, and the existing solution/projects untouched in behavior (no new MSBuild targets, no new props files).
**Scale/Scope**: One script file (~150 lines of batch), one feature directory under `specs/002-build-script/`, plus a `dist/` folder created at runtime. No changes to `src/` or `tests/`.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

The five Core Principles from `.specify/memory/constitution.md` (v1.0.0) apply as follows:

- **I. Single Purpose & Simplicity** — PASS. This feature is a repository-root helper that wraps `dotnet publish` for the existing KbFix utility. It adds no functionality to KbFix itself, no GUI, no configuration system, and no plugin surface. Switches are deliberately kept to a minimum viable set (`--help`, `--test`, `--output`, `--no-clean`) and explicitly exclude anything that would imply a general-purpose build system.
- **II. User Configuration Is the Source of Truth** — NOT APPLICABLE. This feature does not touch Windows language settings, session state, or any user-level persisted configuration. The principle governs the runtime behavior of KbFix itself, not the build tooling.
- **III. Safe & Reversible Operations** — PASS. The script's only destructive action is clearing its own output directory (`dist/` by default, or the explicit `--output` path), which is declared script-owned and is already in `.gitignore`. The `--no-clean` switch exists precisely so a user or CI job can opt out of the destructive step. The script never touches `src/`, `tests/`, `bin/`, `obj/`, git state, or any system location. It is idempotent (running twice with the same arguments leaves `dist/` in an identical state).
- **IV. Native Windows Integration** — PASS. The script is a plain Windows `cmd.exe` batch file — the most native Windows scripting surface available — and delegates compilation to the already-required `dotnet` CLI. No PowerShell, no WSL, no third-party shells, no registry access, and no Windows API calls.
- **V. Observability & Diagnosability** — PASS. The script echoes, before building, the resolved configuration, the resolved output directory, and whether the optional test step will run (satisfies FR-016). It returns zero on full success (including no-op idempotent runs) and non-zero on any failure (satisfies FR-009 and aligns with the constitution's exit-code requirement). Error messages identify which step failed — SDK presence check, argument validation, clean, build, or tests — and forward underlying `dotnet` errors to the user. A `--help` / `/?` switch makes the script self-documenting without the user having to read its source.

**Gate result**: PASS. No violations to justify; Complexity Tracking section intentionally empty below.

## Project Structure

### Documentation (this feature)

```text
specs/002-build-script/
├── plan.md              # This file (/speckit.plan command output)
├── spec.md              # Feature specification (already written by /speckit.specify)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/
│   └── cli.md           # CLI contract for build.cmd (Phase 1 output)
├── checklists/
│   └── requirements.md  # Spec quality checklist (from /speckit.specify)
└── tasks.md             # Phase 2 output (/speckit.tasks command — NOT created here)
```

### Source Code (repository root)

```text
D:\Projects\windows-keyboard-layout-fix\
├── build.cmd                      # NEW — repository-root build entry point
├── KbFix.sln                      # unchanged
├── src\
│   └── KbFix\
│       ├── KbFix.csproj           # unchanged (already publishes single-file self-contained win-x64)
│       └── ...                    # unchanged C# sources
├── tests\
│   └── KbFix.Tests\
│       ├── KbFix.Tests.csproj     # unchanged (xunit)
│       └── ...                    # unchanged test sources
├── dist\                          # NEW (created at runtime by build.cmd; already in .gitignore)
└── specs\
    └── 002-build-script\          # this feature's spec artifacts
```

**Structure Decision**: Keep the existing single-project layout (`src/KbFix`, `tests/KbFix.Tests`). Introduce exactly one new file — `build.cmd` at the repository root — and one runtime-created folder, `dist/`. No new source projects, no new directories under `src/` or `tests/`, no changes to `KbFix.sln` or either `.csproj`. This matches Principle I (Single Purpose & Simplicity): the feature adds the minimum surface required to satisfy the spec, and it lives at the repo root so developers discover it via `ls` rather than having to remember a path.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

No violations. Section intentionally empty.
