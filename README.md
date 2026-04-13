# kbfix

A small Windows utility that removes keyboard layouts from the current Windows
session that are not part of the user's persisted (HKCU) keyboard
configuration. It targets the long-standing Windows annoyance where Remote
Desktop (and a few other system-level events) silently inject extra layouts
into a session even though the language settings remain unchanged.

The tool is one self-contained `.exe`. It needs no installer, no
Administrator rights, and writes nothing to disk.

## Use

```powershell
.\kbfix.exe              # one-shot fix
.\kbfix.exe --dry-run    # preview only
.\kbfix.exe --help       # full usage
```

Exit codes: `0` success or no-op, `1` failure, `2` unsupported platform,
`3` refused (e.g. empty persisted set), `64` usage error.

## Build

A `build.cmd` script at the repository root wraps `dotnet publish` and drops
a runnable, self-contained `kbfix.exe` into `dist\`:

```cmd
build.cmd                       :: Debug build (default) into dist\
build.cmd release               :: Release build into dist\
build.cmd release --test        :: Release build, then run the test suite
build.cmd debug --output out\x  :: custom output directory
build.cmd --no-clean            :: keep prior artifacts in dist\
build.cmd --help                :: list every switch and default
```

The script validates that a .NET 8 SDK is on `PATH`, cleans the output
directory, runs `dotnet publish` for the selected configuration, optionally
runs `dotnet test` against `KbFix.Tests`, and propagates `dotnet`'s exit code.
See [`specs/002-build-script/contracts/cli.md`](specs/002-build-script/contracts/cli.md)
for the full contract.

## Run and verify

See [`specs/001-fix-keyboard-layouts/quickstart.md`](specs/001-fix-keyboard-layouts/quickstart.md)
for the run instructions and the **mandatory manual RDP verification** that
gates every release per the project constitution.

## v1 scope

Removal-only, one-shot, current-user session, no GUI, no background watcher.
A resident watcher mode that re-runs the cleanup automatically when Windows
re-injects a layout is **explicitly out of scope for v1** and reserved for a
possible later iteration.

## Specification

The full feature spec, plan, contracts, and task list live under
`specs/001-fix-keyboard-layouts/` (the utility itself) and
`specs/002-build-script/` (the `build.cmd` entry point).

## How this project was built

Every artifact in this repository — the C# source under `src/`, the tests
under `tests/`, `build.cmd`, the constitution under `.specify/memory/`, and
every document under `specs/` — was produced by [Claude Code](https://claude.com/claude-code)
running the **Claude Opus 4.6 (1M context)** model, driven by the
[GitHub Spec Kit](https://github.com/github/spec-kit) workflow
(`/speckit.specify` → `/speckit.plan` → `/speckit.tasks` → `/speckit.implement`).
The human author's role was limited to stating intent in Czech at each
speckit phase, reviewing the generated plans, and running the manual
verification steps that the constitution requires.
