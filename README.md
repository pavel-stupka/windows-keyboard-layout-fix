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

## Build, run, verify

See [`specs/001-fix-keyboard-layouts/quickstart.md`](specs/001-fix-keyboard-layouts/quickstart.md)
for the build command, the run instructions, and the **mandatory manual RDP
verification** that gates every release per the project constitution.

## v1 scope

Removal-only, one-shot, current-user session, no GUI, no background watcher.
A resident watcher mode that re-runs the cleanup automatically when Windows
re-injects a layout is **explicitly out of scope for v1** and reserved for a
possible later iteration.

## Specification

The full feature spec, plan, contracts, and task list live under
`specs/001-fix-keyboard-layouts/`.
