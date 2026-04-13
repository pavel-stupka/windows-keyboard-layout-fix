# Feature Specification: Build Command Script

**Feature Branch**: `002-build-script`
**Created**: 2026-04-13
**Status**: Draft
**Input**: User description: "Vytvor build prikaz.cmd, ktery sestavi aplikaci do dist - build.cmd debug pro debug, build.cmd release pro release, vychozi bude debug. Pripadne pridej dalsi parametry, ktere uznas za vhodne"

## User Scenarios & Testing *(mandatory)*

### User Story 1 - One-command Debug Build (Priority: P1)

A developer working on the KbFix utility wants to produce a runnable build of the application with a single command from the repository root, without needing to remember the underlying .NET CLI invocation, target framework, runtime identifier, or output path. Running the command with no arguments produces a Debug build and places the resulting artifacts under the `dist/` folder at the repository root, ready to be launched or copied to a test machine.

**Why this priority**: This is the most frequent developer workflow — iterating on the utility and quickly verifying behaviour. Without a one-command path to a fresh build, every developer must memorise the exact `dotnet publish` invocation, which is error-prone and slows the inner loop. This single story delivers immediate daily value on its own.

**Independent Test**: From a clean checkout on a Windows machine with the .NET 8 SDK installed, run `build.cmd` from the repository root. Verify that the command exits with success, that a `dist/` folder exists at the repository root, and that it contains a runnable `kbfix.exe` built in Debug configuration. Launching the produced executable must behave identically to running the application via `dotnet run` in Debug.

**Acceptance Scenarios**:

1. **Given** a clean repository checkout on Windows with the .NET 8 SDK installed, **When** the developer runs `build.cmd` from the repository root with no arguments, **Then** a Debug build is produced, placed under `dist/`, and the script exits with status code 0.
2. **Given** a previous build exists in `dist/`, **When** the developer runs `build.cmd` again, **Then** stale artifacts from the previous build are cleared before the new build runs, so that `dist/` always reflects the current build configuration.
3. **Given** the .NET 8 SDK is not installed or not on PATH, **When** the developer runs `build.cmd`, **Then** the script stops with a clear error message explaining that the SDK is required, and exits with a non-zero status code.

---

### User Story 2 - Explicit Debug / Release Selection (Priority: P1)

The same developer (or a release engineer preparing a drop for distribution) wants to be able to explicitly choose between a Debug and a Release build from the same single command, so that the same script serves both inner-loop development and release packaging. The user types `build.cmd debug` or `build.cmd release` and the script produces the corresponding configuration in `dist/`. Omitting the argument falls back to Debug.

**Why this priority**: Equally important as Story 1 for release engineering. Without an explicit Release path, producing a publishable build of the utility still requires hand-crafted `dotnet publish` commands, which defeats the point of having a build script. Making Debug the default keeps the everyday developer workflow frictionless while still giving release engineers a one-liner for Release.

**Independent Test**: On a machine with the .NET 8 SDK installed, run `build.cmd release` from the repository root; verify that `dist/` contains a Release build of `kbfix.exe`. Then run `build.cmd debug` and verify that `dist/` is replaced with a Debug build. Finally run `build.cmd` with no arguments and verify the result is equivalent to `build.cmd debug`.

**Acceptance Scenarios**:

1. **Given** the developer runs `build.cmd release`, **When** the build completes successfully, **Then** the artifacts in `dist/` are built in Release configuration and are suitable for distribution.
2. **Given** the developer runs `build.cmd debug`, **When** the build completes successfully, **Then** the artifacts in `dist/` are built in Debug configuration.
3. **Given** the developer runs `build.cmd` with no configuration argument, **When** the build completes successfully, **Then** the result is identical to `build.cmd debug`.
4. **Given** the developer runs `build.cmd` with an unrecognised configuration argument (e.g. `build.cmd staging`), **When** the script validates its arguments, **Then** it refuses to run, prints the accepted configurations and usage help, and exits with a non-zero status code.

---

### User Story 3 - Discoverable Usage Help and Extra Switches (Priority: P2)

A developer who is new to the repository (or returning after a break) wants to discover what the build script can do without reading its source. Running `build.cmd --help` (or `/?`) prints a short usage message that lists the accepted configurations, the default configuration, the output directory, and any additional switches the script supports. In addition, the script offers a small set of optional switches that cover common needs beyond "just build": skipping the pre-build clean of `dist/`, running the test suite as part of the build, and selecting a different output directory than the default `dist/`.

**Why this priority**: Nice to have rather than strictly required — the P1 stories already deliver a usable build command. But a discoverable `--help` and a handful of well-chosen switches (clean/no-clean, test, custom output directory) dramatically reduce friction for contributors and for CI pipelines that need slightly different behaviour without forking the script.

**Independent Test**: Run `build.cmd --help` and verify the output lists the accepted configurations, the default, and every supported switch. Then exercise each switch in isolation and verify the observable effect (e.g. `--no-clean` preserves previous artifacts; `--test` runs the test suite after a successful build; `--output <path>` writes to the specified folder instead of `dist/`).

**Acceptance Scenarios**:

1. **Given** the developer runs `build.cmd --help` or `build.cmd /?`, **When** the script starts, **Then** it prints a usage message covering configurations, default, output directory, and switches, and exits with status code 0 without performing a build.
2. **Given** the developer runs `build.cmd release --test`, **When** the Release build completes successfully, **Then** the full test suite is executed and its success or failure is reflected in the script exit code.
3. **Given** the developer runs `build.cmd debug --output out/local`, **When** the build completes successfully, **Then** the artifacts are placed under `out/local` instead of `dist/` and `dist/` is left untouched.
4. **Given** the developer runs `build.cmd --no-clean`, **When** the build runs, **Then** any pre-existing content in the output directory is preserved and only overwritten where the new build writes to the same paths.

---

### Edge Cases

- The repository root contains an existing `dist/` folder with files that are read-only or currently open in another process — the script must surface the failure with a clear message rather than partially deleting content and then silently continuing.
- The developer runs the script from a working directory other than the repository root (e.g. from inside `src/`) — the script must still locate the solution correctly and produce `dist/` at the repository root, not at the current working directory.
- The developer passes flags and the positional configuration in different orders (e.g. `build.cmd --test release` vs `build.cmd release --test`) — both orderings must be accepted and produce identical results.
- The `.NET SDK` is installed but the solution fails to compile — the script must not produce a half-populated `dist/` folder; the previous successful build, if any, should either be preserved or the failure should be unambiguous so the developer knows not to ship whatever is on disk.
- The developer runs `build.cmd --test` with a configuration whose build step fails — the test step must be skipped and the overall script exit code must reflect the build failure.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The repository MUST provide a single entry-point batch script named `build.cmd` at the repository root that developers can invoke directly from a standard Windows command prompt or PowerShell session.
- **FR-002**: Running `build.cmd` with no arguments MUST produce a Debug build of the KbFix application, equivalent to running `build.cmd debug`.
- **FR-003**: Running `build.cmd debug` MUST produce a Debug build of the KbFix application and place the resulting artifacts under a top-level `dist/` folder at the repository root.
- **FR-004**: Running `build.cmd release` MUST produce a Release build of the KbFix application and place the resulting artifacts under the top-level `dist/` folder at the repository root.
- **FR-005**: The positional configuration argument MUST be case-insensitive, so that `Debug`, `DEBUG`, `debug`, `Release`, and `RELEASE` are all accepted and treated as equivalent to their lowercase form.
- **FR-006**: If the configuration argument is present but is not one of the accepted values, the script MUST refuse to run, print a clear error message that lists the accepted values, and exit with a non-zero status code.
- **FR-007**: Before producing a new build, the script MUST clear any stale contents of the output directory so that `dist/` always reflects the configuration and sources of the most recent successful build, unless the developer explicitly opts out via a `--no-clean` switch.
- **FR-008**: The script MUST validate that a compatible .NET 8 SDK is available on the machine before attempting to build, and MUST fail with a clear, actionable error message if it is not.
- **FR-009**: The script MUST return a non-zero exit code if any step of the build (or the optional test step) fails, and MUST return zero only when every step that it actually performed succeeded.
- **FR-010**: The script MUST locate the repository root and the solution file relative to its own location, so that it works correctly regardless of the current working directory from which it is invoked.
- **FR-011**: The script MUST expose a `--help` (alias `/?`) switch that prints a short usage message describing accepted configurations, the default configuration, the default output directory, and every supported switch, and then exits with status code 0 without performing a build.
- **FR-012**: The script MUST expose a `--test` switch that, after a successful build, runs the project's automated test suite against the same configuration and reflects its outcome in the script exit code.
- **FR-013**: The script MUST expose an `--output <path>` switch that redirects build artifacts to the specified directory instead of the default `dist/` folder. The path MAY be absolute or relative to the repository root.
- **FR-014**: The script MUST expose a `--no-clean` switch that skips the pre-build clean of the output directory, so that developers or CI jobs can preserve prior artifacts alongside the new build.
- **FR-015**: The script MUST accept switches and the positional configuration in any order, so that `build.cmd release --test` and `build.cmd --test release` are equivalent.
- **FR-016**: The script MUST echo to the console, at minimum, which configuration it is building, which output directory it is using, and whether the test step will run, so that the developer can confirm at a glance that the invocation matches their intent.
- **FR-017**: The produced artifacts in the output directory MUST be runnable on a supported Windows target without requiring the developer to manually copy additional files from the source tree.

### Key Entities *(include if feature involves data)*

- **Build script**: The `build.cmd` file at the repository root. Accepts a positional configuration argument and a small set of switches, and is responsible for validating inputs, locating the solution, cleaning the output directory, invoking the build (and optionally the tests), and surfacing the outcome via its exit code.
- **Output directory**: The folder that holds the artifacts of the most recent successful build. Defaults to `dist/` at the repository root; can be overridden via `--output`. Treated as a disposable, script-owned folder — its contents are expected to be replaced on every run unless `--no-clean` is passed.
- **Build configuration**: The logical selection between Debug and Release. Expressed as a case-insensitive positional argument to the script, with Debug as the default when the argument is omitted.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A developer on a freshly cloned repository, with a supported .NET 8 SDK already installed, can produce a runnable Debug build of the application with a single command (`build.cmd`) and zero additional setup steps.
- **SC-002**: The same developer can switch between Debug and Release builds by changing only the first argument of that single command, with no other edits to files, environment variables, or configuration.
- **SC-003**: Running the build script on an invalid configuration argument or on a machine without the required SDK produces an error message that, in a user survey or informal review, is rated "immediately actionable" by at least 9 out of 10 developers — i.e. the reader knows exactly what to do next without reading source code.
- **SC-004**: After any successful run of the build script, 100% of the files needed to launch the application are present in the output directory, so that the folder can be copied to another supported Windows machine and the application can be started from there without further steps.
- **SC-005**: Running `build.cmd --help` surfaces every supported switch and the default configuration, so that a contributor who has never seen the script before can discover its full capabilities in under 30 seconds without reading its source.
- **SC-006**: Running `build.cmd` twice in a row with the same arguments leaves `dist/` in an identical state both times, with no files left over from the first run that do not belong to the second (unless `--no-clean` was explicitly requested).

## Assumptions

- The script targets Windows developer machines only. A matching shell script for bash/PowerShell on non-Windows platforms is out of scope for this feature, consistent with the project's Windows-only nature.
- A compatible .NET 8 SDK is expected to be installed and available on `PATH` on any machine that runs the script. The script validates its presence but does not attempt to install or bootstrap it.
- The default output directory is `dist/` at the repository root. This folder is treated as disposable and script-owned; developers are not expected to place hand-authored files there, and the script is free to delete its contents during a clean step.
- The solution to build is the existing `KbFix.sln` at the repository root, which already exposes both `Debug` and `Release` solution configurations. The script delegates the actual compile and publish steps to the standard .NET build toolchain, and does not introduce a parallel build system.
- The automated test suite referenced by the `--test` switch is the existing `tests/KbFix.Tests` project referenced from the solution. No new test infrastructure is introduced by this feature.
- Debug is chosen as the default configuration (rather than Release) on the assumption that the most frequent caller of the script is a developer iterating on the code, not a release engineer cutting a drop. Release engineers explicitly pass `release`.
- Flag parsing is expected to be straightforward positional + GNU-style `--flag` / `--flag value` handling. No attempt is made to support POSIX short flags, flag bundling, or response files in this first iteration.
