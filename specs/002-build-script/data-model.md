# Phase 1 Data Model: Build Command Script

**Feature**: 002-build-script
**Date**: 2026-04-13

This feature is a command-line wrapper, not a data-processing component. Nothing is persisted on disk beyond build artifacts the .NET toolchain already produces. The "data model" below therefore describes the small set of runtime values the script derives from its arguments and environment, the validation rules those values obey, and the single lifecycle they go through: *parse → validate → execute → report*.

## Entities

### BuildInvocation

A single run of `build.cmd`. Exists only for the lifetime of the process. Holds all resolved inputs and the final result.

| Field | Type | Source | Rules |
|-------|------|--------|-------|
| `configuration` | enum: `Debug` \| `Release` | first positional argument; falls back to `Debug` when absent | Case-insensitive match against the fixed set; any other value is a validation error. |
| `outputDirectory` | absolute filesystem path | `--output <path>` (optional); defaults to `<repoRoot>\dist` | If user-supplied and relative, resolved against `<repoRoot>`. Must not equal `<repoRoot>` itself, `<repoRoot>\src`, `<repoRoot>\tests`, or `<repoRoot>\.git` (safety guard against catastrophic clean). |
| `clean` | boolean | `--no-clean` switch (default `true`) | When `true`, the output directory is removed and recreated before the build. |
| `runTests` | boolean | `--test` switch (default `false`) | When `true`, after a successful build the test suite is executed with the same `configuration`. |
| `helpOnly` | boolean | `--help` / `-h` / `/?` switch (default `false`) | When `true`, the script prints usage and exits `0` without parsing further inputs. |
| `repoRoot` | absolute filesystem path | derived from `%~dp0` at script start | The directory in which `build.cmd` itself lives; treated as the repository root. Immutable for the run. |
| `solutionPath` | absolute filesystem path | `<repoRoot>\KbFix.sln` | Must exist (existence check happens just before the build step). |
| `publishProjectPath` | absolute filesystem path | `<repoRoot>\src\KbFix\KbFix.csproj` | Must exist. |
| `result` | enum: `Succeeded` \| `Failed` | computed at the end of the run | `Succeeded` only if every step that actually ran returned `0`. |
| `failedStage` | enum: `argparse` \| `sdk` \| `clean` \| `build` \| `test` \| (none) | computed at the end of the run | Set iff `result == Failed`; identifies which stage produced the non-zero exit. |
| `exitCode` | integer | computed at the end of the run | `0` on success; `1` for argparse errors; `2` for missing SDK; otherwise the exit code of the failing `dotnet` invocation. |

### BuildStage

An ordered sequence of work the invocation performs. Not a persisted record — it is the implicit list of labels the script uses in its output and in `failedStage`.

1. `argparse` — walk `%*`, populate the `BuildInvocation` fields, validate them.
2. `sdk` — confirm `dotnet` is on `PATH` and `dotnet --list-sdks` succeeds.
3. `clean` — if `clean == true`, remove and recreate `outputDirectory`.
4. `build` — `dotnet publish "<publishProjectPath>" -c <Configuration> -o "<outputDirectory>"` (Release or Debug).
5. `test` — only if `runTests == true` and `build` succeeded: `dotnet test "<solutionPath>" -c <Configuration> --nologo`.
6. `report` — print the one-line summary and return `exitCode`.

Stages execute in the order above; a non-zero exit from any stage short-circuits the remaining stages (except `report`, which always runs).

## Validation rules

These rules are enforced during stage 1 (`argparse`) unless noted otherwise.

- **V-CONFIG-ENUM**: The positional configuration token, if present, must match `debug` or `release` case-insensitively. Anything else fails with exit `1`.
- **V-CONFIG-ONE**: At most one positional configuration token is allowed. A second positional token fails with exit `1`.
- **V-OUTPUT-REQ-VALUE**: `--output` (or `--output=`) must be followed by a non-empty path. A bare `--output` at end of line fails with exit `1`.
- **V-OUTPUT-NOT-REPO**: After resolution, the absolute `outputDirectory` must not equal `repoRoot`, `<repoRoot>\src`, `<repoRoot>\tests`, or `<repoRoot>\.git`. Violating this fails with exit `1` (safety guard — the clean step would otherwise nuke source code).
- **V-UNKNOWN-FLAG**: Any token that begins with `-` or `--` and is not a recognised switch fails with exit `1` and prints usage.
- **V-HELP-SHORT-CIRCUIT**: If any help flag appears anywhere in `%*`, `helpOnly` is set to `true` and all other validation is skipped — the script prints usage and returns `0`.
- **V-SDK-PRESENT**: Stage `sdk` must observe `dotnet --list-sdks` exiting with code `0`. Otherwise the script fails with exit `2` and prints the "install .NET 8 SDK from https://dot.net" message.
- **V-SOLUTION-EXISTS**: Stage `build` must observe that `solutionPath` and `publishProjectPath` exist. Otherwise the script fails with exit `1` and prints a "not inside a KbFix checkout?" hint (should never happen in practice because the script is shipped inside the repo, but guards against a renamed/deleted solution file).

## State transitions

```text
(start)
  │
  ├── argparse ──fail──> Failed(failedStage=argparse, exitCode=1)
  │       │
  │       └── helpOnly? ──yes──> Succeeded(exitCode=0, no further stages)
  │
  ├── sdk ──fail──> Failed(failedStage=sdk, exitCode=2)
  │
  ├── clean ──fail──> Failed(failedStage=clean, exitCode=<dotnet/rmdir exit>)
  │
  ├── build ──fail──> Failed(failedStage=build, exitCode=<dotnet exit>)
  │
  ├── runTests? ──no──> Succeeded(exitCode=0)
  │              │
  │              yes
  │              │
  │              └── test ──fail──> Failed(failedStage=test, exitCode=<dotnet exit>)
  │
  └── Succeeded(exitCode=0)
```

Every Failed state still executes the `report` stage so that the user sees a one-line summary identifying the failing stage before `cmd.exe` returns the exit code.

## Notes

- No entity in this model is ever written to disk. The only on-disk side effect of the whole feature is the contents of `outputDirectory`, which is owned by `dotnet publish`.
- The `configuration` enum deliberately contains exactly two values. Adding a third (`Staging`, `Profiling`, ...) would require a matching MSBuild configuration in `KbFix.sln`; the script is not the right layer to invent configurations.
- `BuildInvocation` has no persistence, no serialization, no API — it is a conceptual grouping of script-local variables. It is documented here so that the contract and tasks phases can reference named fields instead of ad-hoc "the configuration argument".
