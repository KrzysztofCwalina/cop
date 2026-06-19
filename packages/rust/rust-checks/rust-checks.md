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

Rust coding conventions and correctness checks for coding agents, covering panicking
APIs (`unwrap`/`expect`/`panic!`), unfinished code (`todo!`/`unimplemented!`), console
output, naming conventions, and missing documentation on public items.

These checks hardcode the Rust provider via `codebase(rust.parse())`, so they run with
just a target directory — no `-p` flag required:

```bash
cop rust-checks -t .
```
