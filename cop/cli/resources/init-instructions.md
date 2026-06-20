# Cop — Writing and Running Checks

This project uses **Cop** for static analysis checks. All checks live in `cop-checks/` at the repo root.

## How to Run Checks

```bash
cop cop-checks/main.cop -t .          # Run all checks against the repo root
cop cop-checks/main.cop -t . -c ai    # Run a specific named command (e.g. an AI command)
cop verify cop-checks/                # Verify check files are correct (no execution)
cop test tests/                       # Run `test` assertions
```

**There is NO `-p` flag in this model.** `main.cop` builds the codebase itself by calling
each language's `parse()` (see below), so checks run with just `-t <target>`.

**Always run `cop verify` after writing or editing .cop files** to catch syntax/type errors before execution.

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
`unsafe`/traits, Java `record`/`enum`, Python `@dataclass`). **Only then**, narrow a `Type`
to a language-specific subtype with `:as<Language>` and read its extra fields:

```cop
import csharp

# `record`-ness has no place in the common model, so narrow to CSharpType:
predicate isDto(Type) => Type.Name:endsWith('Dto')
let mutable-dtos = codebase.Types:isDto:asCSharp:!isRecord
    :toError('{item.Name} should be a record')
```

Available narrowings: `:asCSharp` → `CSharpType`, `:asRust` → `RustType`, `:asJava` →
`JavaType`, `:asPython` → `PythonType`, `:asGo` → `GoType`, `:asJavaScript` →
`JavaScriptType`. Run `cop help <language>` to see each one's extra fields. If the
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
# Run all checks: cop cop-checks/main.cop -t .

import code
import code-analysis
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

import code-layering

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
- **DO NOT use text matching on Lines** when semantic Codebase elements exist. Use `codebase.Types`, `codebase.Statements`, `Type.Name`, `Statement.TypeName`, `Statement.MemberName`, `File.Usings` etc. instead of `Line.Text:contains(...)`. Line-level text matching is a last resort for patterns that have no semantic representation.
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
cop help language           # Full language reference
cop help <package-name>     # Package API docs (types, fields, functions)
cop package list            # List available packages
```

**Run `cop help language` before writing cop code** for the full syntax reference.
When using a package, run `cop help <package-name>` to see its types and API.

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
- `cop verify` passes but runtime doesn't work as expected
- You cannot figure out how to accomplish something after consulting help

Do NOT silently work around problems. File the issue FIRST, then work around it.

```bash
gh issue create --repo KrzysztofCwalina/cop --label agent-feedback \
  --title "Agent feedback: <brief description>" \
  --body "## What I tried\n<cop code or command>\n\n## What happened\n<error or unexpected output>\n\n## What I expected\n<desired behavior>"
```
