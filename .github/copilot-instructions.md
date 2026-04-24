# Copilot Instructions

## Build & Run

```bash
# Build the full solution
dotnet build cop.sln

# Run all tests
dotnet test tests/Cop.Tests/Cop.Tests.csproj
dotnet test tests/Lang.Tests/Lang.Tests.csproj

# Publish self-contained cop.exe
install/publish.ps1
```

**After any code change to cop/ projects**, rebuild:

```bash
install/publish.ps1                          # all platforms
install/publish.ps1 -Runtimes win-x64        # single platform
```

This publishes self-contained single-file EXEs into `install/<rid>/` subfolders. No .NET runtime needed on the target machine.

## Architecture

- **cop/** — Main Cop project folder containing:
  - **cop/cli/** — `cop.exe` CLI (.NET 10 console app, System.CommandLine). Commands for package management.
  - **cop/language/** — Cop language (namespace `Cop.Lang`). Parser, interpreter, evaluator, type system. General-purpose language features only.
  - **cop/runtime/** — Runtime engine (namespace `Cop.Providers`). Engine orchestrator, source parsers (C#, Python, JavaScript), provider loading and registration.
  - **cop/shared/** — Core library (Cop.Core): package models, CopProvider base class, DataObject binary format, feed manager, dependency resolver, restore engine, checksum manager.
- **providers/** — Data providers (filesystem, code, typespec, etc.). Each provider extends `CopProvider` and supplies typed collections to the language runtime. Built-in providers compile into `cop.exe`; external providers ship as separate DLLs.
- **packages/** — Seed packages organized by language (general at root, language-specific in dotnet/, js/, python/).
- **tests/Cop.Tests/** — NUnit 4.x tests for core library.
- **tests/Lang.Tests/** — NUnit 4.x tests for Cop language runtime.
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

## Language vs. Package Boundary

The C# language project (`cop/language/`) implements **only general-purpose language features**: keywords (`predicate`, `function`, `let`, `type`), the parser, evaluator, interpreter, and type system. **Domain-specific concepts** (e.g., Violation type, error/warning/info functions, severity levels, analysis rules) must **never** be added to C# code — they belong in `.cop` files in `packages/` packages. When adding a new capability, ask: "Is this a language feature or a domain concept?" Only language features go in C#.

Runtime data providers (`cop/runtime/` and `providers/`) supply collections to Cop packages via `runtime::` declarations (e.g., `runtime::Filesystem`, `runtime::Code`). All providers extend the `CopProvider` base class in `cop/shared/`. Built-in providers use a fast in-proc binary format (`DataObject[]` with a flat UTF-8 string heap) — no CLR string allocations per record. External providers can use JSON or the same binary format. Data providers are NOT domain-specific — they provide raw data that packages can query and analyze.
