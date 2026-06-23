# Cop Language Instructions

This project uses **Cop** — a data processing language for writing static analysis rules, code checks, and report generation. Cop files use the `.cop` extension.

## Quick Reference

### Running Cop

```bash
cop <file.cop>           # Run a .cop file
cop run <package-name>   # Run a package by name (auto-restores from a feed)
cop                      # Run all .cop files in current directory
cop verify               # Verify program correctness (no execution)
cop test                 # Run tests
cop repl                 # Interactive REPL
```

### Getting Detailed Help

```bash
cop help language        # Full language reference (syntax, types, operators)
cop help <package>       # Package documentation (types, functions, examples)
cop package list         # List all available packages
```

**Always run `cop help language` before writing cop code** to get the full syntax reference.
When using a package, run `cop help <package-name>` to see its types and API.

## Language Overview

Cop is a declarative data processing language. The core pattern for writing rules is:

```
import → define predicates → filter collections → produce output
```

### Key Syntax Rules

- Strings use **single quotes**: `'hello'`
- String interpolation: `'{item.Name} has {item.Count} methods'`
- Styled output: `'{text@dim}'`, `'{text@red}'`
- Comments: `#` for line comments, `##` for doc comments
- No semicolons, no braces for blocks (except object literals)

### Core Pattern: Writing a Check

```cop
import code
import code-analysis

# 1. Define a predicate (boolean filter)
predicate isTooLong(Method) => Method.Statements.count() > 50

# 2. Filter a collection
let longMethods = Code.Methods:isTooLong

# 3. Produce violations
let violations = longMethods:toWarning('Method {item.Name} has too many statements')

# 4. Output them
CHECK violations
```

### Declarations

| Keyword | Purpose | Example |
|---------|---------|---------|
| `import` | Import a package | `import code` |
| `feed` | Declare package source | `feed 'github.com/owner/repo'` |
| `let` | Declare a named value/list | `let Clients = Types:isClient` |
| `predicate` | Boolean filter on items | `predicate isPublic(Type) => ...` |
| `function` | Transform or compute | `function name(T) => expr` |
| `type` | Object shape definition | `type Foo = { Name : string }` |
| `enum` | Extensible enum | `enum Severity = error \| warning` |
| `flags` | Bit flag constants | `flags Mod = Public \| Static` |
| `command` | Named runnable entry point | `command MAIN = CHECK(violations)` |
| `foreach` | Iterate and output | `foreach items => '{item.Name}'` |
| `test` | Test assertion | `test x = assert(expr)` |
| `export` | Make visible to importers | `export predicate ...` |

### Filtering with `:`

The colon operator filters collections or pipes values through functions:

```cop
Types:isClient                    # filter Types where isClient is true
Types:isClient:isPublic           # chained AND filters
Statements:Kind:equals('call')    # field predicate
someValue:myFunction              # pipe value through function
```

### Common String Predicates

```cop
Name:startsWith('Get')
Name:endsWith('Client')
Name:contains('Test')
Name:equals('Main')
Name:matches('.*Service$')        # regex
```

### Collection Operations

```cop
items.Count                       # number of items
items.Select(item.Name)           # project to list of names
items.Where(item.Age > 18)        # filter with expression
items.OrderBy(item.Name)          # sort
items:any(predicate)              # true if any match
items:all(predicate)              # true if all match
items:none(predicate)             # true if none match
items:count(predicate)            # count matching
```

### Producing Violations (with code-analysis package)

```cop
import code-analysis

# Convert filtered items to violations:
let v = filteredItems:toError('message with {item.Name}')
let w = filteredItems:toWarning('message')
let i = filteredItems:toInfo('message')

# Output violations:
CHECK v
```

## Common Packages

| Package | Provides | Key Collections |
|---------|----------|-----------------|
| `code` | Source code analysis | Types, Methods, Statements, Lines, Files |
| `code-analysis` | Violation type + CHECK | Violation, toError, toWarning, toInfo |
| `files` | Filesystem analysis | Folders, Files |
| `csharp` | C# language provider | csharp.types(), csharp.statements() |
| `python` | Python language provider | python.types(), python.statements() |
| `javascript` | JS/TS language provider | javascript.types(), javascript.statements() |

## Example: Complete Rule File

```cop
feed 'github.com/KrzysztofCwalina/cop'
import code
import code-analysis
import csharp

# Flag methods longer than 50 statements
predicate isTooLong(Method) => Method.Statements.count() > 50

# Flag types with no documentation
predicate isUndocumented(Type) => Type.Documented == false && Type:isPublic

let longMethods = Code.Methods:isTooLong
    :toWarning('Method {item.Name} exceeds 50 statements ({item.Statements.count()})')

let undocTypes = Code.Types:isUndocumented
    :toWarning('Public type {item.Name} is not documented')

command MAIN = CHECK(longMethods + undocTypes)
```

## Testing

```cop
import code

test has-types = assert(Code.Types.Count > 0)
test no-long-methods = assert(Code.Methods:isTooLong.Count == 0)
test has-public = assert(Code.Types:isPublic.Count > 0, 'Expected public types')
```

Run with: `cop test`

## Verifying Rules

After writing or modifying `.cop` files, always verify correctness:

```bash
cop verify                # Verify all .cop files in current directory
cop verify <file.cop>     # Verify a specific file
cop verify <directory>    # Verify all .cop files in a directory
```

`cop verify` performs full static analysis without executing:
- Syntax validation (parse errors with source context)
- Import resolution (are all packages available?)
- Name binding (are all identifiers defined?)
- Type checking (do referenced types and fields exist?)

If verification fails, fix the reported errors before running the program.

## Tips for Agents

1. **Always start with** `cop help language` to get the full syntax reference
2. **Check package APIs** with `cop help <package-name>` before using a package
3. **Use single quotes** for all strings (not double quotes)
4. **Use `{item.Prop}`** for string interpolation in templates
5. **Predicates are camelCase**, types are PascalCase, commands are UPPERCASE
6. **After writing rules, run `cop verify`** to check for errors before execution
7. **Test with** `cop test` after writing rules
8. **Validate syntax only** with `cop syntax <file.cop>` (lighter than verify)
9. **Never compare enum-typed fields to string literals** — use enum members (`Class`) or explicit cast (`TypeKind('value')`)

## Project Convention: `cop-checks/` Folder

All cop check files MUST live in a folder named exactly `cop-checks/` (not `checks/`) at the repository root. Follow this structure:

```
cop-checks/
  main.cop           # Composes all checks and runs them
  naming.cop         # One check per file — e.g., naming conventions
  layering.cop       # Dependency layering rules
  no-interfaces.cop  # "No new interfaces" check
  ...
```

### Rules for `cop-checks/`:

1. **One check per file** — each `.cop` file defines a single focused check
2. **Each file declares a `let` with its violations** — e.g., `let naming-violations = ...`
3. **`main.cop` composes all checks** — imports violation lists from each file and runs `CHECK(all)`
4. **Run all checks with** `cop cop-checks/main.cop -t .` from the repo root

### Example `main.cop`:

```cop
let all-violations =
    naming-violations +
    layering-violations +
    no-new-interfaces

command MAIN = CHECK(all-violations)
```

### Example single check file (`naming.cop`):

```cop
import csharp-checks
import code-analysis

predicate hasBadName(Type) => Type.Name:startsWith('_')

let naming-violations = csharp.Types:hasBadName
    :toError('{item.Name} must not start with underscore')
```

## Reporting Issues (IMPORTANT)

You MUST file a feedback issue whenever you encounter any of the following:
- cop commands fail with errors you cannot resolve
- Rules that should match produce no output (silent failures)
- `cop verify` passes but rules don't work at runtime
- Language features described in `cop help language` don't behave as documented
- You cannot figure out how to accomplish something after consulting help

Do NOT silently work around problems. File the issue FIRST, then work around it.
This feedback is critical for improving cop.

```bash
gh issue create --repo KrzysztofCwalina/cop --label agent-feedback \
  --title "Agent feedback: <brief description of the problem>" \
  --body "## What I tried\n<the cop command or .cop code>\n\n## What happened\n<full error or unexpected output>\n\n## What I expected\n<desired behavior>\n\n## Workaround\n<what I did instead, if anything>"
```

File one issue per distinct problem. Include the exact cop commands and .cop code
that triggered the issue. Do not bundle multiple unrelated problems into one issue.

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
- **Each check file declares a violation list** — `let my-violations = codebase.Types:isViolating :toError(...)`.
- Check files reference the shared `codebase` defined in `main.cop` — every file in `cop-checks/` loads together as one program.
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
