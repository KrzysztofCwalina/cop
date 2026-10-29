---
name: code
version: 1.0.0
title: Code Analysis Types
description: Core type definitions for source code structural analysis
authors: cop-team
tags: types, code, analysis
---

# Code Analysis Types

Defines the type system for source code structural analysis. Import with `import code` in check files.

Provides types for: Type, Method, Constructor, Parameter, TypeReference, Statement, Argument, Line, File.
Provides collections for: Types, Statements, Lines, Files.

## Author structural rules from parsed facts

Start with `cop help code`, then `cop help <language>` for language-specific properties.
Use `Types` and `Methods` for declarations and modifiers, `Calls` for invocations,
`Statement.Parent`/`Children`/`Ancestors` for nesting, `Method.Parameters`/`ReturnType` for
signatures, and `File.Usings`/`Project.References` for dependencies.

Compare parsed names for naming rules. Do not infer code syntax from `Source`, `Line.Text`,
`Condition`, `Expression`, string-valued `Arguments`, or implicit object-to-text conversions.
This prohibition includes equality, substring checks, splitting, and helpers, not just regex.

If the model lacks a required fact, explain the gap and **ask the user before writing any
text-based check**. Without approval, report it as unsupported rather than approximating it.
Verify with `cop verify cop-checks/ --strict-structure`; use `--allow-text-check <name>`
only for a specific check the user has explicitly approved. Run the verified checks afterward.
See `samples/check-nested-throws.cop` for a parent/child query without source-text matching.

## Common source facts and graph identity

The common model is shared by every language provider. Providers populate facts they can
establish; an unavailable optional fact is `nic`, not an invented value.

- `Type.Kind = Alias` denotes a type alias, not an interface. `Type.AliasTarget` is the
  referenced type expression when available. `Type.Namespace` identifies the declaration's
  namespace or module, including files with multiple namespaces.
- `Type.TypeParameters` and `Method.TypeParameters` contain the declared generic parameter
  names. `Parameter.Optional` describes syntactic optionality independently of `Defaulted`.
- Types, methods, parameters, statements, and imports expose `Column`, `EndLine`, and
  `EndColumn`. Positions are one-based; the end is exclusive. Unknown positions are `nic`.
- `File.Imports` retains parsed `Import` facts: `Module`, optional imported `Name` and local
  `Alias`, `IsWildcard`, `IsStatic`, and `RelativeLevel` (zero for absolute imports). These
  are source facts, not a claim that the imported symbol has been resolved. `File.Usings`
  remains available for compatibility.
- `Statement.ValueExpression` describes a parsed return, await, initializer, or similar
  value when supplied by the parser. `CallTarget` and `ArgumentExpressions` describe calls.
  Literal expression kinds distinguish strings, numbers, booleans, null, arrays, and objects;
  unsupported forms remain `other`. Textual `Expression` is still not a substitute for these facts.
- `Project.SourceFiles` is optional exact source-file membership. `nic` means membership is
  unspecified and directory-based membership is used; an empty list means no source files.

`File.Statements`, `Method.Statements`, and `Statement.Children` refer to the same statement
objects. A method can expose a forest of root statements rather than every descendant:
follow `Children` or `Ancestors` to navigate nesting. Repeated calls on the same line remain
distinct nodes. Disk-cache round trips preserve these identities, ownership, expressions,
and source positions, including language-specific subtypes.

Languages that declare receiver methods separately can supply `Method.ReceiverType` and
opt into shared cross-file member linking. Accessing `Type.Methods` then resolves matching
receiver methods in the same language, directory, and namespace, only when the target type
is unambiguous. The method retains its declaring `File`; `File.Methods` remains intrinsic
to that file. Looking up the first type's `Name` does not demand other files. The flattened
`Methods` collection reports each intrinsic method once and does not include constructors.
Scoped receiver targets that cannot be matched exactly are not guessed.

Provider authors can override `ISourceParser.GetCacheIdentity(filePath)` to include the
selected backend and relevant configuration. It runs only when a file is demanded, before
cache lookup, so backend discovery can report a fallback warning even on cache hits.
Override `CanCache(filePath)` to return false when a semantic projection cannot safely
be persisted. Execution snapshots still memoize demanded files in either mode.

## Compiler-bound statement facts

`Statement.ReceiverType` is the compiler-inferred type of the actual receiver expression,
not the spelling of a call target. `ValueType` is the inferred expression/result type.
`ReferencedCallable` is available only for a compiler-selected declaration and describes its
`Name`, `Signature`, optional `DeclaringType` and `Namespace`, `ReturnType`, ordered parameter
types, and optional source path and declaration span. The receiver can be a derived type
while the selected declaration belongs to a base class. This is static binding, not a claim
that a virtual or interface call executes that declaration at runtime.

Unknown or unresolved facts remain `nic`. Providers can defer binding until one of these
three fields is accessed; structural names, expression trees, and first-type queries do not
demand binding. Missing compilation context must be reported on semantic demand, not replaced
with guessed receiver names or a fabricated selected callable.

The shared disk format is a **syntax cache**: it never invokes semantic getters and never
persists these three dependency-bound fields, even after they have been demanded. It retains
the original syntactic `TypeName`, not a later semantic enrichment. Providers must attach
fresh semantic callbacks after loading syntax or disable persistence with `CanCache = false`
until their semantic cache accounts for imported declarations and compilation context.


# code-analysis

Provides types and functions for producing structured code analysis results.

Defines the `Violation` type and `toError`/`toWarning`/`toInfo` functions that transform
source code items (Statements, Types, etc.) into typed violations with severity,
message, file path, and line number.

## Usage

```cop
import code

let VarErrors = Statements:csharp:isVarDeclaration:toError("Do not use var for {item.MemberName}")
foreach VarErrors => '{item.Severity}: {item.Message} ({item.File}:{item.Line})'
```


---
name: code-layering
version: 1.0.0
title: Architecture Layering Enforcement
description: Formal architecture layer definitions and dependency direction checks
authors: cop-team
tags: architecture, layering, dependencies, enforcement
dependencies:
  - code
  - code-analysis
---

# Architecture Layering Enforcement

Defines types and predicates for declaring architectural layers and enforcing dependency direction between projects.

This package is language-agnostic and works with project/dependency data from C#, JavaScript/TypeScript, Python, Java, Go, and Rust.

## Usage

```cop
import code

# Define layers as project name lists
let presentation-projects = ['MyApp.Web', 'MyApp.Api']
let business-projects     = ['MyApp.Services', 'MyApp.Domain']
let data-projects         = ['MyApp.Data', 'MyApp.EF']
let all-known-projects = presentation-projects + business-projects + data-projects

# Define disallowed references
predicate presentationReferencesData(Project) =>
    Project.Name:in(presentation-projects)
    && Project.References:containsAny(data-projects)

predicate dataReferencesPresentation(Project) =>
    Project.Name:in(data-projects)
    && Project.References:containsAny(presentation-projects)

# Produce violations
export let violations = Code.Projects:presentationReferencesData
    :toError('Presentation project {item.Name} must not reference Data layer directly')
    + Code.Projects:dataReferencesPresentation
    :toError('Data project {item.Name} must not reference Presentation layer')

# Detect uncategorized projects
export let uncategorized = Code.Projects:notInLayer
    :toWarning('Project {item.Name} is not assigned to any architectural layer')

CHECK code-layering => violations + uncategorized
```
