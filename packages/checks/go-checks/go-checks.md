---
name: go-checks
version: 1.0.0
title: Go Checks
description: Go coding conventions and correctness checks for coding agents
authors: cop-team
tags: go, golang, coding-standards, correctness
language: Go
dependencies: go
---

# Go Checks

Go correctness, style, complexity, and documentation checks for coding agents — a curated set
of [go vet](https://pkg.go.dev/cmd/vet) / [staticcheck](https://staticcheck.dev/) /
[revive](https://revive.run/) / golint-inspired lints implemented as deterministic static
checks (no Go toolchain required).

These checks hardcode the Go provider via `codebase(go.parse())`, so they run with just a
target directory — no `-p` flag required:

```bash
cop run go-checks -t .
```

## What it checks

| Check | Group | Tool analogue |
|---|---|---|
| `panic-calls` | correctness | staticcheck / revive |
| `os-exit` | correctness | revive deep-exit |
| `time-sleep` | correctness | revive / staticcheck |
| `console-output` | style | forbidigo / revive |
| `underscore-naming` | style | golint / revive var-naming |
| `initialism-casing` | style | golint / staticcheck ST1003 |
| `use-any` | style | staticcheck / modernize |
| `too-many-arguments` | complexity | revive argument-limit |
| `large-function` | complexity | funlen |
| `undocumented-types` | documentation | golint / revive exported |
| `undocumented-functions` | documentation | golint / revive exported |

Checks are grouped into the aggregates `go-correctness-checks`, `go-style-checks`,
`go-complexity-checks`, and `go-doc-checks`, all combined into `go-checks`. Test files
(`*_test.go`) and vendored dependencies (`vendor/`) are excluded from the production-source
checks.

## Excluding checks and violations

**Whole rule** — subtract a check (or group) from the package in a `.cop` file:

```cop
import go-checks
import code
let my-checks = go-checks - panic-calls - time-sleep
command MAIN = CHECK(my-checks)
```

**Single instance** — add a `// cop-ignore: <check>` comment on the line directly above the
one to exempt (works for the statement/line-level checks: `panic-calls`, `os-exit`,
`time-sleep`, `console-output`, `use-any`):

```go
// cop-ignore: panic-calls
panic("unreachable")
```

See the [Go walkthrough](../../docs/languages/go.md) for full details.
