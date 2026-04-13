# CLI Contract

**Feature**: 001-fix-keyboard-layouts
**Artifact**: `kbfix.exe` (working name; final name may change at packaging time)

The utility's only external interface is its command line, its stdout/stderr
streams, and its process exit code. This document is the contract.

## Synopsis

```text
kbfix [--dry-run] [--quiet] [--help] [--version]
```

No positional arguments. No subcommands. No config files.

## Options

| Flag             | Alias  | Meaning                                                                                              |
|------------------|--------|------------------------------------------------------------------------------------------------------|
| `--dry-run`      | `--preview` | Inspect and report only. Do not modify session state. Required by FR-010.                       |
| `--quiet`        | `-q`   | Suppress the persisted/session sections. Still prints actions and result. Errors still go to stderr. |
| `--help`         | `-h`, `-?` | Print usage to stdout and exit 0.                                                               |
| `--version`      |        | Print the build version (semver) to stdout and exit 0.                                              |

Unknown flags MUST cause exit code 64 (`EX_USAGE`) with an error on stderr.

## Stdout format (default verbosity)

Plain UTF-8 text. Four labelled sections in fixed order, separated by blank
lines. Section headers are exact strings.

```text
Persisted layouts:
  - 0405  00000405  Czech (QWERTY)
  - 0409  00000409  English (United States)

Session layouts:
  - 0405  00000405  Czech (QWERTY)
  - 0405  00010405  Czech (QWERTZ)            <-- session-only
  - 0409  00000409  English (United States)
  - 0409  00020409  United States-International <-- session-only

Actions:
  - SwitchActive 0405 00000405 (was 0405 00010405): OK
  - Deactivate   0405 00010405: OK
  - Deactivate   0409 00020409: OK

Result: SUCCESS — removed 2 session-only layout(s); persisted set unchanged.
```

Each layout line uses three space-separated columns:
`<langid hex 4>  <klid hex 8>  <human readable name>`. The hex values are
the contract; the human-readable name is best-effort and MAY be empty if
Windows does not return one.

In `--dry-run`, every action line is prefixed with `(dry-run) ` and the
result line begins with `DRY-RUN: `.

In `--quiet`, the `Persisted layouts:` and `Session layouts:` sections are
omitted; `Actions:` and `Result:` are still printed.

## Stderr format

Stderr is reserved for failure detail and warnings. The structured stdout
report MUST NOT be split across stderr.

On failure, stderr ends with one line of the form:
```text
ERROR: <one-line summary>
```
preceded by any underlying Win32/HRESULT detail.

## Exit codes

| Code | Meaning                                                                              |
|------|--------------------------------------------------------------------------------------|
| 0    | Success — including the no-op case where nothing needed to change.                   |
| 1    | Generic failure (unhandled exception, partial reconciliation, verification failed).  |
| 2    | Unsupported platform — required Windows API surface is unavailable (FR-014).         |
| 3    | Refused — persisted layout set is empty, or no fallback layout available.            |
| 64   | Usage error — unknown flag, malformed arguments.                                     |

A run that prints `Result: SUCCESS` on stdout MUST exit 0. A run that prints
`Result: NO-OP` MUST also exit 0. Any other `Result:` value MUST map to a
non-zero exit code per the table above. This is a hard contract — tests will
assert it.

## Behavioural invariants (binding on the implementation)

1. **No-op idempotency**: invoking `kbfix` immediately after a successful
   `kbfix` run MUST produce `Result: NO-OP` and exit 0. (FR-007, SC-003.)
2. **Persisted is read-only**: across the lifetime of any run (success or
   failure), the contents of `HKCU\Keyboard Layout\Preload` and
   `HKCU\Keyboard Layout\Substitutes` MUST be byte-identical before and
   after. (FR-006, SC-004.)
3. **Never zero layouts**: at no point during a run may the session's active
   keyboard layout count drop to zero. (FR-008, SC-005.)
4. **Switch-before-remove**: if the foreground active layout is in the
   removal set, the `SwitchActive` action MUST be emitted (and succeed)
   before any `Deactivate` action targeting that layout. (FR-009.)
5. **No elevation**: the binary MUST run successfully under a standard,
   non-Administrator user token. (FR-013.)
6. **No background residency**: the process MUST exit when the run finishes.
   It MUST NOT spawn child processes that outlive it, register services,
   register run-on-logon entries, or write any persistent state. (FR-015.)
