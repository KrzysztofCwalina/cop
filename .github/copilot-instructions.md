# Copilot Instructions

## Build & Run

```bash
# Build the full solution
dotnet build cop.sln

# Run all tests
dotnet test tests/cop.tests/Cop.Tests.csproj
dotnet test tests/lang.tests/Lang.Tests.csproj

# Publish self-contained cop.exe
install/publish.ps1
```

**After any code change to cop/ projects**, rebuild:

```bash
install/publish.ps1                          # all platforms
install/publish.ps1 -Runtimes win-x64        # single platform
```

This publishes self-contained single-file EXEs into `install/<rid>/` subfolders. No .NET runtime needed on the target machine.

**After finishing a feature or bug fix**, always republish so the user's PATH-installed `cop.exe` is up to date:

```bash
install/publish.ps1 -Runtimes win-x64
```

**After modifying `install/publish.ps1`**, always verify Unix archives are correct:

```bash
install/verify-archives.ps1
```

This validates that Linux/macOS zip archives have the correct ZIP "version made by" host OS (Unix) and executable permission bits. Without these, `unzip`/`mise` extract the `cop` binary as non-executable (0644) and users get "Permission denied".

## Regenerating Docs

After any change to packages (new/renamed packages, updated doc comments, added/removed samples), regenerate the reference app:

```bash
dotnet run --project tools/copdocs -- packages --output docs/reference.html
```

This reads all packages under `packages/` and generates a single-file HTML reference app. The tool discovers packages by looking for directories containing `src/*.cop` files or a manifest `.md` file. It extracts:

- Types, predicates, functions, and checks from `.cop` source files (including `##` doc comments)
- Code samples from `samples/*.cop` files within each package

Always commit the regenerated `docs/reference.html` alongside the package changes.

## Architecture

- **cop/** — Main Cop project folder containing:
  - **cop/cli/** — `cop.exe` CLI (.NET 10 console app, System.CommandLine). Commands for package management.
  - **cop/language/** — Cop language (namespace `Cop.Lang`). Parser, interpreter, evaluator, type system. General-purpose language features only.
  - **cop/runtime/** — Runtime engine (namespace `Cop.Providers`). Engine orchestrator, source parsers (C#, Python, JavaScript), provider loading and registration.
  - **cop/shared/** — Core library (Cop.Core): package models, ObjectProvider base class, DataObject binary format, feed manager, dependency resolver, restore engine, checksum manager.
- **providers/** — Data providers (files, code, typespec, etc.). Each provider extends `ObjectProvider` and supplies typed collections to the language runtime. Built-in providers compile into `cop.exe`; external providers ship as separate DLLs.
- **packages/** — Seed packages organized by language (general at root, language-specific in dotnet/, js/, python/).
- **tests/cop.tests/** — NUnit 4.x tests for core library.
- **tests/lang.tests/** — NUnit 4.x tests for Cop language runtime.
- **install/** — Publish script, platform subfolders for self-contained binaries, VS Code extension.
- **docs/** — User-facing docs (getting started, language reference, package references).
  - **docs/internal/** — Internal architecture and design docs.

## Conventions

- Target framework: `net10.0`
- Nullable reference types: enabled
- Implicit usings: enabled
- Test framework: NUnit 4.x
- Keep code simple — prefer straightforward code over abstractions
- Packages are directories (not zips) under `packages/` in GitHub repos
- `.cop` files contain the Cop language and package declarations
- `.cop` files use single-quoted strings (not double) and `{Prop}` for template interpolation, `{text@style}` for styled output

## Safety Rules

- **NEVER discard, revert, or unstage changes you did not make.** If you see uncommitted or untracked files you don't recognize, ask the user before touching them — they may be the user's own work. Assume all changes in the working tree are intentional until told otherwise.

## Mandatory Functional Testing

**NEVER declare a feature done or publish a release without a successful end-to-end functional test.**

- Unit tests passing is NOT sufficient. You MUST run the actual `cop` command against real input and verify correct output.
- For new provider features (new properties, predicates): write a small `.cop` file that uses the feature, run it against a real target directory with known content, and verify the output matches expectations.
- For new language features: write a `.cop` snippet exercising the feature and confirm it runs without errors and produces expected results.
- If filesystem interference prevents testing (antivirus, file locks), find another target directory or wait — do NOT skip the test.
- If you cannot successfully run a functional test, **tell the user explicitly** that the feature is untested and should not be shipped.
- Passing `cop verify` only proves syntax/type correctness — it does NOT prove runtime behavior works.

## cop-checks/ Convention

All cop check files live in `cop-checks/` at the repository root. Structure:

- **One check per file** — each `.cop` file defines a single focused check
- **Each file exports a `let` with its violations** — e.g., `export let naming-violations = ...`
- **`main.cop` composes all checks** — concatenates violation lists from each file with `+` and runs `CHECK(all-violations)`
- **Run all checks with** `cop cop-checks/main.cop -t .` from the repo root

When adding a new self-check, create a new `.cop` file in `cop-checks/`, export a violation let, and add it to the `+` expression in `main.cop`.

## Language vs. Package Boundary

The C# language project (`cop/language/`) implements **only general-purpose language features**: keywords (`predicate`, `function`, `let`, `type`), the parser, evaluator, interpreter, and type system. **Domain-specific concepts** (e.g., Violation type, error/warning/info functions, severity levels, analysis rules) must **never** be added to C# code — they belong in `.cop` files in `packages/` packages. When adding a new capability, ask: "Is this a language feature or a domain concept?" Only language features go in C#.

Runtime data providers (`cop/runtime/` and `providers/`) supply collections to Cop packages via `runtime::` declarations (e.g., `runtime::Filesystem`, `runtime::Code`). All providers extend the `ObjectProvider` base class in `cop/shared/`. Built-in providers use a fast in-proc binary format (`DataObject[]` with a flat UTF-8 string heap) — no CLR string allocations per record. External providers can use JSON or the same binary format. Data providers are NOT domain-specific — they provide raw data that packages can query and analyze.

<!-- BEGIN COP INSTRUCTIONS -->
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
- **Each check file exports a violation list** — `export let my-violations = codebase.Types:isViolating :toError(...)`.
- Check files reference the shared `codebase` defined in `main.cop` — every file in `cop-checks/` loads together as one program.
- **Never put a `command` in an individual check file.**

## Canonical Check File Template

```cop
# <Brief description of what this check enforces>

predicate isViolating(Type) => <condition>

export let my-violations = codebase.Types:isViolating
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

predicate isInTestProject(Type) => Type.File.Path:startsWith('tests/') || Type.File.Path:startsWith('samples/')
predicate hasNamespace(Type) => Type.File.Namespace.Length:greaterThan(0)
predicate isMissingNamespace(Type:isCSharp) => !hasNamespace && !isInTestProject

export let types-without-namespace = codebase.Types:isMissingNamespace
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

export let layering-violations = codebase.Projects:isRuntimeReferencingProvider
    :toError('{item.Name} must not reference providers')
```

## DO NOT — Critical Rules

- **DO NOT implement checks as AI / LLM-based checks** (e.g. `ai.judge`) **unless the human VERY EXPLICITLY asks for an AI check.** Default to static, deterministic checks built from the codebase model (`codebase.Types`, `codebase.Statements`, predicates, etc.). AI checks are non-deterministic, require network access and an API key, and cost money — they are an exception, never the default. If a requirement *seems* to need an LLM, first try to express it as a static check; only reach for `ai.judge` when the human has explicitly requested it.
- **DO NOT pass `-p` flags.** `main.cop` builds the codebase via `parse()`; run with just `-t <target>`.
- **DO NOT use text matching on Lines** when semantic Codebase elements exist. Use `codebase.Types`, `codebase.Statements`, `Type.Name`, `Statement.TypeName`, `Statement.MemberName`, `File.Usings` etc. instead of `Line.Text:contains(...)`. Line-level text matching is a last resort for patterns that have no semantic representation.
- **DO NOT use `foreach` to print violations.** Never write `foreach violations => '{item.Message}'`. Always use `CHECK(violations)`.
- **DO NOT put a `command` in an individual check file.** Only `main.cop` has the command.
- **DO NOT manually iterate violations.** The pattern is always: `codebase.<Collection>:predicate` → `:toError()` → `CHECK()`.

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

<!-- END COP INSTRUCTIONS -->
