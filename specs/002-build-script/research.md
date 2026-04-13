# Phase 0 Research: Build Command Script

**Feature**: 002-build-script
**Date**: 2026-04-13

The Technical Context in `plan.md` contains no `NEEDS CLARIFICATION` markers. The research below documents the small set of deliberate decisions needed to turn the spec into an implementation, each framed as Decision / Rationale / Alternatives considered.

## R1. Script language: Windows batch (`cmd.exe`) vs. PowerShell

- **Decision**: Implement the entry point as a plain `cmd.exe` batch file named `build.cmd` at the repository root.
- **Rationale**:
  - The user explicitly asked for `build.cmd` (batch extension) in the feature description; switching to a different shell would fight the request.
  - Batch files run on every supported Windows version without any execution-policy friction. PowerShell requires `Set-ExecutionPolicy` gymnastics on some locked-down developer machines and CI runners.
  - The script's work is trivial: parse a handful of arguments, `rmdir /s /q dist`, shell out to `dotnet`, and propagate an exit code. That is squarely within what batch handles cleanly; the cases where batch becomes painful (complex string manipulation, arrays, JSON) do not arise here.
  - Stays aligned with Constitution Principle IV (Native Windows Integration): `cmd.exe` is the most native Windows scripting surface available.
- **Alternatives considered**:
  - **PowerShell (`build.ps1`)**: Richer language and better error handling, but changes the user-visible command, pulls in execution-policy concerns, and offers no meaningful benefit for this script's scope.
  - **MSBuild target** (`<Target Name="Dist">...`): Keeps everything inside the .NET toolchain, but forces the user to remember `dotnet msbuild /t:Dist /p:Configuration=Release` — exactly the kind of long invocation the build script is meant to replace.
  - **Bash script under WSL/Git Bash**: Not portable to a vanilla Windows developer machine and violates the Windows-only assumption in the spec.

## R2. `dotnet` subcommand: `publish` vs. `build`

- **Decision**: Use `dotnet publish` on `src/KbFix/KbFix.csproj` for the selected configuration.
- **Rationale**:
  - `KbFix.csproj` already sets `PublishSingleFile=true`, `SelfContained=true`, `IncludeNativeLibrariesForSelfExtract=true`, and `RuntimeIdentifier=win-x64`. These properties only take effect during `dotnet publish`; a plain `dotnet build` produces a framework-dependent folder in `bin/` that is *not* the runnable artifact developers want in `dist/`.
  - FR-017 in the spec requires that the contents of the output directory be runnable on a supported Windows target without manually copying additional files from the source tree — which matches the self-contained single-file publish output.
  - Keeps the build command equivalent across Debug and Release: Debug produces a Debug single-file publish, Release produces a Release single-file publish. Both are runnable drops.
- **Alternatives considered**:
  - **`dotnet build`**: Faster for inner-loop iteration but does not produce a single-file self-contained executable, so the `dist/` drop would not satisfy SC-004 (every file needed to run is present in `dist/`).
  - **`dotnet build` for Debug + `dotnet publish` for Release**: Split-personality behavior would surprise users and complicate the script with a second code path for the same operation.

## R3. Target the solution or the project?

- **Decision**: Target `src/KbFix/KbFix.csproj` directly for the publish step, and target `KbFix.sln` for the optional `--test` step via `dotnet test`.
- **Rationale**:
  - `dotnet publish KbFix.sln` would attempt to publish *every* project in the solution, including `tests/KbFix.Tests` — which is marked `<IsPackable>false</IsPackable>` but still an executable-like target under `dotnet publish`, producing noisy output and a test-project `publish/` folder nobody wants.
  - Targeting the specific csproj keeps the publish step focused on the one artifact that matters (`kbfix.exe`) and keeps `dist/` clean.
  - `dotnet test KbFix.sln` does the right thing automatically: it discovers `KbFix.Tests`, builds only what it needs, and reports pass/fail. Using the solution here (rather than the test csproj directly) is slightly more future-proof if a second test project is ever added.
- **Alternatives considered**:
  - **Publish the solution**: Rejected for the reasons above.
  - **Test the csproj directly (`dotnet test tests/KbFix.Tests/KbFix.Tests.csproj`)**: Equivalent today, less flexible tomorrow; no upside to tying the script to exactly one test project path.

## R4. Output directory layout: publish straight into `dist/`?

- **Decision**: Run `dotnet publish` with `--output <outdir>` set to the resolved output path (default `dist/`). Clean that directory up front (unless `--no-clean` is passed), and do not create any intermediate subfolder like `dist/win-x64/` or `dist/Release/`.
- **Rationale**:
  - The spec's FR-003 / FR-004 state that artifacts land "under a top-level `dist/` folder", and SC-004 requires the folder to be directly copyable to another machine. A flat layout with `kbfix.exe` sitting directly at `dist/kbfix.exe` is the most copy-pasteable and the least surprising.
  - Switching between `debug` and `release` replaces `dist/` wholesale thanks to the clean step, so there is no need to namespace by configuration. Users who want to preserve both builds can pass `--output out/debug` and `--output out/release` explicitly.
  - Using `dotnet publish --output` bypasses the default `bin/<config>/net8.0-windows.../publish/` directory, which is deep, configuration-namespaced, and not what FR-003 describes.
- **Alternatives considered**:
  - **Publish to default `bin/.../publish/` and `robocopy` into `dist/`**: Two steps where one suffices; more chances to leave `dist/` partially populated on failure.
  - **`dist/<config>/`**: More "correct" for side-by-side builds but contradicts the spirit of FR-007 (clean `dist/` reflects the most recent build) and makes the default `build.cmd` case subtly worse for copy-paste.

## R5. SDK presence check

- **Decision**: Before doing anything else, the script runs `dotnet --list-sdks` (redirecting stdout to `nul`, keeping stderr visible), checks `ERRORLEVEL`, and on failure prints an actionable error that tells the user to install .NET 8 SDK from `https://dot.net`, then exits with a non-zero code. It does *not* parse the output to enforce a specific SDK version — `dotnet publish` itself will already fail loudly if the target framework (`net8.0-windows10.0.17763.0`) is not satisfied.
- **Rationale**:
  - Satisfies FR-008 ("validate that a compatible .NET 8 SDK is available ... fail with a clear, actionable error") without re-implementing what the .NET CLI already does.
  - Distinguishes "no `dotnet` on `PATH` at all" (the common mistake for a new developer) from "`dotnet` present but wrong version" (a less common, and harder-to-fake, scenario), because the first case produces a recognisable "command not found" failure we can catch up front and translate into a friendly message.
  - Avoids tying the script to an exact SDK build number; Microsoft ships frequent patch releases and hard-coding one would churn for no benefit.
- **Alternatives considered**:
  - **Parse `dotnet --list-sdks` and enforce a minimum 8.x version**: More precise, but brittle (output format could change) and duplicates validation the .NET CLI already performs during restore/build.
  - **Skip the check and let `dotnet` fail**: Technically works, but the spec explicitly requires a clear message *before* attempting to build, and "command not found" from `cmd.exe` is markedly less helpful than a custom message pointing at `https://dot.net`.

## R6. Argument parsing: positional + GNU-style flags in any order

- **Decision**: Walk `%*` left-to-right with a `:parse_args` loop. Accept `--help` / `-h` / `/?` and `--no-clean` as flag-only tokens, `--test` as flag-only, and `--output <path>` (or `--output=<path>`) as a valued flag. Any single non-flag token is treated as the positional configuration and validated against a fixed `debug`/`release` set (case-insensitive). A second positional, or an unknown flag, is an error that prints usage and exits non-zero.
- **Rationale**:
  - Satisfies FR-015 (flags and the positional configuration may appear in any order) without pulling in a batch-file argument-parsing library (which does not really exist anyway).
  - Keeps the implementation scope tight — one loop, a handful of `if /i` comparisons, no regex. Matches the Assumptions section of the spec (no POSIX short-flag bundling, no response files).
  - `/?` is the traditional Windows help convention and is cheap to support alongside `--help`.
- **Alternatives considered**:
  - **Positional-only (`build.cmd [debug|release]`) and no flags**: Simpler parser but fails FR-011 / FR-012 / FR-013 / FR-014.
  - **Dispatch to PowerShell just for parsing**: Adds a shell-hop and an execution-policy dependency purely to save twenty lines of `if` statements. Not worth it.

## R7. Clean step implementation

- **Decision**: When `--no-clean` is not set, the script checks whether the resolved output directory exists and, if it does, runs `rmdir /s /q "%OUTPUT_DIR%"` followed by `mkdir "%OUTPUT_DIR%"`. Any failure of the `rmdir` step (e.g. a file is locked) is treated as a hard error: the script prints which path it was trying to clean, forwards the underlying error, and exits non-zero *before* attempting to build. It does not fall back to a partial clean.
- **Rationale**:
  - Matches the Edge Cases entry in the spec about locked files in `dist/`: "surface the failure with a clear message rather than partially deleting content and then silently continuing".
  - Matches FR-007 (clean before build) and the `--no-clean` opt-out in FR-014.
  - Keeps the implementation boring and obvious. `rmdir /s /q` is the native Windows idiom for "remove directory tree recursively without prompting"; nothing more sophisticated is required for a script-owned folder.
- **Alternatives considered**:
  - **`del /s /q` on the directory contents, keep the directory handle**: Slightly friendlier for tools that hold a handle on `dist/` itself but introduces asymmetric cleanup (files gone, subfolders left). Not worth the complexity.
  - **Trust `dotnet publish` to overwrite in place**: `dotnet publish --output` overwrites files it writes, but does not delete files the previous build produced that the current build no longer produces (renames, deletions, dependency changes). Relying on overwrite-only would violate SC-006 (identical state after two identical runs).

## R8. Locating the repository root regardless of current working directory

- **Decision**: Use `%~dp0` at the top of `build.cmd` to capture the directory the script itself lives in, and treat that as the repository root. All paths (`KbFix.sln`, `src/KbFix/KbFix.csproj`, the default `dist/` directory, relative `--output` paths) are resolved against it.
- **Rationale**:
  - Satisfies FR-010 ("works correctly regardless of the current working directory") in one line of batch that every Windows developer has seen before.
  - Avoids the `cd /d` pitfall where a failed build leaves the user's shell in a surprising working directory.
- **Alternatives considered**:
  - **`cd /d %~dp0` at the top and trust relative paths afterwards**: Works, but mutates the caller's shell `cwd` visibly. Cleaner to pass absolute paths to `dotnet`.

## R9. What "SUCCESS" prints and what exit codes mean

- **Decision**: On success, the script prints a short summary line such as `[build.cmd] OK: Release build written to D:\...\dist (tests: skipped)`. On any failure, it prints a single line identifying which step failed (`argument validation`, `SDK check`, `clean`, `build`, `test`) and re-emits the exit code of the underlying `dotnet` invocation where relevant. Exit codes: `0` on full success; `1` for argument / usage errors; `2` for a missing SDK; and the non-zero exit code of the failing `dotnet` command for build or test failures (so that CI systems see a meaningful error class).
- **Rationale**:
  - Matches FR-009 (non-zero on any failure, zero only on full success) and the constitution's Observability & Diagnosability principle ("Errors MUST identify the failing operation").
  - Propagating `dotnet`'s exit code preserves the information the .NET CLI already provides to the user, instead of collapsing every failure onto `1`.
- **Alternatives considered**:
  - **Collapse everything onto `0` / `1`**: Simpler, but loses useful signal for CI pipelines and pager alerts. Not worth the simplification.

---

**Phase 0 gate result**: All Technical Context items are resolved. Ready for Phase 1 (data model / contracts / quickstart).
