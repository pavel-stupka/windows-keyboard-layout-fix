# kbfix

A small Windows utility that removes keyboard layouts from the current Windows
session that are not part of the user's persisted (HKCU) keyboard
configuration. It targets the long-standing Windows annoyance where Remote
Desktop (and a few other system-level events) silently inject extra layouts
into a session even though the language settings remain unchanged.

The tool is one self-contained `.exe` that needs no Administrator rights.
The one-shot mode writes nothing to disk; the optional background watcher
(see `--install` below) stages a per-user copy under `%LOCALAPPDATA%\KbFix\`
and is fully reversible with `--uninstall`.

## Use

### Easiest: double-click the wrappers

A release build (`build.cmd release`) drops `kbfix.exe` into `dist\` along
with three small batch wrappers you can double-click from Explorer — no
terminal needed:

| Wrapper         | What it does                                          |
|-----------------|-------------------------------------------------------|
| `install.cmd`   | Installs the background watcher for the current user. |
| `uninstall.cmd` | Stops the watcher and removes everything.             |
| `status.cmd`    | Reports whether the watcher is running.               |

Each wrapper opens a console window, runs `kbfix.exe` with the matching
flag, and pauses at the end so you can read the output before closing the
window. The same release build also drops a [`README.txt`](dist-README.txt)
next to the wrappers with the full quick-start, troubleshooting notes, and
exit-code reference for end users.

### From a terminal — one-shot mode

Performs a single cleanup pass and exits. Unchanged from v1:

```powershell
.\kbfix.exe              # one-shot fix
.\kbfix.exe --dry-run    # preview only
.\kbfix.exe --help       # full usage
```

### From a terminal — background watcher

`--install` stages the binary under `%LOCALAPPDATA%\KbFix\`, registers
per-user autostart via `HKCU\...\Run\KbFixWatcher`, and launches a
persistent watcher that re-runs the fix every couple of seconds for the
rest of the user session. Per-user, no elevation, fully reversible.

```powershell
.\kbfix.exe --install    # set it and forget it
.\kbfix.exe --status     # running + autostart state + log path
.\kbfix.exe --uninstall  # stop watcher, clean everything up
```

The watcher logs to `%LOCALAPPDATA%\KbFix\watcher.log`. Set
`KBFIX_DEBUG=1` in your environment before installing if you want
per-poll-cycle DEBUG output in the log.

### Exit codes

`0` success / no-op / `--status: installed and healthy`, `1` failure,
`2` unsupported platform, `3` refused (e.g. empty persisted set),
`64` usage error. `--status` additionally uses: `10` not installed,
`11` installed but watcher not running, `12` watcher running without
autostart, `13` autostart points at a stale path, `14` mixed/corrupt
state.

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

## Scope

Removal-only, current-user session, no GUI. Two modes:

- **One-shot** (`kbfix`) — runs once and exits. The original v1 behaviour.
- **Background watcher** (`kbfix --install` / `--watch`) — keeps running
  in the user session and re-applies the fix automatically whenever the
  session drifts (e.g. after an RDP disconnect). Added in spec 003.

No Administrator rights at any point. Published as a single self-contained
~12 MB `.exe` — no .NET runtime needed on the target machine.

## Specification

The full feature spec, plan, contracts, and task list live under:

- `specs/001-fix-keyboard-layouts/` — the original one-shot utility.
- `specs/002-build-script/` — the `build.cmd` entry point.
- `specs/003-background-watcher/` — the background watcher, `--install` /
  `--uninstall` / `--status` commands, and the trimming work that shrank
  the binary from 80+ MB to ~12 MB.

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
