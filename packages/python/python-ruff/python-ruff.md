# Python Ruff Integration

Runs the [Ruff](https://docs.astral.sh/ruff/) Python linter and exposes its diagnostics as cop Violations.

## Prerequisites

- **ruff** must be installed and on PATH (`pip install ruff`)
- **python** must be installed and on PATH

## Usage

```cop
import python-ruff

command MAIN = CHECK(python-ruff.checks())
```

Combine with custom cop rules:

```cop
import python-ruff
import python-checks

command MAIN = CHECK(python-ruff.checks() + python-checks)
```

## How it works

1. The package runs `ruff check --output-format=json .` against your project
2. Parses ruff's JSON output into Diagnostic objects
3. Maps each Diagnostic to a cop Violation (Severity, Message, File, Line, Source)
4. Ruff rule codes starting with `E` are mapped to `error` severity; others to `warning`

## Exported

- `checks() : [Violation]` — all ruff diagnostics as Violations
- `command MAIN` — runs CHECK on all ruff violations
