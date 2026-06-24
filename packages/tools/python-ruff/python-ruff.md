# Python Ruff Integration

Runs the [Ruff](https://docs.astral.sh/ruff/) Python linter and exposes its findings as cop violations.

## Prerequisites

- **ruff** must be installed and on PATH (`pip install ruff`)
- **python** must be installed and on PATH

## Usage

```cop
import python-ruff

command MAIN = CHECK(python-ruff.diagnostics())
```

Combine with custom cop rules:

```cop
import python-ruff
import python-checks

command MAIN = CHECK(python-ruff.diagnostics() + python-checks)
```

## How it works

1. The package runs `ruff check --output-format=json .` against your project
2. Parses ruff's JSON output into Violation objects
3. Maps each Ruff finding to a cop Violation (Severity, Message, File, Line, Source)
4. Ruff rule codes starting with `E` are mapped to `error` severity; others to `warning`

## Exported

- `diagnostics() : [Violation]` — all Ruff violations
- `ruff-checks` — the provider-backed Ruff violation collection
- `command MAIN` — runs CHECK on all Ruff violations
