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

## Parser and toolchain requirements

The Cop language parser is bundled with Cop. An installed SDK, external compiler, or
language server is not required, and no parser tools are downloaded or installed.
Structural analysis reads source without executing the analyzed Cop program; it is not
a replacement for `cop verify` or running that program's tests.

There is no optional external-parser fallback for Cop. Malformed input is reported as a
source-analysis error rather than silently returning an empty successful result. Unknown
source positions or unsupported facts remain unknown instead of being inferred from text.

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

## Deferred source semantics

`Statement.ValueType`, `ReceiverType`, and `ReferencedCallable` use the Cop compiler's
declaration-only static analysis. Direct source calls select unambiguous typed/arity
overloads, instantiate generic scalar or collection-element signatures from known argument
types, and infer simple expression-body results. An unknown required parameter type makes
the entire callable reference unknown without discarding an independently known result type.
Package-qualified calls have no value receiver; supported scalar colon applications expose
the subject separately from the result.

Binding begins only when a semantic field is read. Structural queries and `FIRST`/`TAKE`
do not load semantic dependencies. The context includes sibling `.cop` files in the same
directory and imported packages available in local source feeds or an ancestor `packages`
directory. Imports are parsed for exported declarations only: no target commands, package
initializers, providers, guards, or function bodies are executed. Missing or malformed source
dependencies are visible errors on semantic demand. Semantic results are execution-scoped
and never written into file-only syntax caches, so dependency-only edits are reanalyzed.

Runtime-dependent guards, ambiguous/dynamic overloads, partial/variadic application, and
collection-filter dispatch remain unknown with a capability warning. Unsupported expression
inference also remains unknown. Imported functions expose declared results; results requiring inference
inside a package's private scope remain unknown. No remote feed is downloaded during
analysis.

Import graphs are cycle-safe and bounded to 64 levels, 256 packages, 4096 source files, and
32 MiB of UTF-8 source text per context; exceeding a limit is an explicit analysis error.
Only directly imported packages expose exports to the analyzed program; private transitive
imports are checked without leaking their declarations into the caller's scope.
Local feed discovery skips directory reparse points and has a 64-level / 4096-directory
budget. Feed paths come from decoded parser metadata, never from matching target source text.

## Usage

```cop
import cop

# List all exported type definitions in .cop files
foreach cop.Types:isPublic => '{item.Name} ({item.File.Path})'

# Find predicates
foreach cop.Statements:declaration => '{item.MemberName}'
```
