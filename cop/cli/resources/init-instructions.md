# Cop — Writing and Running Checks

This project uses **Cop** for static analysis checks. All checks live in `cop-checks/` at the repo root.

## How to Run Checks

```bash
cop cop-checks/main.cop -t .       # Run all checks against the repo
cop verify cop-checks/              # Verify check files are correct (no execution)
```

**Always run `cop verify` after writing or editing .cop files** to catch syntax/type errors before execution.

## How Checks Are Organized

```
cop-checks/
  main.cop              # Composes all checks → CHECK(all-violations)
  naming.cop            # One focused check per file
  layering.cop          # Another check
  ...
```

Rules:
- **One check per file** — each file defines a single focused rule
- **Each file exports a violation list** — `export let my-violations = ...`
- **Only `main.cop` has a command** — `command MAIN = CHECK(all-violations)`
- **Never put `command` in individual check files**

## Canonical Check File Template

```cop
# <Brief description of what this check enforces>

import csharp-checks    # or: import code
import code-analysis

predicate isViolating(Type) => <condition>

export let my-violations = csharp.Types:isViolating
    :toError('<message about {item.Name} in {item.File.Path}>')
```

## Canonical `main.cop` Template

```cop
# Run all checks: cop cop-checks/main.cop -t .

export let all-violations =
    check-a-violations +
    check-b-violations +
    check-c-violations

command MAIN = CHECK(all-violations)
```

## Complete Real-World Example

**`cop-checks/namespaces.cop`** — ensures all types are in namespaces:

```cop
# All C# types must be in namespaces

import csharp-checks
import code-analysis

predicate isInTestProject(Type) => Type.File.Path:startsWith('tests/')
predicate hasNamespace(Type) => Type.File.Namespace.Length:greaterThan(0)
predicate isMissingNamespace(Type) => !hasNamespace && !isInTestProject

export let types-without-namespace = csharp.Types:isMissingNamespace
    :toError('{item.Name} in {item.File.Path} must be in a namespace')
```

**`cop-checks/layering.cop`** — enforces dependency rules:

```cop
# Runtime must not reference providers

import csharp-checks
import code-analysis
import code-layering

let runtime-projects = ['runtime']
let provider-projects = ['code', 'csharp-provider', 'python-provider']

predicate isRuntimeReferencingProvider(Project) =>
    Project.Name:in(runtime-projects)
    && Project.References:containsAny(provider-projects)

export let layering-violations = csharp.Projects:isRuntimeReferencingProvider
    :toError('{item.Name} must not reference providers')
```

## DO NOT — Critical Rules

- **DO NOT use `foreach` to print violations.** Never write `foreach violations => '{item.Message}'`. Always use `CHECK(violations)`.
- **DO NOT put `command MAIN` in individual check files.** Only `main.cop` has the command.
- **DO NOT manually iterate violations.** The pattern is always: filter → `:toError()` → `CHECK()`.

## Key Syntax

- Strings use **single quotes**: `'hello'`
- Interpolation: `'{item.Name} has {item.Count} methods'`
- Filter with colon: `collection:predicate` (e.g., `Types:isPublic`)
- Chain filters: `Types:isPublic:hasNoTests`
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

| Package | Provides | Key Collections |
|---------|----------|-----------------|
| `code` | Source code analysis | Types, Methods, Statements, Lines, Files |
| `code-analysis` | Violation type + CHECK | toError, toWarning, toInfo |
| `code-layering` | Dependency rules | containsAny, in |
| `files` | Filesystem analysis | Folders, Files |
| `csharp-checks` | C# language provider | csharp.Types, csharp.Projects, csharp.Methods |

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
