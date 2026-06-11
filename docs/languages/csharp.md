# C# Walkthrough

This guide walks you through analyzing a C# project with cop — from setup to writing and running custom rules.

---

## 1. Install Cop

Download the latest release for your platform from [GitHub Releases](https://github.com/KrzysztofCwalina/cop/releases) and add it to your PATH.

Verify the installation:

```bash
cop --version
```

---

## 2. Target a C# Project

Navigate to any directory containing C# source files (`.cs`). Cop scans all `.cs` files in the target directory tree.

Example project structure:

```
src/
  Models/
    User.cs
    Order.cs
  Services/
    UserService.cs
MyProject.csproj
```

---

## 3. Set Up Agent Context

Run `cop init` to generate instruction files that teach coding agents (GitHub Copilot, Claude Code) how to write cop rules in your project:

```bash
cop init
```

Commit the generated files (`.github/copilot-instructions.md`, `AGENTS.md`) to your repo.

---

## 4. Write a Simple Rule

Create a file called `checks.cop` in your project root:

```cop
import csharp
import code
import code-analysis

let cb = csharp.parse()

# Flag public types without XML doc comments
predicate isUndocumented(Type) => Type.Documented == false && Type:isPublic

# Flag methods longer than 30 statements
predicate isTooLong(Method) => Method.Statements.count() > 30

let undocumented = cb.Types:isUndocumented
    :toWarning('Public type {item.Name} is missing XML documentation')

let longMethods = cb.Types.Methods:isTooLong
    :toWarning('Method {item.Name} has {item.Statements.count()} statements (max 30)')

command MAIN = CHECK(undocumented + longMethods)
```

---

## 5. Run the Rule

From your project root:

```bash
cop checks.cop -t src/
```

Example output:

```
src/Models/Order.cs: warning: Public type Order is missing XML documentation
src/Services/UserService.cs: warning: Method ProcessBatch has 45 statements (max 30)

2 violation(s) found.
```

---

## 6. Use Built-In Checks

Cop ships with comprehensive C# check packages:

```bash
cop csharp-checks                          # all C# conventions
cop csharp-checks -c no-var                # just the "no var" check
cop csharp-library-checks                  # library API design rules
cop csharp-library-azure-checks            # Azure SDK conventions
```

---

## 7. Explore Further

### List all public classes

```cop
import csharp
import code

let cb = csharp.parse()
command MAIN = foreach cb.Types:isPublic => '{item.Name} ({item.Kind})'
```

### Find types implementing a specific interface

```cop
import csharp
import code

let cb = csharp.parse()
command MAIN = foreach cb.Types => '{item.Name}: {item.Interfaces}'
```

### Check sealed classes pattern (Azure SDK)

```cop
import csharp
import code
import code-analysis

let cb = csharp.parse()

predicate isClient(Type) => Type.Name:endsWith('Client')
predicate isNotSealed(Type) => Type:isSealed == false

let violations = cb.Types:isClient:isPublic:isNotSealed
    :toError('Client type {item.Name} must be sealed')

command MAIN = CHECK(violations)
```

---

## Available Collections

The `csharp.parse()` function returns a `Codebase` with these collections:

| Collection | Description |
|------------|-------------|
| `cb.Types` | All classes, structs, interfaces, enums |
| `cb.Statements` | Method calls, new expressions, throw, using, etc. |
| `cb.Files` | Source files with namespace and using info |
| `cb.Lines` | Every line of code (with kind: code/comment/blank) |
| `cb.Projects` | .csproj projects with references and packages |
| `cb.Api` | Public API surface (types, methods, properties) |

### C#-Specific Features

- **Semantic analysis**: The C# provider uses Roslyn to resolve interfaces, base types, and constructed type interfaces
- **Project discovery**: Parses `.csproj` files for `PackageReference`, `ProjectReference`, `TargetFramework`
- **Modifiers**: Full support for `public`, `private`, `protected`, `internal`, `static`, `sealed`, `abstract`, `virtual`, `override`, `async`, `readonly`, `const`

---

## Tips

- Use `cop verify checks.cop` to check your rule for errors before running
- The C# provider supports full Roslyn semantic analysis for interface resolution
- Use `-t path/` to limit analysis to a specific directory
- Run `cop help csharp-checks` to see all built-in C# checks
- Run `cop help code` to see available types and predicates
