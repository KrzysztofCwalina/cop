# Agent Cop

**Agent Cop** is a static analysis tool that detects and prevents code slop — the architectural violations, convention drift, and design debt that coding agents introduce at machine speed. It ships with built-in checks for common rules and a formal specification language (DSL) for expressing custom requirements that are deterministically enforced in CI.

Read the [product pitch](why-agent-cop.md) for the strategic rationale.

## Why Agent Cop

Coding agents produce 10x–100x the volume of code. Without deterministic enforcement, architects become bottlenecked reviewing everything for slop. Natural language instructions (copilot instructions, system prompts) are advisory — agents drift from them, and humans still can't trust the output without manual review.

Agent Cop solves this by giving architects a way to **formally specify** their rules in a simple DSL. These rules run as a build step, produce compiler-style errors, and feed directly back to agents for auto-remediation — no human in the loop.

## Installation

Download `cop.exe` from the [releases](https://github.com/KrzysztofCwalina/cop/releases) page and add it to your PATH. Cop ships as a self-contained executable — no .NET runtime needed.

Cop comes with a default package feed (`github.com/KrzysztofCwalina/cop`) so all standard packages are available out of the box.

### VS Code Extension

For syntax highlighting and completions, install the VS Code extension:

1. Open VS Code
2. Press `Ctrl+Shift+P` → **Extensions: Install from VSIX...**
3. Select `install/vscode-cop/` from this repository (or package it first with `cd install/vscode-cop && npx @vscode/vsce package`)

The extension provides syntax highlighting, keyword completions, and snippet support for `.cop` files.

## Quick Start

Create a file called `checks.cop` in any project folder:

```ruby
import code-analysis

# Define what a "client type" means
predicate client(Type) => Type.Name:endsWith('Client')

# Specify the rule: clients must be sealed
let unsealed-clients = Code.Types:csharp:client:!isSealed
    :toError('{item.Name} must be sealed')

CHECK(unsealed-clients)
```

Run it:

```bash
cop run checks.cop
```

Output:

```
src/BlobClient.cs(15): error: BlobClient must be sealed
src/QueueClient.cs(8): error: QueueClient must be sealed
```

Exit code 1 if violations found, 0 if clean — suitable for CI. Agents see these errors and fix them automatically, just like compiler errors.

## Running Built-In Checks

Agent Cop ships with pre-built check packages for common rules. No `.cop` files needed:

```bash
cop check csharp-style            # naming, formatting, documentation
cop check csharp-library          # library design conventions
cop check csharp-style csharp-library   # run multiple packages
cop check csharp-style -t src/    # analyze a specific directory
```

## Source Code Analysis

The specification language works across languages (C#, Python, JavaScript). Write rules once, enforce everywhere:

```ruby
import code

# Predicate on Method — checks statement count
predicate longMethod(Method) => Method.Statements.Count > 50
# Predicate on Type — checks if any method is long
predicate hasLongMethod(Type) => Type.Methods:any(longMethod)

foreach Code.Types => PRINT('{item.Name} in {item.File.Path}')
foreach Code.Types:hasLongMethod => PRINT('{warning:@yellow} {item.Name} has a method with too many statements')
```

Use language-specific packages for targeted rules. For example, a project with both C# and JavaScript:

```ruby
import csharp
import javascript

# These checks automatically apply to the right language
# csharp checks only match .cs files, javascript checks only match .js/.ts files
foreach csharp-checks => PRINT('{item.File@dim}:{item.Line@dim} {item.Message}')
foreach javascript-checks => PRINT('{item.File@dim}:{item.Line@dim} {item.Message}')
```

Or write cross-language rules using the `code` package directly:

```ruby
import code

# Catches that swallow all errors without rethrowing — works across C#, Python, JS
predicate swallowsError(ErrorHandler) => ErrorHandler.Generic && !ErrorHandler.Rethrows
foreach ErrorHandlers:swallowsError => PRINT('{error:@red} {ErrorHandler.File.Path}:{ErrorHandler.Line} swallows error')
```

## Filesystem Analysis

The same specification language works for filesystem structure rules:

```ruby
import filesystem

# Find empty folders
foreach Disk.Folders:empty => PRINT('{warning:@yellow} Empty folder: {item.Path}')

# Find large files (over 1MB)
predicate large(DiskFile) => DiskFile.Size > 1048576
foreach Disk.Files:large => PRINT('{item.Path} ({item.Size} bytes)')

# Find stale folders not modified in 24+ hours
foreach Disk.Folders:stale => PRINT('{item.Path} — last modified {item.MinutesSinceModified} min ago')
```

Available collections: `Disk.Folders` (all folders) and `Disk.Files` (all files). Each has properties like `Path`, `Name`, `Depth`, `Size`, `Extension`, `MinutesSinceModified`.

## Working with JSON Data

Load and process JSON files with the `json` package:

```ruby
import json

type Task = { id : int, title : string, status : string, assignee : string }
let Tasks = Parse('tasks.json', [Task])

predicate blocked(Task) => Task.status == 'blocked'
predicate unassigned(Task) => Task.assignee:empty

foreach Tasks:blocked => PRINT('{warning:@yellow} Blocked: {item.title}')
foreach Tasks:unassigned => PRINT('{item.title} has no assignee')
```

`Parse(path, [Type])` reads a JSON array file and deserializes each element into a typed object.

## Core Concepts

A `.cop` file is a formal specification that declares rules over collections of code elements and produces deterministic pass/fail results. The building blocks are:

- **`import`** — bring types and collections from a package into scope
- **`predicate`** — a named boolean test over a typed item
- **`let`** — declare a named collection (base or filtered subset)
- **`foreach`** — iterate over a collection
- **`PRINT`** / **`SAVE`** — commands that produce output
- **`command`** — a named command that can be invoked individually

## Filtering and Subsets

The `:` operator is the core of cop — it filters a list to a subset:

```ruby
import code

# Chain predicates to narrow results (each : is AND)
let PublicClients = Code.Types:csharp:client:isPublic

# Negate with !
let InternalTypes = Code.Types:csharp:!isPublic

# Use built-in predicates directly on properties
let LongNames = Code.Types:csharp:Name:startsWith('Azure')
let RecentFiles = Disk.Files:recentlyModified
```

### Collection Operations

```ruby
# Concatenate collections with +
let allChecks = csharp-checks + javascript-checks + python-checks

# Project to a list of values
let typeNames = Code.Types.Select(item.Name)

# Format and join into a single string
let report = Code.Types:client.Text('{item.Name} — {item.File.Path}')

# Test sub-collections
predicate hasPublicCtor(Type) => Type.Constructors:any(isPublic)
predicate noMethods(Type) => Type.Methods:empty
```

## Writing Custom Rules

Most rules follow the same pattern: import a package, define predicates, filter collections, output violations.

```ruby
import code

# Predicates test individual items
predicate client(Type) => Type.Name:endsWith('Client')

# Let declarations create filtered subsets
let Clients = Code.Types:client

# foreach iterates over the subset — one line per item
foreach Clients:csharp:!isSealed => PRINT('{error:@red} {item.Name} should be sealed')
```

Predicates compose — you can reference one predicate from another:

```ruby
predicate optionsType(Parameter) => Parameter.Type.Name:endsWith('Options')
predicate hasOptions(Constructor) => Constructor.Parameters:any(optionsType)
predicate missingOptions(Type) => Type.Constructors:none(hasOptions)

foreach Clients:missingOptions => PRINT('{warning:@yellow} {item.Name} needs an options constructor')
```

### Styled Output

Use `{text@style}` to colorize output:

```ruby
foreach Code.Types:csharp:!isSealed => PRINT('{error:@red} {item.Name} should be sealed')
foreach Code.Lines:python:todoComment => PRINT('{info:@cyan} {item.File.Path}:{item.Number} {item.Text}')
```

Available styles: `@red`, `@yellow`, `@green`, `@cyan`, `@dim`, `@bold`, `@auto` (auto-colors by severity keyword).

### Saving Output to Files

Use `SAVE` to write results to a file instead of the console:

```ruby
command export-types = foreach Code.Types:csharp => SAVE('types.txt', '{item.Name}')
```

Run it explicitly: `cop run export-types`.

## Excluding Files

Skip paths from scanning with `exclude`:

```ruby
exclude '**/node_modules/**'
exclude '**/bin/**'
exclude '**/generated/**'
```

Or filter inline:

```ruby
let prodCode = Code.Statements:sleepCall:!Path('**/test/**')
```

## Strings and Identifiers

Cop has two string comparison modes to make cross-language checks easy:

| Mode | Syntax | Behavior |
|---|---|---|
| **CaseInsensitive** | `==`, `!=`, `contains`, etc. | Ignores letter case (default for all operations) |
| **ConventionInsensitive** | `:sameAs()` or `.Normalized` | Ignores case AND naming convention (`FooBar` = `foo_bar` = `fooBar`) |

### CaseInsensitive (Default)

All string comparisons ignore case by default:

| Operation | Example | Behavior |
|---|---|---|
| `==`, `!=` | `item.Name == 'foo'` | Case-insensitive equality |
| `contains` | `Name:contains('task')` | Case-insensitive substring |
| `startsWith` | `Name:startsWith('i')` | Case-insensitive prefix |
| `endsWith` | `Name:endsWith('Client')` | Case-insensitive suffix |
| `matches` | `Name:matches('^Foo$')` | **Case-sensitive** regex (escape hatch) |

### ConventionInsensitive

Use `:sameAs()` to compare identifiers regardless of naming convention:

```ruby
# All of these are true — FooBar, foo_bar, fooBar, FOO_BAR all normalize to the same form
Type.Name:sameAs('foo_bar')
Type.Name:sameAs('FooBar')
Type.Name:sameAs('fooBar')
```

This is ideal for cross-language rules where C# uses `PascalCase`, Python uses `snake_case`, and JS uses `camelCase`.

### Identifier Normalization

The `.Words` property splits identifiers into lowercase words, normalizing across naming conventions:

```ruby
# PascalCase → ['task', 'completion', 'source']
# camelCase  → ['task', 'completion', 'source']
# snake_case → ['task', 'completion', 'source']
Type.Name.Words:contains('task')
```

### String Properties

```ruby
Type.Name.Lower       # 'Foo' → 'foo'
Type.Name.Upper       # 'Foo' → 'FOO'
Type.Name.Normalized  # 'Foo_Bar' → 'foobar' (convention-insensitive canonical form)
Type.Name.Length      # string length
```

## Packages

Packages provide reusable types, predicates, and checks. Use `import` to bring them into scope:

```ruby
import code              # Type, Statement, File definitions and modifier predicates
import filesystem        # Folder, DiskFile for file system analysis
import csharp            # C# language provider (scans .cs files)
import javascript        # JavaScript/TypeScript provider (scans .js/.ts files)
import python            # Python provider (scans .py files)
```

Cop auto-discovers a `packages/` directory by walking up from the current folder. You can also point to a local directory or a GitHub repository with `feed`:

```ruby
# Local package directory
feed '../my-packages'

# GitHub repository (packages are fetched on first use)
feed 'github.com/my-org/my-packages'

import my-custom-package
```

The `code-analysis` package provides the `CHECK` command for structured violation output:

```ruby
import code-analysis

let errors = Code.Types:csharp:client:!isSealed
    :toError('{item.Name} must be sealed')

CHECK(errors)
```

## Named Commands

Give commands a name so they can be run individually:

```ruby
import code

predicate client(Type) => Type.Name:endsWith('Client')

# Named commands — only run when invoked explicitly
command list-types = foreach Code.Types => PRINT('{item.Name} ({item.Kind}) — {item.File.Path}')
command list-clients = foreach Code.Types:client => PRINT('{item.Name}')
command count-files = PRINT('{Code.Files.Count} source files')
```

```bash
cop run list-types              # run just that command
cop run -c list-types,count-files  # run multiple commands
cop run -f json                 # all output as JSON
cop run -t src/                 # scan only the src/ directory
```

Unnamed statements (bare `foreach`) always run. Named commands only run when invoked by name or with `-c`.

## Available Packages

### General Purpose

| Package | Description |
|---------|-------------|
| [`code`](docs/packages/code.md) | Core type definitions for source code analysis (Type, Statement, File, etc.) |
| [`code-analysis`](docs/packages/code-analysis.md) | Violation type and severity functions (toError, toWarning, toInfo) |
| [`filesystem`](docs/packages/filesystem.md) | Type definitions for filesystem analysis (Folder, DiskFile) |
| [`json`](docs/packages/json.md) | Parse JSON files into typed collections using user-defined schemas |
| [`markdown`](docs/packages/code.md) | Analyze markdown documents (headings, links, sections, fenced code blocks) |

### C# / .NET

| Package | Description |
|---------|-------------|
| [`csharp`](docs/packages/dotnet/csharp.md) | C# language provider (scans .cs files) and coding conventions |
| [`csharp-style`](docs/packages/dotnet/csharp-style.md) | C# style rules (naming, formatting) |
| [`fdg`](docs/packages/dotnet/fdg.md) | .NET Framework Design Guidelines (~26 checks: naming, type/member/exception design) |
| [`csharp-library`](docs/packages/dotnet/csharp-library.md) | Library design conventions for .NET class libraries |
| [`csharp-library-client`](docs/packages/dotnet/csharp-library-client.md) | Patterns for .NET client libraries (retry, pagination, LROs) |
| [`csharp-library-client-azure`](docs/packages/dotnet/csharp-library-client-azure.md) | Azure SDK patterns (TokenCredential, Azure.Core integration) |
| [`csharp-api`](docs/packages/dotnet/csharp.md) | API surface tracking, export, and diff for .NET libraries |
| [`csharp-snippets`](docs/packages/dotnet/csharp.md) | Validates C# code snippets in #region blocks match markdown docs |
| [`test-nunit`](docs/packages/dotnet/test-nunit.md) | NUnit-specific testing patterns |

### JavaScript / TypeScript

| Package | Description |
|---------|-------------|
| [`javascript`](docs/packages/js/javascript.md) | JavaScript/TypeScript language provider and coding conventions |

### Python

| Package | Description |
|---------|-------------|
| [`python`](docs/packages/python/python.md) | Python language provider and coding conventions |
| [`python-library`](docs/packages/python/python-library.md) | Library design conventions for Python packages |

### TypeSpec

| Package | Description |
|---------|-------------|
| [`typespec`](docs/packages/typespec.md) | TypeSpec API specification analysis (raw type graph) |
| [`typespec-http`](docs/packages/typespec-http.md) | TypeSpec HTTP protocol graph (verbs, paths, parameters) |

## Testing

Write tests with `ASSERT` and `ASSERT_EMPTY`, run them with `cop test`:

```ruby
import csharp

command test-has-types = ASSERT(csharp.Types)
command test-no-var = ASSERT_EMPTY(csharp.Statements:varDeclaration)
command test-public = ASSERT(csharp.Types:isPublic, 'expected public types')
```

```bash
cop test
```

```
  ✓ test-has-types
  ✓ test-no-var
  ✗ test-public: expected public types (found 0 items)

  3 tests, 2 passed, 1 failed
```

See [Testing with Cop](docs/testing-with-cop.md) for the full testing guide.

## CI Integration

Cop's exit codes make it easy to integrate into CI pipelines:

```yaml
# GitHub Actions
- name: Install cop
  run: |
    curl -L https://github.com/KrzysztofCwalina/cop/releases/latest/download/cop-linux-x64.zip -o cop.zip
    unzip cop.zip && chmod +x cop && mv cop /usr/local/bin/

- name: Run style checks
  run: cop check csharp-style

- name: Run custom checks
  run: cop run

- name: Run tests
  run: cop test
```

| Exit Code | Meaning |
|-----------|---------|
| `0` | Clean — no output, all tests pass |
| `1` | Violations found or tests failed |
| `2` | Fatal error (parse error, missing package) |

## Next Steps

- Read [Why Agent Cop](why-agent-cop.md) for the product strategy and rationale
- Read the [Language Reference](docs/language-reference.md) for the full specification DSL syntax
- Read the [CLI Reference](docs/cli-reference.md) for all commands and options
- Read [Static Analysis with Cop](docs/static-analysis-with-cop.md) for writing source code checks
- Read [Testing with Cop](docs/testing-with-cop.md) for writing and running tests
- Browse `packages/` for examples of check definitions
