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

Demanding `ReceiverType`, `ValueType`, or `ReferencedCallable` binds the demanded source
file with Roslyn. Factory-inferred receivers, selected overloads, parameter/result types,
and source declaration identity are available through the common model. Static type
qualifications are not instance receivers; a derived receiver and a base declaration remain
distinct. `ReferencedCallable` describes the compiler-selected declaration, not runtime
virtual dispatch. Missing semantic context is reported on demand and unresolved facts stay
absent. Syntax-only queries do not require a complete compilation. Only syntax is cached;
semantic facts are rebound against current dependencies on each execution.

Conditional access (`client?.Ping()`), direct generic calls, and nested object creation
have independent call facts. Methods and nested types are matched by syntax position,
not line number. Accessor/operator/local-function bodies, finally blocks, do loops,
deconstruction foreach, labels, break, and continue are represented. `HasRethrow`
means a bare `throw`, not throwing a newly constructed exception.

Types expose their own `Namespace` and generic parameters; methods expose generic
parameters, and parameters expose optionality. `File.Imports` preserves using aliases
and static imports, including namespace-scoped directives; `Usings` remains compatible.
Source spans use 1-based positions with an exclusive end.

Semantic compilations are separated by discovered projects and connected through
`ProjectReference`; unrelated projects cannot contribute symbols to one another.
Restored package references and project implicit/explicit usings are used without
installing any tools. This is not a full MSBuild evaluation: directory membership and
the first restored target are used. Conditional/imported/custom Compile or multi-target
settings produce a capability warning; unsupported settings can leave symbol resolution
incomplete. Source syntax facts remain available. Assembly API inspection includes public
and protected members and operators, but excludes private implementation details.

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
