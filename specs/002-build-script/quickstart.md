# Quickstart: Build Command Script

**Feature**: 002-build-script
**Date**: 2026-04-13
**Audience**: developers and release engineers working in the `windows-keyboard-layout-fix` repository.

This quickstart shows the everyday use of `build.cmd` and maps each step to the acceptance scenarios and success criteria in `spec.md`, so that a manual verifier can walk the document top-to-bottom and tick off every check.

## Prerequisites

- Windows 10 or Windows 11 developer machine.
- A recent **.NET 8 SDK** installed and available on `PATH`. Confirm with:

  ```text
  dotnet --list-sdks
  ```

  The command must exit with `0` and list at least one `8.x.y` entry. If it does not, install from <https://dot.net> before continuing.
- A clone of this repository. All commands below are run from a standard `cmd.exe` or PowerShell prompt; nothing needs to be installed beyond the SDK.

## Scenario 1: Default Debug build (P1)

Maps to: US1 acceptance scenario 1, FR-001, FR-002, SC-001.

1. Open a new prompt anywhere and `cd` into the repository root.
2. Run:

   ```text
   build.cmd
   ```

3. Observe a one-line banner printing the configuration (`debug`), the resolved output directory (`<repoRoot>\dist`), and whether tests will run (`skipped`).
4. Wait for the `dotnet publish` output to finish.
5. Confirm that the script prints `[build.cmd] OK: Debug build written to <repoRoot>\dist (tests: skipped)` and returns exit code `0`. Check with `echo %ERRORLEVEL%`.
6. Confirm that `dist\kbfix.exe` exists and that launching it behaves identically to `dotnet run --project src\KbFix` in Debug.

## Scenario 2: Explicit Release build (P1)

Maps to: US2 acceptance scenario 1, FR-004, SC-002.

1. From the repository root, run:

   ```text
   build.cmd release
   ```

2. Confirm the banner identifies `release` as the configuration.
3. Confirm the previous `dist\` contents from Scenario 1 have been removed before the build started (maps to FR-007 and SC-006).
4. Confirm the produced `dist\kbfix.exe` is a Release-configured single-file self-contained executable runnable without further steps (maps to FR-017, SC-004).

## Scenario 3: Debug default is equivalent to explicit debug (P1)

Maps to: US2 acceptance scenario 3.

1. Run `build.cmd debug`, note the contents of `dist\`.
2. Run `build.cmd` (no arguments), observe that `dist\` is cleaned and repopulated.
3. Confirm that the two runs produce equivalent artifacts (same filenames and sizes, same exit code, same banner except for timestamps).

## Scenario 4: Invalid configuration argument (P1)

Maps to: US2 acceptance scenario 4, FR-006, SC-003.

1. Run:

   ```text
   build.cmd staging
   ```

2. Observe that the script prints `[build.cmd] FAILED at argparse: unknown configuration 'staging'. Accepted: debug, release.` (or equivalent wording), prints the usage message, and exits non-zero (`echo %ERRORLEVEL%` returns `1`).
3. Confirm that `dist\` was **not** touched.

## Scenario 5: Discoverable help (P2)

Maps to: US3 acceptance scenario 1, FR-011, SC-005.

1. Run any of the following:

   ```text
   build.cmd --help
   build.cmd -h
   build.cmd /?
   ```

2. Confirm the output lists: the synopsis, the default configuration, the default output directory, every supported switch, and at least one example per supported flag.
3. Confirm the exit code is `0` and that **no build** was performed (`dist\` is unchanged).

## Scenario 6: Run tests as part of the build (P2)

Maps to: US3 acceptance scenario 2, FR-012.

1. Run:

   ```text
   build.cmd release --test
   ```

2. Observe the banner reports `tests: run`.
3. Wait for the Release publish step, then for `dotnet test` output.
4. On green tests, confirm the final line is `[build.cmd] OK: Release build written to <repoRoot>\dist (tests: run)` and exit code is `0`.
5. On red tests (optional negative check — e.g. temporarily mark a test as failing): confirm the script prints `[build.cmd] FAILED at test: ...` and exits with `dotnet test`'s own non-zero code.

## Scenario 7: Custom output directory (P2)

Maps to: US3 acceptance scenario 3, FR-013.

1. Run:

   ```text
   build.cmd debug --output out\local
   ```

2. Confirm that `out\local\kbfix.exe` exists after the build.
3. Confirm that `dist\` is **untouched** (if it already existed from a previous run, its contents must still be there).
4. Repeat with an absolute path (e.g. `build.cmd debug --output C:\Temp\kbfix-drop`) and confirm the same behaviour.
5. Safety check: run `build.cmd --output .` and confirm it fails with an argparse error instead of attempting to clean the repository root.

## Scenario 8: Skip the clean step (P2)

Maps to: US3 acceptance scenario 4, FR-014.

1. Produce any build (e.g. `build.cmd release`) and create a sentinel file inside `dist\`:

   ```text
   echo marker > dist\sentinel.txt
   ```

2. Run:

   ```text
   build.cmd debug --no-clean
   ```

3. Confirm that `dist\sentinel.txt` still exists after the run, alongside the newly produced Debug artifacts.

## Scenario 9: Order independence (P2)

Maps to: FR-015.

1. Run `build.cmd release --test` and `build.cmd --test release` back-to-back.
2. Confirm both runs produce the same banner content (modulo timestamps), the same exit code, and the same final `dist\` state.

## Scenario 10: Run from a non-root working directory

Maps to: FR-010, the edge case about running from `src/`.

1. From the repository root, run `cd src`.
2. Invoke the script via its full path, e.g. `..\build.cmd release`.
3. Confirm that `dist\` still materialises at the **repository root**, not inside `src\dist\`.

## Scenario 11: Missing SDK

Maps to: FR-008, US1 acceptance scenario 3.

1. On a machine or PATH configuration without `dotnet`, run `build.cmd`.
2. Confirm the script prints the actionable "install .NET 8 SDK from https://dot.net" message, identifies the stage as `sdk`, and exits with code `2`.

## Sign-off

All eleven scenarios pass → the feature is ready to ship. Any scenario that does not pass must be fixed before the feature is considered complete; recording the failure in `specs/002-build-script/checklists/` is optional but recommended.
