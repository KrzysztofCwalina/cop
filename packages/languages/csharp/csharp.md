---
name: csharp
version: 1.0.0
title: C# Source Language Provider
description: C# source code parser and analyzer
authors: cop-team
tags: csharp, dotnet
provider: clr
providerEntry: Cop.Providers.CSharpProvider
providerAssembly: csharp-provider.dll
---
# C# Source Language Provider
Parses C# source code files and provides Types, Statements, Calls, and Lines collections.

Calls include invocations nested in arguments, conditions, and initializers. `CallTarget`
and `ArgumentExpressions` expose parsed `CodeExpression` nodes with `Kind`, `Name`, `Target`,
and decoded literal `StringValue`, so checks never need to parse string-valued `Arguments`.
Invocation expressions expose their arguments through `Children`; unsupported forms have
`Kind == other`. `CallTarget` preserves the syntactic receiver even when semantic analysis
resolves `TypeName` to its declaring type.

Active `#nullable` directives appear in `Statements` with `Kind == directive` and parsed
`Keywords`: `nullable`, `enable`/`disable`/`restore`, and optionally `warnings`/`annotations`.
Comments, string literals, and inactive preprocessor branches do not produce directives.

---
name: csharp-lister
version: 1.0.0
title: C# API Surface Analysis
description: API surface tracking, export, and diff for .NET libraries
authors: cop-team
tags: csharp, api, surface, diff, breaking-changes
language: C#
dependencies:
  - github.com/cop/cop/csharp-library-checks
---

# C# API Surface Analysis

Predicates and checks for tracking public API surface, detecting breaking changes,
and validating API listings against baseline files.

## Checks

- `api-removed` — Detect API entries in baseline file that no longer exist (breaking change)
- `api-added` — Detect new API entries not in baseline (informational)

## Usage

Place an `api-baseline.txt` file in your project root with one API signature per line.
Run cop with the `csharp-lister` package to compare current code against the baseline.
