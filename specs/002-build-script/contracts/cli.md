# CLI Contract: `build.cmd`

**Feature**: 002-build-script
**Date**: 2026-04-13
**Location at runtime**: `<repoRoot>\build.cmd`

`build.cmd` is the only external interface introduced by this feature. The contract below fully specifies its command-line syntax, its exit codes, the text it writes to stdout/stderr, and the filesystem side effects it is allowed to produce. Anything not listed here is out of scope.

## Syntax

```text
build.cmd [configuration] [options]
```

### Configuration (positional, optional)

| Token | Meaning |
|-------|---------|
| `debug` | Build the KbFix project in Debug configuration. Default when omitted. |
| `release` | Build the KbFix project in Release configuration. |

Matching is case-insensitive: `Debug`, `DEBUG`, and `debug` are equivalent. At most one configuration token may appear on the command line.

### Options

| Option | Argument | Default | Effect |
|--------|----------|---------|--------|
| `--help`, `-h`, `/?` | — | off | Print the usage message to stdout and exit `0`. Skips all other work, including SDK validation. If any help flag is present anywhere in the argument list, it wins over every other argument. |
| `--test` | — | off | After a successful build, run `dotnet test` on `KbFix.sln` in the same configuration and fold its exit code into the script's exit code. |
| `--output <path>`, `--output=<path>` | path (absolute or relative to the repository root) | `<repoRoot>\dist` | Write build artifacts to this directory instead of `dist/`. A relative path is resolved against the directory that contains `build.cmd`. The path must not resolve to the repository root itself, `src/`, `tests/`, or `.git/`. |
| `--no-clean` | — | off (i.e. cleaning is the default) | Skip the pre-build removal of the output directory. Pre-existing files are preserved and may coexist with newly produced artifacts. |

Options may appear in any order, and may appear before or after the positional configuration token. For example, all three of the following are equivalent:

```text
build.cmd release --test
build.cmd --test release
build.cmd --test --output dist release
```

## Exit codes

| Code | Meaning | Emitted when |
|------|---------|--------------|
| `0` | Success | Every stage that actually ran returned zero. Includes the `--help` short-circuit and the no-op pattern `build.cmd --no-clean` where a prior build already matches the current configuration. |
| `1` | Usage / argument error | Unknown flag, unknown configuration token, second positional token, `--output` without a value, resolved output directory that matches a forbidden path, missing `KbFix.sln` or `src/KbFix/KbFix.csproj`. |
| `2` | .NET 8 SDK not available | `dotnet --list-sdks` returned a non-zero exit code, or `dotnet` is not on `PATH`. |
| (`dotnet` exit) | Build or test failure | The failing `dotnet publish` or `dotnet test` invocation's own exit code is propagated verbatim. The clean stage surfaces `rmdir` failures as a non-zero code, likewise propagated. |

## Output

### On success

Exactly one line to stdout, before exit:

```text
[build.cmd] OK: <Configuration> build written to <absoluteOutputDirectory> (tests: <run|skipped>)
```

Before the build starts, the script also prints a one-line banner identifying the resolved configuration, output directory, and whether `--test` will run. Everything the underlying `dotnet` CLI writes is forwarded unmodified.

### On failure

Exactly one summary line to stderr identifying the failing stage, in the form:

```text
[build.cmd] FAILED at <stage>: <short reason>
```

where `<stage>` is one of `argparse`, `sdk`, `clean`, `build`, `test`. Stage-specific detail written by subcommands (`dotnet publish`, `dotnet test`, `rmdir`) is forwarded to the user as it is produced.

### Usage message (`--help` / `/?`)

The usage message is stable and minimally wraps the syntax section above. At a minimum it must contain:

- A one-line synopsis: `Usage: build.cmd [debug|release] [options]`.
- A line stating the default configuration (`Default configuration: debug`).
- A line stating the default output directory (`Default output: <repoRoot>\dist`).
- Every supported option with its argument shape and a one-line description.
- An example invocation for each of: default Debug, explicit Release, Release with `--test`, custom `--output`, `--no-clean`.

The usage message is printed to stdout and the script exits `0`.

## Filesystem side effects

The script is allowed to touch exactly these paths:

1. The resolved output directory (`<repoRoot>\dist` by default). It may delete and recreate this directory (unless `--no-clean` is passed) and it may write files into it. It must never touch any other path under `<repoRoot>`.
2. `%TEMP%` and other ambient locations used by the .NET toolchain are the responsibility of `dotnet` itself; the script does not read or write them directly.

The script must not modify: `KbFix.sln`, anything under `src/`, anything under `tests/`, `.git/`, `.gitignore`, `.editorconfig`, `README.md`, `SPECIFICATION.md`, or anything under `specs/`. This is an invariant, not a guideline.

## Non-goals

To keep the contract small and the script simple, the following are explicitly *not* part of this interface:

- POSIX short-flag bundling (e.g. `-th`).
- Response files or `@argfile` syntax.
- Environment-variable overrides (e.g. `BUILD_CONFIGURATION=release build.cmd`). Future versions may add them under a namespace if they become necessary.
- Cross-platform support (bash/PowerShell/Linux/macOS). The script is Windows-only.
- Partial/incremental builds beyond what `dotnet publish` already provides.
- Packaging, signing, or uploading artifacts. The script produces a drop; shipping it is a separate concern.
