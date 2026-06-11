# Cop — Writing and Running Checks

This project uses **Cop** for static analysis checks. All checks live in `cop-checks/` at the repo root.

## How to Run Checks

```bash
cop cop-checks/main.cop -t . -p csharp       # Run checks on C# code
cop cop-checks/main.cop -t . -p python       # Run checks on Python code
cop cop-checks/main.cop -t . -p csharp -p python  # Multi-language repo
cop verify cop-checks/                        # Verify check files are correct (no execution)
```

**Always run `cop verify` after writing or editing .cop files** to catch syntax/type errors before execution.

### Running Packages Directly

```bash
cop csharp-checks -t .                  # Run the csharp-checks package
cop csharp-checks python-checks -t .    # Run multiple packages
cop code-metrics -t . -p csharp -p python  # Compute slop metrics across languages
```

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

import code
import code-analysis

predicate isViolating(Type) => <condition>

export let my-violations = Types:isViolating
    :toError('<message about {item.Name}>')
```

## Canonical `main.cop` Template

```cop
# Run all checks: cop cop-checks/main.cop -t . -p csharp

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

import code
import code-analysis

predicate isInTestProject(Type) => Type.File.Path:startsWith('tests/')
predicate hasNamespace(Type) => Type.File.Namespace.Length:greaterThan(0)
predicate isMissingNamespace(Type) => !hasNamespace && !isInTestProject

export let types-without-namespace = Types:isMissingNamespace
    :toError('{item.Name} in {item.File.Path} must be in a namespace')
```

**`cop-checks/layering.cop`** — enforces dependency rules:

```cop
# Runtime must not reference providers

import code
import code-analysis
import code-layering

let runtime-projects = ['runtime']
let provider-projects = ['code', 'csharp-provider', 'python-provider']

predicate isRuntimeReferencingProvider(Project) =>
    Project.Name:in(runtime-projects)
    && Project.References:containsAny(provider-projects)

export let layering-violations = Projects:isRuntimeReferencingProvider
    :toError('{item.Name} must not reference providers')
```

## DO NOT — Critical Rules

- **DO NOT use text matching on Lines** when semantic Codebase elements exist. Use `Types`, `Statements`, `Methods`, `File.Usings`, `Type.Name`, `Statement.TypeName`, `Statement.MemberName` etc. instead of `Line.Text:contains(...)`. Line-level text matching is a last resort for patterns that have no semantic representation.
- **DO NOT use `foreach` to print violations.** Never write `foreach violations => '{item.Message}'`. Always use `CHECK(violations)`.
- **DO NOT put `command MAIN` in individual check files.** Only `main.cop` has the command.
- **DO NOT manually iterate violations.** The pattern is always: filter → `:toError()` → `CHECK()`.

## Key Syntax

- Strings use **single quotes**: `'hello'`
- Interpolation: `'{item.Name} has {item.Count} methods'`
- Styled interpolation: `'{item.File@dim}({item.Line@dim}): {item.Message}'`
- Filter with colon: `collection:predicate` (e.g., `Types:isPublic`)
- Chain filters: `Types:isPublic:hasNoTests`
- Combine violations: `list-a + list-b`
- Violation levels: `:toError('...')`, `:toWarning('...')`, `:toInfo('...')`
- Comments: `#` (no multi-line comments)
- Predicates are camelCase, types are PascalCase, commands are UPPERCASE

## Providers and Ambient Collections

Providers supply data collections (Types, Statements, Lines, Files, etc.) from source code. Load them with `-p`:

```bash
cop my-checks.cop -t . -p csharp          # C# code analysis
cop my-checks.cop -t . -p python          # Python code analysis
cop my-checks.cop -t . -p javascript      # JavaScript/TypeScript analysis
cop my-checks.cop -t . -p csharp -p python -p javascript  # All languages
```

Once loaded, providers fill **ambient collections** you can use directly in .cop files:
- `Types` — all types from all loaded providers
- `Statements` — all statements
- `Lines` — all source lines
- `Files` — all source files
- `Projects` — all projects

For multi-language repos, use multiple `-p` flags — collections merge automatically.

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
| `code` | Source code types | Types, Statements, Lines, Files, Methods |
| `code-analysis` | Violation type + CHECK | toError, toWarning, toInfo |
| `code-metrics` | Slop metrics (JSON) | METRICS(violations, lines) |
| `code-layering` | Dependency rules | containsAny, in |
| `csharp-checks` | C# conventions | Run with: `cop csharp-checks -t .` |
| `python-checks` | Python conventions | Run with: `cop python-checks -t .` |
| `javascript-checks` | JS/TS conventions | Run with: `cop javascript-checks -t .` |
| `files` | Filesystem analysis | Folders, Files |

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
