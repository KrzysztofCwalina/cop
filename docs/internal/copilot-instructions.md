# Agent Cop Architecture

## Overview

Agent Cop is a governed agentic software development system. It provides a custom scripting language (`.cop` files) for defining analysis rules over source code and filesystems, and a CLI for package management.

The system has two main deliverables:

- **cop.exe** (CLI) — Package management, rule evaluation, lock/unlock enforcement
- **packages/** — Repository-based package distribution (directories, not zips)

## Directory Structure

```
cop/cli/            CLI (.NET 10, System.CommandLine). Commands for
                    package management.

cop/language/       Cop language (namespace Cop.Lang). Parser, interpreter,
                    evaluator, type system. General-purpose language features only.

cop/runtime/        Runtime engine (namespace Cop.Providers). Engine orchestrator,
                    source parsers (C#, Python, JavaScript), provider loading
                    and registration.

cop/shared/         Core library (namespace Cop.Core). DataProvider base class,
                    DataObject binary format, package models, feed manager,
                    dependency resolver, restore engine, checksum/lock file manager.

providers/          Data providers. Each extends DataProvider and supplies typed
                    collections to the language runtime.
  filesystem-provider/  Folders, DiskFiles — built-in, uses DataObject[] format
  code-provider/        Types, Methods, Statements, Files — built-in, uses CLR objects
  typespec-provider/    TypeSpec HTTP API analysis — external, uses JSON format
  csharp-provider/      C# language-specific analysis
  python-provider/      Python language-specific analysis
  javascript-provider/  JavaScript/TypeScript language-specific analysis

packages/           Seed packages organized by language. General packages are at
                    the root; language-specific packages are in group folders
                    (dotnet/, js/, python/). Each package contains metadata,
                    instructions, skills, rules, and tests.

tests/Cop.Tests/    NUnit 4.x tests for core library
tests/Lang.Tests/   NUnit 4.x tests for language runtime

install/            Publish script and platform subfolders for self-contained binaries
                      install/publish.ps1 builds cop.exe for all platforms
                      install/vscode-cop/ — VS Code syntax highlighting extension
docs/               Documentation
samples/            Example .cop scripts (s1-HelloWorld through s6-Strings)
```

## Key Classes

### Language — `cop/language/` (Cop.Lang)

| Class | File | Role |
|---|---|---|
| **Tokenizer** | `Tokenizer.cs` | Lexical analysis of `.cop` source text |
| **ScriptParser** | `ScriptParser.cs` | Parses token stream into AST |
| **ScriptInterpreter** | `ScriptInterpreter.cs` | Evaluates commands, `foreach` loops, implicit output, `SAVE` actions |
| **PredicateEvaluator** | `PredicateEvaluator.cs` | Evaluates predicate chains (`:filter`, `.Where(...)`, `.Select(...)`) on collections |
| **TypeRegistry** | `TypeRegistry.cs` | Type system — property resolution, method dispatch |
| **ScriptObject** | `ScriptObject.cs` | Runtime value representation for all Cop values |

### Runtime — `cop/runtime/` (Cop.Providers)

| Class | File | Role |
|---|---|---|
| **Engine** | `Engine.cs` | Main orchestrator. Loads `.cop` files, resolves imports, runs interpreter |
| **CSharpSourceParser** | `SourceParsers/CSharpSourceParser.cs` | Parses C# source into the source model (types, methods, statements) |
| **PythonSourceParser** | `SourceParsers/PythonSourceParser.cs` | Parses Python source into the source model |
| **ProviderLoader** | `ProviderLoader.cs` | Loads external provider DLLs, queries data, registers into type system. Also provides `RegisterSchema()` for built-in providers. |

Source model types (e.g., `MethodDeclaration`, `StatementInfo`) live in `cop/runtime/` and represent parsed source code structures that `.cop` scripts can query.

### Providers — `providers/` and `cop/shared/` (Cop.Core)

| Class | File | Role |
|---|---|---|
| **DataProvider** | `cop/shared/DataProvider.cs` | Base class for all data providers. Defines schema, format negotiation, query API |
| **DataObject** | `cop/shared/DataObject.cs` | 192-byte blittable struct — 24 × 8-byte slots for compact binary records |
| **DataTable** | `cop/shared/DataObject.cs` | Wraps `DataObject[]` + UTF-8 string heap + type name |
| **DataObjectView** | `cop/shared/DataObject.cs` | Lightweight evaluator bridge — references a record without boxing the 192-byte struct |
| **FilesystemProvider** | `providers/filesystem-provider/` | Scans directories, packs into `DataObject[]` with shared string heap |
| **CodeProvider** | `providers/code-provider/` | Provides code-analysis types via CLR objects and collection extractors |

### Core Library — `cop/shared/` (Cop.Core)

| Class | File | Role |
|---|---|---|
| **RestoreEngine** | `RestoreEngine.cs` | Resolves and downloads packages into the local workspace |
| **FeedManager** | `FeedManager.cs` | Manages configured package sources (GitHub repos, local paths) |
| **LockFile** | `LockFile.cs` | `.cop-lock` file management with HMAC-SHA256 integrity verification |
| **DependencyResolver** | `DependencyResolver.cs` | Transitive dependency resolution with cycle detection |

## Language vs Package Boundary

This is a critical architectural rule:

- **`cop/language/`** implements **only general-purpose language features**: keywords (`predicate`, `function`, `let`, `type`), the parser, evaluator, interpreter, and type system. No domain-specific concepts.
- **Domain-specific concepts** — the `Violation` type, `CHECK` command, `error`/`warning`/`info` functions, severity levels, analysis rules — belong **exclusively in `.cop` files** inside `packages/`. They must never be added to C# code.
- **`cop/runtime/`** provides **general-purpose data providers** that supply collections to `.cop` packages via `runtime::` declarations (e.g., `runtime::Filesystem`, `runtime::Code`). Data providers are not domain-specific — they provide raw data that packages query and analyze.
- **`providers/`** contains all provider implementations. Each provider extends `DataProvider` (defined in `cop/shared/`). Built-in providers use the fast `DataObject[]` binary format with a flat UTF-8 string heap — no CLR string allocations per record. External providers can use JSON or the same binary format.

When adding a new capability, ask: *"Is this a language feature or a domain concept?"* Only language features go in C#.

## Package Format

Packages are directories (not zips) under `packages/` in GitHub repositories. Each package contains:

- **metadata** — `.md` file with YAML frontmatter (name, version, dependencies)
- **instructions/** — Markdown guidance for coding agents
- **skills/** — Agent skill definitions
- **rules/** — `.cop` rule files evaluated by the language runtime
- **tests/** — Package-specific tests

## Build, Test, Publish

```bash
# Build the full solution
dotnet build cop.sln

# Run core library tests (44 tests)
dotnet test tests/Cop.Tests/Cop.Tests.csproj

# Run language runtime tests (281 tests)
dotnet test tests/Lang.Tests/Lang.Tests.csproj

# Publish self-contained cop.exe for all platforms
install/publish.ps1

# Or publish for a single platform
install/publish.ps1 -Runtimes win-x64
```

**After any code change** to `cop/` or related projects, rebuild:

```bash
install/publish.ps1 -Runtimes win-x64        # single platform (fast)
install/publish.ps1                           # all platforms
```

This publishes self-contained single-file EXEs into `install/<rid>/` subfolders. No .NET runtime is required on the target machine.

## Conventions

- **Target framework:** `net10.0`
- **Nullable reference types:** enabled
- **Implicit usings:** enabled
- **Test framework:** NUnit 4.x
- **Style:** keep code simple — prefer straightforward code over abstractions
- **`.cop` files:** single-quoted strings, `{Prop}` template interpolation, `{text@style}` for styled output, `feed` and `import` for package management
- **Packages:** directories (not zips) under `packages/` in GitHub repos
- **Markdown:** used for instructions, documentation, and package metadata (YAML frontmatter)
