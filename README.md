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

# :empty is a built-in predicate from the filesystem package
foreach Disk.Folders:empty => PRINT('{warning:@yellow} Empty folder: {Folder.Path}')
# :deep is the custom predicate defined above
foreach Disk.Folders:deep => PRINT('{Folder.Path} is deeply nested ({Folder.Depth} levels)')
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

# Works across all supported languages
predicate longMethod(Method) => Method.Statements:count > 50

foreach Code.Types => PRINT('{Type.Name} in {Type.File.Path}')
foreach Code.Types:longMethod => PRINT('{warning:@yellow} {Method.Name} has too many statements')
```

Use language-specific packages for targeted rules. For example, a project with both C# and JavaScript:

```ruby
import csharp
import javascript

# These checks automatically apply to the right language
# csharp checks only match .cs files, javascript checks only match .js/.ts files
foreach csharp-checks => PRINT('{Violation.File@dim}:{Violation.Line@dim} {Violation.Message}')
foreach javascript-checks => PRINT('{Violation.File@dim}:{Violation.Line@dim} {Violation.Message}')
```

Or write cross-language rules using the `code` package directly:

```ruby
import code

# Catches that swallow all errors without rethrowing — works across C#, Python, JS
predicate swallowsError(ErrorHandler) => ErrorHandler.Generic && !ErrorHandler.Rethrows
foreach ErrorHandlers:swallowsError => PRINT('{error:@red} {ErrorHandler.File.Path}:{ErrorHandler.Line} swallows error')
```

## Core Concepts

A `.cop` file is a program that filters collections of items and produces output. The building blocks are:

- **`import`** — bring types and collections from a package into scope
- **`predicate`** — a named boolean test over a typed item
- **`let`** — declare a named collection (base or filtered subset)
- **`foreach`** — iterate over a collection
- **`PRINT`** / **`SAVE`** — commands that produce output
- **`command`** — a named command that can be invoked individually

## Writing Checks

Most checks follow the same pattern: import a package, define predicates, filter collections, output violations.

```ruby
import code

# Predicates test individual items
predicate client(Type) => Type.Name:endsWith('Client')
predicate clientOptions(Type) => Type.Name:endsWith('ClientOptions')

# Let declarations create filtered subsets
let Clients = Code.Types:client:!clientOptions

# foreach iterates over the subset — one line per item
foreach Clients:csharp:!Sealed => PRINT('{error:@red} {Type.Name} should be sealed')
```

Predicates compose — you can reference one predicate from another:

```ruby
predicate optionsType(Parameter) => Parameter.Type.Name:endsWith('Options')
predicate hasOptions(Constructor) => Constructor.Parameters:any(optionsType)
predicate missingOptions(Type) => Type.Constructors:none(hasOptions)

foreach Clients:missingOptions => PRINT('{warning:@yellow} {Type.Name} needs an options constructor')
```

## Strings and Identifiers

Cop has two string comparison modes to make cross-language checks easy:

| Mode | Syntax | Behavior |
|---|---|---|
| **CaseInsensitive** | `==`, `!=`, `contains`, etc. | Ignores letter case (default for all operations) |
| **ConventionInsensitive** | `:same()` or `.Normalized` | Ignores case AND naming convention (`FooBar` = `foo_bar` = `fooBar`) |

### CaseInsensitive (Default)

All string comparisons ignore case by default:

| Operation | Example | Behavior |
|---|---|---|
| `==`, `!=` | `Type.Name == 'foo'` | Case-insensitive equality |
| `contains` | `Name:contains('task')` | Case-insensitive substring |
| `startsWith` | `Name:startsWith('i')` | Case-insensitive prefix |
| `endsWith` | `Name:endsWith('Client')` | Case-insensitive suffix |
| `matches` | `Name:matches('^Foo$')` | **Case-sensitive** regex (escape hatch) |

### ConventionInsensitive

Use `:same()` to compare identifiers regardless of naming convention:

```ruby
# All of these are true — FooBar, foo_bar, fooBar, FOO_BAR all normalize to the same form
Type.Name:same('foo_bar')
Type.Name:same('FooBar')
Type.Name:same('fooBar')
```

This is ideal for cross-language rules where C# uses `PascalCase`, Python uses `snake_case`, and JS uses `camelCase`.

### Identifier Normalization

The `:words` predicate splits identifiers into lowercase words, normalizing across naming conventions:

```ruby
# PascalCase → ['task', 'completion', 'source']
# camelCase  → ['task', 'completion', 'source']
# snake_case → ['task', 'completion', 'source']
Type.Name:words:contains('task')
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

let errors = Code.Types:csharp:client:!Sealed
    :toError('{Type.Name} must be sealed')

CHECK(errors)
```

## Named Commands

Give commands a name so they can be run individually:

```ruby
command check-var = foreach Code.Statements:csharp:varDeclaration => PRINT('{error:@red} {Statement.File.Path} uses var')
command check-clients = foreach Clients:csharp:!Sealed => PRINT('{error:@red} {Type.Name} must be sealed')
```

```bash
cop run rules.cop --commands:check-clients     # run only check-clients
cop run rules.cop --format:json                 # JSON output
```

## Available Packages

### General Purpose

| Package | Description |
|---------|-------------|
| [`code`](docs/packages/code-package-reference.md) | Core type definitions for source code analysis (Type, Statement, File, etc.) |
| [`code-analysis`](docs/packages/code-analysis.md) | Violation type and severity functions (toError, toWarning, toInfo) |
| [`filesystem`](docs/packages/filesystem-package-reference.md) | Type definitions for filesystem analysis (Folder, DiskFile) |
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

## Next Steps

- Read the [Language Reference](docs/cop-language-reference.md) for the full syntax
- Read the [Code Package Reference](docs/packages/code-package-reference.md) for all types, properties, and predicates
- Read the [Filesystem Package Reference](docs/packages/filesystem-package-reference.md) for file system analysis
- Browse `packages/` for examples of check definitions
