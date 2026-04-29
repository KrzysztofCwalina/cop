# Getting Started with Cop

Cop is a general-purpose data processing language. It can analyze source code (C#, Python, JavaScript, and more), filesystems, and other structured data.

## Installation

Download `cop.exe` from the [releases](https://github.com/KrzysztofCwalina/cop/releases) page and add it to your PATH. Cop ships as a self-contained executable — no .NET runtime needed.

Cop comes with a default package feed (`github.com/KrzysztofCwalina/cop`) so all standard packages are available out of the box.

## Hello World

Create a file called `checks.cop` in any project folder:

```ruby
import filesystem

# define a custom predicate
predicate deep(Folder) => Folder.Depth > 5

# :empty is a built-in predicate — works on any collection or string
foreach Disk.Folders:empty => PRINT('{warning:@yellow} Empty folder: {item.Path}')
# :deep is the custom predicate defined above
foreach Disk.Folders:deep => PRINT('{item.Path} is deeply nested ({item.Depth} levels)')
```

Run it:

```bash
cop run checks.cop
```

Cop scans the filesystem, filters collections with your predicates, and prints one line per match. Exit code 1 if any output, 0 if clean — suitable for CI.

## Source Code Analysis

Import the `code` package to analyze source code across languages (C#, Python, JavaScript, and more):

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

Import the `filesystem` package to work with folders and files on disk:

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

A `.cop` file is a program that filters collections of items and produces output. The building blocks are:

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

## Writing Checks

Most checks follow the same pattern: import a package, define predicates, filter collections, output violations.

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
import code              # Type, Statement, File, etc.
import code-analysis     # Violation type, toError/toWarning/toInfo functions
import filesystem        # Folder, DiskFile for file system analysis
import csharp            # C# coding conventions and checks
import javascript        # JavaScript/TypeScript conventions and checks
import python            # Python coding conventions and checks
```

Cop auto-discovers a `packages/` directory by walking up from the current folder. You can also point to a specific directory with `feed`:

```ruby
feed '../my-packages'
import csharp
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
cop run -c list-clients         # run by name with -c
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
| [`test`](docs/packages/test.md) | General testing principles and patterns |
| [`ui-web`](docs/packages/ui-web.md) | Web UI guidance for accessibility and responsive design |
| [`application-azure`](docs/packages/application-azure.md) | Azure app development guidance |

### C# / .NET

| Package | Description |
|---------|-------------|
| [`csharp`](docs/packages/dotnet/csharp.md) | C# coding conventions (var usage, exception handling, async patterns) |
| [`csharp-style`](docs/packages/dotnet/csharp-style.md) | C# style rules (naming, formatting) |
| [`csharp-library`](docs/packages/dotnet/csharp-library.md) | Library design conventions for .NET class libraries |
| [`csharp-library-client`](docs/packages/dotnet/csharp-library-client.md) | Patterns for .NET client libraries (retry, pagination, LROs) |
| [`csharp-library-client-azure`](docs/packages/dotnet/csharp-library-client-azure.md) | Azure SDK patterns (TokenCredential, Azure.Core integration) |
| [`test-nunit`](docs/packages/dotnet/test-nunit.md) | NUnit-specific testing patterns |

### JavaScript / TypeScript

| Package | Description |
|---------|-------------|
| [`javascript`](docs/packages/js/javascript.md) | JavaScript/TypeScript coding conventions and best practices |

### Python

| Package | Description |
|---------|-------------|
| [`python`](docs/packages/python/python.md) | Python coding conventions and best practices |
| [`python-library`](docs/packages/python/python-library.md) | Library design conventions for Python packages |

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

- name: Restore packages
  run: cop package restore

- name: Run checks
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

- Read the [Language Reference](docs/language-reference.md) for the full syntax
- Read the [CLI Reference](docs/cli-reference.md) for all commands and options
- Read the [Code Package Reference](docs/packages/code.md) for all types, properties, and predicates
- Read the [Filesystem Package Reference](docs/packages/filesystem.md) for file system analysis
- Browse `packages/` for examples of check definitions
