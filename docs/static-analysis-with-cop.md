# Static Analysis with Cop

This guide covers how to write, organize, and run static analysis checks with cop. It walks through real examples in C#, JavaScript, and Python — from simple single-file checks to multi-language packages.

## Prerequisites

Install cop (see [README](../README.md) for details) and create a project with source files to analyze. The examples in this guide assume a repo with C#, JavaScript, and Python code.

## Your First Check

Create a file called `checks.cop` in your project root:

```ruby
import code

foreach Code.Statements:call
    => PRINT('{item.File.Path}:{item.Line} {item.TypeName}.{item.MemberName}')
```

Run it:

```bash
cop run
```

This prints every function call in every source file cop can parse. Not very useful yet — but it shows the core pattern: **import data, filter it, produce output**.

## The Data Model

When cop runs, it parses every source file in your project and populates four lists:

| List | Item Type | What it contains |
|---|---|---|
| `Code.Types` | `Type` | Classes, structs, interfaces, enums, records |
| `Code.Statements` | `Statement` | Individual statements (calls, declarations, error handlers, etc.) |
| `Code.Lines` | `Line` | Raw text lines |
| `Code.Files` | `File` | Source files |

Every item in these lists has properties you can filter on. A `Type` has `Name`, `Public`, `Sealed`, `Methods`, `Constructors`, `BaseTypes`. A `Statement` has `Kind`, `TypeName`, `MemberName`, `Arguments`, `Line`. See the [Code Package Reference](packages/code-package-reference.md) for the full property catalog.

The key insight: **the same data model works across all languages**. A C# class and a Python class both produce `Type` items with the same properties. A `console.log()` call in JavaScript and a `Console.WriteLine()` call in C# both produce `Statement` items with `Kind == 'call'`.

## Writing Predicates

A **predicate** is a named boolean test on a single item. Predicates are the building blocks of all checks:

```ruby
import code

# Matches any call to Thread.Sleep (C#) or time.sleep (Python)
predicate sleepCall(Statement) => Statement.Kind == 'call'
    && Statement.MemberName:sm('sleep')

# Matches types whose name ends with "Client"
predicate client(Type) => Type.Name:ew('Client')

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
predicate sealed(Type) => Type.Sealed
let UnsealedClients = Code.Types:client:!sealed     # clients that are NOT sealed
```

### Chaining

Predicates chain left to right. Each `:predicate` filters the list further:

```ruby
# Start with all types → keep only C# → keep only clients → remove sealed ones
let UnsealedCSharpClients = Code.Types:csharp:client:!sealed
```

## Producing Output

### PRINT

`PRINT` writes one line per matching item. The template string uses `{Property}` for interpolation:

```ruby
foreach Code.Types:csharp:client:!sealed
    => PRINT('{item.File.Path}:{item.Line} {item.Name} should be sealed')
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
    => PRINT('{error@red} {item.File.Path}:{item.Line} Do not use eval()')
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

CHECK([sleepErrors, evalErrors, printWarnings])
```

The `CHECK` command formats output as `file(line): severity: message` — the standard format understood by IDEs and CI systems.

The `toError`, `toWarning`, and `toInfo` functions work on `Statement`, `Type`, `Line`, and `Folder` items. They produce `Violation` objects with `Severity`, `Message`, `File`, `Line`, and `Source` properties.

## Cross-Language Checks

Many coding rules apply across all languages. Cop's data model and comparison operators make it straightforward to write checks once:

### Case-Insensitive Comparisons (Default)

All string comparisons in cop are **case-insensitive by default**. This means `Thread` matches `thread`, and `Sleep` matches `sleep`:

```ruby
# This catches Thread.Sleep (C#), time.sleep (Python), and any casing variant
predicate sleepCall(Statement) => Statement.Kind == 'call'
    && Statement.MemberName == 'sleep'
```

### Convention-Insensitive Comparisons with `:sm`

Different languages use different naming conventions. Use `:sm()` to compare identifiers regardless of convention (PascalCase, camelCase, snake_case, UPPER_SNAKE):

```ruby
# All of these match: ConfigureAwait, configure_await, configureAwait
Type.Name:sm('ConfigureAwait')
```

### Word-Level Analysis with `.Words`

The `.Words` transform splits identifiers into lowercase words, normalizing across all conventions:

```ruby
# "TaskCompletionSource", "task_completion_source", "taskCompletionSource" all produce:
# ['task', 'completion', 'source']

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
predicate client(Type) => Type.Name:ew('Client')

CHECK([swallowed, consoleWarnings])
```

## Organizing Checks

### Single File

For small projects, a single `checks.cop` at the project root is all you need:

```ruby
import csharp
import javascript
import python

CHECK([csharp-checks, javascript-checks, python-checks])
```

### Multiple Files

Cop discovers and runs all `.cop` files in a directory. Split checks across files by concern — each file shares the same scope:

```
checks/
  definitions.cop     # shared predicates
  csharp-checks.cop   # C# specific checks
  js-checks.cop       # JavaScript checks
  project-rules.cop   # project-specific cross-language rules
```

The convention is to separate **predicates** (pure definitions, no output) from **checks** (produce violations). This keeps predicates reusable:

**`definitions.cop`** — predicates only:

```ruby
# Service layer conventions
predicate serviceClass(Type) => Type.Name:ew('Service')
predicate controllerClass(Type) => Type.Name:ew('Controller')
predicate repositoryClass(Type) => Type.Name:ew('Repository')

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

Cop ships with packages for common languages and frameworks. Import multiple to cover a polyglot codebase:

```ruby
import csharp
import javascript
import python

CHECK([csharp-checks, javascript-checks, python-checks])
```

This runs all built-in checks for all three languages. Each package only matches files in its language — `csharp-checks` only touches `.cs` files, `javascript-checks` only `.ts`/`.js`, etc.

### Package Hierarchy

Built-in packages form a dependency chain. Importing a leaf package brings in everything above it:

```
code → code-analysis → csharp → csharp-library → csharp-library-client → csharp-library-client-azure
                      → javascript
                      → python → python-library
```

For example, `import csharp-library-client` gives you all of `csharp-library`, `csharp`, `code-analysis`, and `code`.

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

command list-types = foreach Code.Types => PRINT('{item.Name} ({item.Kind}) — {item.File.Path}')
command list-clients = foreach Code.Types:client => PRINT('{item.Name}')
command count-files = PRINT('{Code.Files.Count} source files')
```

```bash
cop run list-types          # run just that command
cop run list-clients
cop run                     # run all statements (but not named commands)
```

## Running Checks

### Basic Usage

```bash
cop run                      # discover and run all .cop files in the project
cop run checks.cop           # run a specific file
cop run my-command           # run a named command
```

### Exit Codes

| Code | Meaning |
|---|---|
| 0 | All PRINT/CHECK commands produced zero output — all checks pass |
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
cop restore checks.cop       # download remote packages
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
let all-language-checks = [csharp-checks, javascript-checks, python-checks]

# ── Custom cross-language rules ──
predicate todoComment(Line) => Line.Text:ct('TODO') || Line.Text:ct('HACK')
let todos = Code.Lines:todoComment
    :toInfo('{item.Text}')

# ── Custom project-specific rules ──
predicate testHelper(Type) => Type.Name:ew('TestHelper')
    && !Type.Public
let non-public-helpers = Code.Types:testHelper
    :toWarning('{item.Name} should be public so tests in other projects can use it')

# ── Run everything ──
CHECK([all-language-checks, todos, non-public-helpers])
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

## Further Reading

- [Language Reference](language-reference.md) — full DSL syntax and semantics
- [Code Package Reference](packages/code-package-reference.md) — complete type catalog (Type, Statement, File, etc.)
- [Filesystem Package Reference](packages/filesystem-package-reference.md) — folder and file analysis types
- [Package Documentation](packages/) — reference docs for all built-in packages
