---
name: rust-checks
version: 1.0.0
title: Rust Checks
description: Rust coding conventions and correctness checks for coding agents
authors: cop-team
tags: rust, coding-standards, correctness
language: Rust
dependencies: rust
---

# Rust Checks

Rust correctness, style, complexity, safety, performance, and documentation checks for
coding agents — a curated set of [Clippy](https://doc.rust-lang.org/clippy/)-inspired lints
implemented as deterministic static checks (no Rust toolchain required).

These checks hardcode the Rust provider via `codebase(rust.parse())`, so they run with
just a target directory — no `-p` flag required:

```bash
cop rust-checks -t .
```

## What it checks

| Check | Group | Clippy analogue |
|---|---|---|
| `unwrap-calls` | correctness | `unwrap_used` |
| `expect-calls` | correctness | `expect_used` |
| `panic-macros` | correctness | `panic` |
| `unfinished-code` | correctness | `todo` / `unimplemented` |
| `mem-forget` | correctness | `mem_forget` |
| `transmute-calls` | correctness | transmute lints |
| `eq-to-none` | correctness | `partialeq_to_none` |
| `console-output` | style | `print_stdout` / `dbg_macro` |
| `type-naming` / `method-naming` | style | naming conventions |
| `wildcard-imports` | style | `wildcard_imports` |
| `too-many-arguments` | complexity | `too_many_arguments` |
| `large-function` | complexity | `too_many_lines` |
| `missing-safety-doc` | safety | `missing_safety_doc` |
| `needless-clone` | performance | `redundant_clone` |
| `undocumented-types` / `undocumented-methods` | documentation | missing-docs |

Checks are grouped into the aggregates `rust-correctness-checks`, `rust-style-checks`,
`rust-complexity-checks`, `rust-safety-checks`, `rust-perf-checks`, and `rust-doc-checks`,
all combined into `rust-checks`. Test, bench, example, and `build.rs` files are excluded
from the production-source checks.

## Excluding checks and violations

**Whole rule** — subtract a check (or group) from the package in a `.cop` file:

```cop
import rust-checks
import code
let my-checks = rust-checks - panic-macros - needless-clone
command MAIN = CHECK(my-checks)
```

**Single instance** — add a `// cop-ignore: <check>` comment on the line directly above the
one to exempt (works for the statement/line-level checks: `unwrap-calls`, `expect-calls`,
`panic-macros`, `unfinished-code`, `mem-forget`, `transmute-calls`, `console-output`,
`needless-clone`, `eq-to-none`, `wildcard-imports`):

```rust
// cop-ignore: unwrap-calls
let raw = std::fs::read_to_string(path).unwrap();
```

See the [Rust walkthrough](../../docs/languages/rust.md) for full details.
