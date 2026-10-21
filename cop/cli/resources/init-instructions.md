# Cop — Writing and Running Checks

This project uses **Cop** for static analysis checks. All checks live in `cop-checks/` at the repo root.

## How to Run Checks

```bash
{{COP}} cop-checks/main.cop -t .          # Run all checks against the repo root
{{COP}} cop-checks/main.cop -t . -c ai    # Run a specific named command (e.g. an AI command)
{{COP}} verify cop-checks/ --strict-structure # Verify syntax/types and enforce structural checks
{{COP}} test tests/                       # Run `test` assertions
```

**There is NO `-p` flag in this model.** `main.cop` builds the codebase itself by calling
each language's `parse()` (see below), so checks run with just `-t <target>`.

**Always run `{{COP}} verify cop-checks/ --strict-structure` after writing or editing checks** before execution.

## Structural Rules Must Use Parsed Facts

**MUST use the parsed code model (AST-derived facts and relationships) for code structure.
NEVER reconstruct syntax from source text.** This prohibition covers ALL text operations:
regex, `contains`, equality, prefix/suffix checks, splitting, and helpers that hide text reads.
Getting text from an AST node does NOT make a text-based check structural.

Use this discovery order: `{{COP}} help code` for the common model, then
`{{COP}} help <language>` for facts exposed by a language-specific narrowing.

| Requirement | Query parsed facts |
|---|---|
| Naming convention | `Type.Name`, `Method.Name`, `Statement.MemberName` |
| Visibility, async, inheritance | `Modifiers`, `isPublic`, `isAsync`, `BaseTypes`, `Interfaces` |
| Invocations | `codebase.Calls`, `Statement.Kind`, `TypeName`, `MemberName` |
| Nesting or enclosing constructs | `Statement.Parent`, `Children`, `Ancestors`, `Method` |
| Parameters and return types | `Method.Parameters`, `Method.ReturnType`, `TypeReference` |
| Imports and project dependencies | `File.Usings`, `Project.References`, `Project.Packages` |
| Language-only syntax | `:asCSharp`, `:asPython`, etc., then the documented extra fields |

String comparisons on **parsed facts** are fine: `Type.Name:endsWith('Service')` is a naming
rule. `Type.Source:contains('class')` is NOT a structural rule. Neither are comparisons
against `Line.Text`, `Statement.Condition`, `Expression`, string-valued `Arguments`,
`Api.Signature`, or `Region.Content`.

**If a required fact is not exposed, explain the capability gap. If you still want to write
a text-based check, ASK THE USER whether it is okay BEFORE writing it.** Describe the
proposed text check and its limitations. No approval means do not implement that check;
report it as unsupported. This also applies to intentionally textual requirements.
Never silently fall back to text, weaken the requirement, or remove strict verification.

After the user explicitly approves a named text check, verify with the narrow exception:
`{{COP}} verify cop-checks/ --strict-structure --allow-text-check approved-check-name`.
Only use names the user approved; never approve your own exception. Exceptions cover a
check's helpers, not other checks that also call those helpers.

Strict mode is conservative: raw-text reads outside diagnostic messages/source excerpts
are rejected, including intermediate aliases and reachable imported helpers. Put display-only
excerpts directly in the message passed to `toError`/`toWarning`/`toInfo`, not in shared
selection bindings. Strict verification is an authoring gate, not execution; run the
verified program afterward and confirm exact expected violations and nonviolations.

## The Codebase Model

Source is obtained by calling a language package's `parse()` function, which returns a
`Codebase`. Combine one or more with the `codebase(...)` function into a single unified
`Codebase`, then query its collections:

```cop
import code
import csharp
import cop

let codebase = codebase(csharp.parse(), cop.parse())
```

A `Codebase` exposes these collections:
- `codebase.Types` — all types
- `codebase.Statements` — all statements
- `codebase.Calls` — all call statements
- `codebase.Lines` — all source lines
- `codebase.Files` — all source files
- `codebase.Regions` — all regions
- `codebase.Projects` — all projects

Language parsers: `csharp.parse()`, `python.parse()`, `javascript.parse()`, `cop.parse()`.
Each also accepts a path, e.g. `csharp.parse('src/')`. For a multi-language repo, pass
several to `codebase(...)`:

```cop
let codebase = codebase(csharp.parse(), python.parse(), javascript.parse())
```

Narrow a collection to one language with `isCSharp` / `isPython` / `isJavaScript`
(e.g. `codebase.Types:isCSharp`).

## Language-Specific Checks (use sparingly)

The `Codebase` model above is **language-agnostic** — `codebase.Types`, `Type.Name`,
`Type.Kind`, `Type.Modifiers`, `Type.BaseTypes`, `Type.Decorators`, etc. work for every
language. **Always prefer the language-agnostic model**: those checks are simpler and run
across languages.

Some facts have **no** language-agnostic representation (e.g. C# `record`/`partial`, Rust
`unsafe`/traits, Java `record`/`enum`, Python `@dataclass`). **Only then**, narrow with
`:as<Language>` and read the extra fields. The same `:as<Language>` narrows **Types,
Methods, AND Statements** (control blocks, error handling, ...), via `codebase.Types`,
`codebase.Methods`, and `codebase.Statements`:

```cop
import csharp

# `record`-ness has no place in the common model, so narrow to CSharpType:
predicate isDto(Type) => Type.Name:endsWith('Dto')
let mutable-dtos = codebase.Types:isDto:asCSharp:!isRecord
    :toError('{item.Name} should be a record')

# Method/statement facts narrow the same way:
let ext-methods = codebase.Methods:asCSharp:isExtensionMethod
    :toWarning('{item.Name} is an extension method')
let locks = codebase.Statements:asCSharp:isLock
    :toInfo('lock statement at line {item.Line}')
```

Available narrowings: `:asCSharp` → `CSharpType`/`CSharpMethod`/`CSharpStatement`, `:asRust` →
`RustType`/`RustMethod`/`RustStatement`, and likewise `:asJava`, `:asPython`, `:asGo`,
`:asJavaScript`. Run `{{COP}} help <language>` to see each one's extra fields. If the
language-agnostic model already expresses your rule, **do not** use a narrowing.

## How Checks Are Organized

```
cop-checks/
  main.cop              # Builds the codebase, composes all checks → CHECK(all-violations)
  namespaces.cop        # One focused check per file
  layering.cop          # Another check
  ...
```

Rules:
- **`main.cop` builds the codebase** with `let codebase = codebase(...)` and is the ONLY file with a `command`.
- **One check per file** — each file defines a single focused rule.
- **Each check file declares a violation list** — `let my-violations = codebase.Types:isViolating :toError(...)`.
- Check files reference the shared `codebase` defined in `main.cop` — every file in `cop-checks/` loads together as one program.
- **No `export` needed.** Files in `cop-checks/` load as one program, so a `let` in one file is visible to the others directly. `export` is only for publishing a reusable package that other repos consume via `import`.
- **Never put a `command` in an individual check file.**

## Canonical Check File Template

```cop
# <Brief description of what this check enforces>

predicate isViolating(Type) => <condition>

let my-violations = codebase.Types:isViolating
    :toError('<message about {item.Name}>')
```

## Canonical `main.cop` Template

```cop
# Run all checks: {{COP}} cop-checks/main.cop -t .

import code
import code
import csharp
import cop

let codebase = codebase(csharp.parse(), cop.parse())

let all-violations =
    check-a-violations +
    check-b-violations +
    check-c-violations

command MAIN = CHECK(all-violations)
```

## Complete Real-World Example

**`cop-checks/namespaces.cop`** — ensures all types are in namespaces:

```cop
# All C# types must be in namespaces

predicate isInTestProject(Type) => Type.File.Path:startsWith('tests/') || Type.File.Path:startsWith('samples/')
predicate hasNamespace(Type) => Type.File.Namespace.Length:greaterThan(0)
predicate isMissingNamespace(Type:isCSharp) => !hasNamespace && !isInTestProject

let types-without-namespace = codebase.Types:isMissingNamespace
    :toError('{item.Name} in {item.File.Path} must be in a namespace')
```

**`cop-checks/layering.cop`** — enforces dependency rules:

```cop
# Runtime must not reference providers

import code

let runtime-projects = ['runtime']
let provider-projects = ['code', 'csharp-provider', 'python-provider']

predicate isRuntimeReferencingProvider(Project) =>
    Project.Name:in(runtime-projects)
    && Project.References:containsAny(provider-projects)

let layering-violations = codebase.Projects:isRuntimeReferencingProvider
    :toError('{item.Name} must not reference providers')
```

## DO NOT — Critical Rules

- **DO NOT implement checks as AI / LLM-based checks** (e.g. `ai.judge`) **unless the human VERY EXPLICITLY asks for an AI check.** Default to static, deterministic checks built from the codebase model (`codebase.Types`, `codebase.Statements`, predicates, etc.). AI checks are non-deterministic, require network access and an API key, and cost money — they are an exception, never the default. If a requirement *seems* to need an LLM, first try to express it as a static check; only reach for `ai.judge` when the human has explicitly requested it.
- **DO NOT pass `-p` flags.** `main.cop` builds the codebase via `parse()`; run with just `-t <target>`.
- **DO NOT infer code structure from ANY raw-text field or text-reading helper.** Use parsed facts and relationships. If a text-based check is still desired, explain why and **ASK THE USER for explicit approval before writing it**; there is no automatic "last resort" exception.
- **DO NOT test for a C# keyword via `Statement.TypeName`.** To detect `var` (or `dynamic`), use `Statement.Keywords:contains('var')` — NOT `Statement.TypeName == 'var'`. `var` is a keyword, not a type; `Statement.TypeName` holds the actual/inferred type (e.g. `var x = 5` has `TypeName` `int`), so `TypeName == 'var'` silently matches nothing.
- **DO NOT use `foreach` to print violations.** Never write `foreach violations => '{item.Message}'`. Always use `CHECK(violations)`.
- **DO NOT put a `command` in an individual check file.** Only `main.cop` has the command.
- **DO NOT manually iterate violations.** The pattern is always: `codebase.<Collection>:predicate` → `:toError()` → `CHECK()`.
- **DO NOT use a language-specific narrowing (`:asCSharp`, `:asRust`, `:asJava`, `:asPython`, `:asGo`, `:asJavaScript`) when the language-agnostic model already expresses the rule.** Prefer `codebase.Types`, `Type.Kind`, `Type.Modifiers`, `Type.BaseTypes`, etc. Reach for a narrowing ONLY for a fact the common model genuinely lacks (e.g. C# `record`, Rust `unsafe`).

## Key Syntax

- Strings use **single quotes**: `'hello'`
- Interpolation: `'{item.Name} has {item.Count} methods'`
- Styled interpolation: `'{item.File@dim}({item.Line@dim}): {item.Message}'`
- Filter with colon: `codebase.Types:isPublic`
- Chain filters: `codebase.Types:isPublic:hasNoTests`
- Member access binds to the filter result: `codebase.Types:isPublic.Count`
- Combine violations: `list-a + list-b`
- Violation levels: `:toError('...')`, `:toWarning('...')`, `:toInfo('...')`
- Comments: `#` (no multi-line comments)
- Predicates are camelCase, types are PascalCase, commands are UPPERCASE

## Getting More Help

```bash
{{COP}} help language           # Full language reference
{{COP}} help <package-name>     # Package API docs (types, fields, functions)
{{COP}} package list            # List available packages
```

**Run `{{COP}} help language` before writing cop code** for the full syntax reference.
When using a package, run `{{COP}} help <package-name>` to see its types and API.

## Common Packages

| Package | Provides | Usage |
|---------|----------|-------|
| `code` | Codebase model + `codebase()` | `codebase.Types`, `codebase.Statements`, ... |
| `code-analysis` | Violation type + CHECK | `toError`, `toWarning`, `toInfo` |
| `code-metrics` | Slop metrics (JSON) | `METRICS(violations, lines)` |
| `code-layering` | Dependency rules | `containsAny`, `in` |
| `csharp` | C# parser | `csharp.parse()` |
| `python` | Python parser | `python.parse()` |
| `javascript` | JS/TS parser | `javascript.parse()` |
| `cop` | Cop language parser | `cop.parse()` |
| `files` | Filesystem analysis | `files()`, `folders()` |

## Reporting Issues (IMPORTANT)

File a feedback issue whenever you encounter:
- cop commands fail with errors you cannot resolve
- Rules produce no output when they should (silent failures)
- `{{COP}} verify` passes but runtime doesn't work as expected
- You cannot figure out how to accomplish something after consulting help

Do NOT silently work around problems. File the issue FIRST, then work around it.

```bash
gh issue create --repo KrzysztofCwalina/cop --label agent-feedback \
  --title "Agent feedback: <brief description>" \
  --body "## What I tried\n<cop code or command>\n\n## What happened\n<error or unexpected output>\n\n## What I expected\n<desired behavior>"
```
