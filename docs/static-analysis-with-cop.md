# Static Analysis with Agent Cop

Agent Cop ships with pre-built analysis packages that check your code for common issues — naming conventions, error handling, documentation gaps, and more. You can run them directly with `cop check` without writing any `.cop` files, or write custom checks using the Agent Cop DSL.

## Running Pre-Built Checks

The fastest way to analyze your code:

```bash
cop check csharp-checks                 # run C# checks (naming, formatting, docs, design)
cop check python-checks                 # run Python checks
cop check csharp-checks python-checks   # run multiple packages at once
cop check csharp-checks -t src/         # analyze a specific directory
```

### Available Check Packages

| Package | What it checks |
|---|---|
| `csharp-checks` | C# conventions: naming, formatting, documentation, error handling, design rules |
| `csharp-library-checks` | Library API design: sealed clients, method conventions, constructor patterns |
| `csharp-library-azure-checks` | Azure SDK conventions: client naming, options, service methods, LRO, paging |
| `csharp-snippets-checks` | Snippet region ↔ markdown fence consistency |
| `python-checks` | Python conventions: print, bare except, eval, naming, docstrings |
| `python-library-checks` | Python library patterns: naming, kwargs, LRO, paging |
| `python-library-azure-checks` | Azure SDK for Python: client naming, async patterns, credentials |
| `python-snippets-checks` | Snippet region ↔ markdown fence consistency |
| `javascript-checks` | JavaScript/TypeScript conventions: console, eval, var, debugger |
| `javascript-library-checks` | JS/TS library patterns: verbs, cancellation, pagination |
| `javascript-library-azure-checks` | Azure SDK for JS: client naming, options, pipeline policies |
| `javascript-snippets-checks` | Snippet region ↔ markdown fence consistency |

For a complete list of every individual check, see the [All Checks Catalog](checks.md).

### Exit Codes

| Code | Meaning |
|---|---|
| 0 | All checks pass — no violations found |
| 1 | One or more violations found |
| 2 | Parse error, missing package, or configuration error |

This makes cop easy to integrate into CI pipelines:

```yaml
- name: Run cop checks
  run: cop check csharp-checks csharp-library-checks
  # Fails the build if any checks produce violations (exit code 1)
```

### Selecting Specific Rules

Each check package exports named rule sets. Use `-c` to run specific ones:

```bash
cop check csharp-checks -c interface-prefix,type-name-casing
```

## Writing Custom Checks

For project-specific rules, create `.cop` files and run them with `cop run`.

### Prerequisites

Install cop (see [README](../README.md) for details) and create a project with source files to analyze.

## The Data Model

When cop runs, it parses every source file in your project and populates four lists:

| List | Item Type | What it contains |
|---|---|---|
| `Code.Types` | `Type` | Classes, structs, interfaces, enums, records |
| `Code.Statements` | `Statement` | Individual statements (calls, declarations, error handlers, etc.) |
| `Code.Lines` | `Line` | Raw text lines |
| `Code.Files` | `File` | Source files |

Every item in these lists has properties you can filter on. A `Type` has `Name`, `Kind`, `Modifiers`, `Methods`, `Constructors`, `BaseTypes` (use `isPublic`, `isSealed` predicates from the code package for modifier checks). A `Statement` has `Kind`, `TypeName`, `MemberName`, `Arguments`, `Line`. See the [Code Package Reference](packages/code.md) for the full property catalog.

The key insight: **the same data model works across all languages**. A C# class and a Python class both produce `Type` items with the same properties. A `console.log()` call in JavaScript and a `Console.WriteLine()` call in C# both produce `Statement` items with `Kind == 'call'`.

## Writing Predicates

A **predicate** is a named boolean test on a single item. Predicates are the building blocks of all checks:

```ruby
import code

# Matches any call to Thread.Sleep (C#) or time.sleep (Python)
predicate sleepCall(Statement) => Statement.Kind == 'call'
    && Statement.MemberName:sameAs('sleep')

# Matches types whose name ends with "Client"
predicate client(Type) => Type.Name:endsWith('Client')

# Matches error handlers that catch a broad exception without rethrowing
predicate swallowsException(Statement) => Statement.ErrorHandler == true
    && Statement.Generic == true && !Statement.Rethrows
```

Predicates don't produce output — they're filters. You apply them with the subset operator (`:`):

```ruby
let SleepCalls = Code.Statements:sleepCall          # all sleep calls across all languages
let Clients = Code.Types:client                      # all client types
let SwallowedExceptions = Code.Statements:swallowsException
```

### Language Scoping

Use a language keyword to restrict a check to specific file types:

```ruby
let CSharpSleepCalls = Code.Statements:csharp:sleepCall     # only .cs files
let PythonSleepCalls = Code.Statements:python:sleepCall     # only .py files
let JsSleepCalls = Code.Statements:javascript:sleepCall     # only .ts/.js files
```

Available language keywords: `csharp` (`.cs`), `python` (`.py`), `javascript` (`.ts`), `java` (`.java`), `go` (`.go`).

### Negation

Prefix a predicate with `!` to negate it:

```ruby
predicate sealed(Type) => Type:isSealed
let UnsealedClients = Code.Types:client:!sealed     # clients that are NOT sealed
```

### Chaining

Predicates chain left to right. Each `:predicate` filters the list further:

```ruby
# Start with all types → keep only C# → keep only clients → remove sealed ones
let UnsealedCSharpClients = Code.Types:csharp:client:!sealed
```

## Producing Output

### Implicit Output

Output is implicit in cop — the evaluated result of a `foreach` expression IS the output. The template string uses `{Property}` for interpolation:

```ruby
foreach Code.Types:csharp:client:!sealed
    => '{item.File.Path}:{item.Line} {item.Name} should be sealed'
```

Output:

```
src/Azure/BlobClient.cs:15 BlobClient should be sealed
src/Azure/QueueClient.cs:8 QueueClient should be sealed
```

### Styled Output

Use `{text@style}` for colored/styled output:

```ruby
foreach Code.Statements:javascript:evalCall
    => '{error@red} {item.File.Path}:{item.Line} Do not use eval()'
```

Available styles: `@red`, `@yellow`, `@green`, `@cyan`, `@dim`, `@bold`, `@auto` (auto-colors by severity keyword).

### Structured Violations with code-analysis

For production checks, use the `code-analysis` package which provides typed `Violation` objects and a `CHECK` command:

```ruby
import code-analysis

let sleepErrors = Code.Statements:csharp:sleepCall
    :toError('Use Task.Delay instead of Thread.Sleep')

let evalErrors = Code.Statements:javascript:evalCall
    :toError('Do not use eval() — it is a security risk')

let printWarnings = Code.Statements:python:printCall
    :toWarning('Avoid print() — use logging instead')

CHECK(sleepErrors + evalErrors + printWarnings)
```

The `CHECK` command formats output as `file(line): severity: message` — the standard format understood by IDEs and CI systems.

The `toError`, `toWarning`, and `toInfo` functions work on `Statement`, `Type`, `Line`, and `Folder` items. They produce `Violation` objects with `Severity`, `Message`, `File`, `Line`, and `Source` properties.

## Cross-Language Checks

Many coding rules apply across all languages. Agent Cop's data model and comparison operators make it straightforward to write checks once:

### Case-Insensitive Comparisons (Default)

All string comparisons in cop are **case-insensitive by default**. This means `Thread` matches `thread`, and `Sleep` matches `sleep`:

```ruby
# This catches Thread.Sleep (C#), time.sleep (Python), and any casing variant
predicate sleepCall(Statement) => Statement.Kind == 'call'
    && Statement.MemberName == 'sleep'
```

### Convention-Insensitive Comparisons with `:sameAs`

Different languages use different naming conventions. Use `:sameAs()` to compare identifiers regardless of convention (PascalCase, camelCase, snake_case, UPPER_SNAKE):

```ruby
# All of these match: ConfigureAwait, configure_await, configureAwait
Type.Name:sameAs('ConfigureAwait')
```

### Word-Level Analysis with `.Words`

The `.Words` transform splits identifiers into lowercase words, normalizing across all conventions:

```ruby
# "TaskCompletionSource", "task_completion_source", "taskCompletionSource" all produce:
# ['task' 'completion' 'source']

# Check if any type name contains the word "client"
predicate client(Type) => Type.Name.Words:contains('client')
```

### The ErrorHandler Abstraction

Different languages handle errors differently: C# has `catch`, Python has `except`, JavaScript has `catch`, Go has `if err != nil`. The code model abstracts all of these as `ErrorHandler`:

```ruby
# Works across C#, Python, JavaScript — no need to check Statement.Kind
predicate swallowsException(Statement) => Statement.ErrorHandler == true
    && Statement.Generic == true && !Statement.Rethrows
```

| Property | Description |
|---|---|
| `ErrorHandler` | `true` if this statement is an error-handling construct |
| `Generic` | `true` if it catches a broad/base exception type |
| `Rethrows` | `true` if the handler rethrows the caught exception |

### Example: A Complete Cross-Language Ruleset

```ruby
import code-analysis

# ── Swallowed exceptions (all languages) ──
predicate swallowsException(Statement) => Statement.ErrorHandler == true
    && Statement.Generic == true && !Statement.Rethrows
let swallowed = Code.Statements:swallowsException
    :toWarning('Do not swallow exceptions — rethrow or catch a specific type')

# ── Console/debug output (per language, but same concept) ──
predicate consoleOutput(Statement) => Statement.Kind == 'call'
    && (Statement.TypeName == 'Console'
        || Statement.TypeName == 'console'
        || Statement.MemberName == 'print')
let consoleWarnings = Code.Statements:consoleOutput
    :toWarning('Avoid console/print output in production code')

# ── Type naming: clients should end with Client ──
predicate client(Type) => Type.Name:endsWith('Client')

CHECK(swallowed + consoleWarnings)
```

## Organizing Checks

### Single File

For small projects, a single `checks.cop` at the project root is all you need:

```ruby
import csharp-checks
import javascript-checks
import python-checks

CHECK(csharp-checks + javascript-checks + python-checks)
```

### Multiple Files

Agent Cop discovers and runs all `.cop` files in a directory. Split checks across files by concern — each file shares the same scope:

```
checks/
  definitions.cop       # shared predicates
  csharp-checks.cop     # C# specific checks
  js-checks.cop         # JavaScript checks
  project-rules.cop     # project-specific cross-language rules
```

The convention is to separate **predicates** (pure definitions, no output) from **checks** (produce violations). This keeps predicates reusable:

**`definitions.cop`** — predicates only:

```ruby
# Service layer conventions
predicate serviceClass(Type) => Type.Name:endsWith('Service')
predicate controllerClass(Type) => Type.Name:endsWith('Controller')
predicate repositoryClass(Type) => Type.Name:endsWith('Repository')

# Error handling
predicate emptyErrorHandler(Statement) => Statement.ErrorHandler == true && Statement.Empty
```

**`project-rules.cop`** — checks that use those predicates:

```ruby
import code-analysis

let empty-catch-blocks = Code.Statements:emptyErrorHandler
    :toWarning('Empty error handler — handle or rethrow the exception')

CHECK(empty-catch-blocks)
```

### Using Built-In Packages

Agent Cop ships with packages for common languages and frameworks. Import multiple to cover a polyglot codebase:

```ruby
import csharp-checks
import javascript-checks
import python-checks

CHECK(csharp-checks + javascript-checks + python-checks)
```

This runs all built-in checks for all three languages. Each package only matches files in its language — `csharp-checks` only touches `.cs` files, `javascript-checks` only `.ts`/`.js`, etc.

### Package Hierarchy

Built-in packages form a dependency chain. Importing a leaf package brings in everything above it:

```
code → code-analysis → csharp-checks → csharp-library-checks → csharp-library-azure-checks
                      → python-checks → python-library-checks → python-library-azure-checks
                      → javascript-checks → javascript-library-checks → javascript-library-azure-checks
```

For example, `import csharp-library-checks` gives you all of `csharp-library-checks`, `csharp-checks`, `code-analysis`, and `code`.

## Excluding Files

Use the `exclude` directive to skip paths from scanning:

```ruby
exclude '**/generated/**'
exclude '**/node_modules/**'
exclude '**/migrations/**'
```

Built-in excludes (`bin/`, `obj/`) are always applied automatically.

You can also filter paths inline with `:!Path()`:

```ruby
# Skip test files for this specific check
let prodSleepCalls = Code.Statements:sleepCall:!Path('**/test/**'):!Path('**/tests/**')
    :toError('Use Task.Delay instead of Thread.Sleep')
```

## Named Commands

Define named commands that can be run individually:

```ruby
import code

command list-types = foreach Code.Types => '{item.Name} ({item.Kind}) — {item.File.Path}'
command list-clients = foreach Code.Types:client => '{item.Name}'
command count-files = '{Code.Files.Count} source files'
```

```bash
cop run list-types          # run just that command
cop run list-clients
cop run                     # run all statements (but not named commands)
```

## Running Checks

### Using Pre-Built Packages

```bash
cop check csharp-checks                  # run checks from a package
cop check csharp-checks -t src/          # analyze a specific directory
cop check csharp-checks -c type-name-casing  # run specific rules only
```

### Using Custom .cop Files

```bash
cop run                      # discover and run all .cop files in the project
cop run checks.cop           # run a specific .cop file
cop run my-command           # run a named command
cop run -t src/              # run against a specific directory
cop test                     # run ASSERT commands and report results
```

### Exit Codes

| Code | Meaning |
|---|---|
| 0 | All commands produced zero output — all checks pass |
| 1 | One or more checks produced output — violations found |
| 2 | Parse error, missing package, or configuration error |

This makes cop easy to integrate into CI pipelines:

```yaml
- name: Run cop checks
  run: cop run
  # Fails the build if any checks produce output (exit code 1)
```

### Restoring Remote Packages

If your `.cop` file imports packages from a GitHub feed, restore them first:

```bash
cop package restore          # download remote packages
cop run                      # run checks
```

## Putting It All Together

Here's a complete project-level `checks.cop` for a polyglot repo with C#, JavaScript, and Python:

```ruby
# Project checks — runs across all source code
import csharp
import javascript
import python
import code-analysis

exclude '**/node_modules/**'
exclude '**/dist/**'
exclude '**/__pycache__/**'

# ── Use all built-in language checks ──
let all-language-checks = csharp-checks + javascript-checks + python-checks

# ── Custom cross-language rules ──
predicate todoComment(Line) => Line.Text:contains('TODO') || Line.Text:contains('HACK')
let todos = Code.Lines:todoComment
    :toInfo('{item.Text}')

# ── Custom project-specific rules ──
predicate testHelper(Type) => Type.Name:endsWith('TestHelper')
    && !Type:isPublic
let non-public-helpers = Code.Types:testHelper
    :toWarning('{item.Name} should be public so tests in other projects can use it')

# ── Run everything ──
CHECK(all-language-checks + todos + non-public-helpers)
```

```bash
cop run
```

Output:

```
src/Services/BlobService.cs(42): error: Use Task.Delay instead of Thread.Sleep
src/Services/BlobService.cs(88): warning: Do not swallow Exception — rethrow or catch a specific type
client/src/api.ts(15): warning: Avoid console.log in production code
client/src/utils.ts(23): warning: Use const or let instead of var
scripts/deploy.py(7): warning: Avoid print() — use logging instead
tests/helpers.py(3): info: # TODO: refactor this after migration
src/Helpers/QueryTestHelper.cs(1): warning: QueryTestHelper should be public so tests in other projects can use it
```

## Exporting to CodeQL

If your organization uses GitHub CodeQL for code scanning, you can transpile cop checks into standalone `.ql` query files:

```bash
cop run checks.cop -cql
```

This generates a `codeql/` directory next to your `.cop` file containing one `.ql` file per exported check. The generated queries can be added to a CodeQL query suite and run with `codeql analyze` in CI.

### What transpiles

Checks that operate on Code provider collections (`Code.Types`, `Code.Statements`, `Code.Calls`) with supported predicates:

```ruby
import code

predicate isGodClass(Type) => Type.Name:endsWith('Manager') && Modifiers:isSet(Public)

export let god-classes = Code.Types:csharp:isGodClass:toWarning('Avoid God classes')
```

Generates `codeql/god_classes.ql`:

```ql
/**
 * @name god-classes
 * @kind problem
 * @problem.severity warning
 * @id cop/god_classes
 */

import csharp

from RefType t
where t.getName().toLowerCase().matches("%manager")
  and t.isPublic()
select t, "Avoid God classes"
```

### What doesn't transpile

CodeQL operates solely on source code graphs. Cop checks that use cross-provider features have no CodeQL equivalent:

- **Multi-provider checks** — combining `Code.Types` with `markdown.Sections` or `Filesystem.Folders`
- **Raw line analysis** — `Code.Lines` (regex on source text)
- **Complex predicate bodies** — predicates referencing other collections or using nested queries
- **Runtime/streaming providers** — HTTP endpoints, live services

When a check cannot be fully expressed in CodeQL, the transpiler reports an error and skips that check (strict mode — no partial output).

### Workflow

A typical workflow uses cop as the authoring experience and exports to CodeQL for CI enforcement:

```bash
# Develop and test checks locally with cop (fast iteration)
cop run checks.cop -t src/

# Export the code-only subset to CodeQL for GitHub code scanning
cop run checks.cop -cql

# Commit generated .ql files alongside your cop sources
git add codeql/
```

## Further Reading

- [CLI Reference](cli-reference.md) — all commands and options for `cop.exe`
- [Language Reference](language-reference.md) — full DSL syntax and semantics
- [Testing with Agent Cop](testing-with-cop.md) — writing and running tests with ASSERT
- [Code Package Reference](packages/code.md) — complete type catalog (Type, Statement, File, etc.)
- [Files Package Reference](packages/files.md) — folder and file analysis types
- [Package Documentation](packages/) — reference docs for all built-in packages
