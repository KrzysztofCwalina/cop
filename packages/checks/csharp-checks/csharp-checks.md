---
name: csharp-checks
version: 1.0.2
title: C# Checks
description: C# correctness, StyleCop style, and Framework Design Guidelines checks for coding agents
authors: cop-team
tags: csharp, dotnet, coding-standards, stylecop, fdg, naming, design
language: C#
dependencies: ['github.com/KrzysztofCwalina/cop/code: 1.2.0', 'github.com/KrzysztofCwalina/cop/csharp: 2.0.0']
provider: clr
providerEntry: Cop.Providers.CSharpProvider
providerAssembly: csharp-provider.dll
---

# C# Checks

Checks covering correctness, StyleCop style, and .NET Framework Design Guidelines.

## Composing checks

Import `csharp-checks` and pass your parsed `Codebase` to `csharpChecks(cb)`. Combine
the returned violations with your own lists, then pass the combined list to `CHECK`.
This reuses your model rather than parsing the target a second time.

The category functions are `csharpCorrectnessChecks(cb)`, `csharpStyleChecks(cb)`,
and `fdgChecks(cb)`. Each rule also has a caller-codebase function, such as
`varDeclarations(cb)`, `requiredBraces(cb)`, and `uninstantiatedInternal(cb)`.
Use `cop help csharp-checks` for the complete API.

`boolParameterNaming(cb)` allows boolean parameters with affirmative or descriptive
names, such as `isEnabled`, `hasChanges`, `includeTests`, and `recursive`. The
conventional data name `value` is also allowed. Vague names such as `flag`, `b`, and
`arg` are reported; boolean inputs do not need to be replaced with option enums.
The older `avoidBoolParams(cb)` entry point now delegates to this naming check.
Boolean data properties may use `Value` or `BooleanValue`; they do not have to
pretend to be state predicates such as `IsEnabled`.

The existing exported lists, including `csharp-checks`, `csharp-style-checks`,
and `var-declarations`, remain available. These aliases use the package's own
`codebase(csharp.parse())` model, as does `cop run csharp-checks`.

## Choosing the scope

The package does not exclude tests, samples, examples, or repository-specific paths.
Pass a scoped `Codebase` to choose which files and declarations the checks inspect.
Keep its `Files`, `Types`, `Methods`, `Statements`, `Calls`, `Lines`, and `Regions`
consistent with that scope. Retain the relevant `Projects` for project-aware checks.

Scope the model **before calling the checks**. Filtering an already computed violation
list does not prevent excluded files from demanding compiler binding or diagnostics.
Tests that deliberately contain invalid C# should not enter the checked model.

## Analysis limits

Structural checks use parsed declarations, expression trees, and compiler-bound types.
Console output is allowed in files belonging to executable projects. Anonymous types,
including anonymous generic arguments, are exempt from the explicit-type rule because
C# cannot spell those types explicitly.

The unused-internal-class check compares constructor result types with declarations
by namespace, name, and generic arity. Reflection, external callers, and construction
in unparsed source are not visible; a finding is not proof that removal is safe.

Static-class suggestions account for inheritance and instance members in other
partial declarations, matched by namespace, name, and generic arity. Only the
declarations in the supplied model are visible.

The disposal check recognizes constructors directly initializing `using` and
`await using` resources. A surrounding using body does not dispose unrelated allocations.
Other disposal patterns and ownership transfers still require review; this check does
not prove that a reported object leaks.

Tab and trailing-whitespace checks intentionally inspect source text. Strict structural
verification requires explicit approval of `hasTab` and `hasTrailingWhitespace`.
Other checks use parsed facts, including decoded comment contents and literal values.
