---
name: cop
version: 1.0.0
title: Cop Language Analysis
description: Structural analysis of .cop source files — types, predicates, functions, imports
authors: cop-team
tags: cop, language, analysis
dependencies: [code]
provider: clr
providerEntry: Cop.Providers.CopProvider
---

# Cop Language Analysis

Provides structural analysis of `.cop` source files through the standard code analysis collections.

## Collections

All standard code collections are available, filtered to `.cop` files:

- **Types** — `type` and `flags` definitions
- **Statements** — predicates, functions, let bindings, imports, commands, and calls
- **Calls** — parsed invocations, including nested calls and calls inside function bodies
- **Lines** — raw text lines
- **Files** — .cop source files with imports and structure

Calls expose `CallTarget` and `ArgumentExpressions` as `CodeExpression` nodes. Use `Kind`,
`Name`, and `Target` to inspect identifiers and member accesses (for example, the `Providers`
member on `Program`), and `StringValue` for decoded string literals. Invocation nodes expose
their arguments through `Children`; unsupported expression forms have `Kind == other`.
Comments and plain string literals do not produce calls.

Malformed source retains a file and reports `ParseErrors` rather than becoming an empty
successful model. Comment positions exclude hashes inside strings. Local let bindings,
foreach loops, pipelines, and nested calls expose `Parent`/`Children` relationships and
function ownership. Cop AST locations currently provide line numbers only; unavailable
column/end positions are left unknown rather than approximated.

## Usage

```cop
import cop

# List all exported type definitions in .cop files
foreach cop.Types:isPublic => '{item.Name} ({item.File.Path})'

# Find predicates
foreach cop.Statements:declaration => '{item.MemberName}'
```
