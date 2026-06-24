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

Run `cop init` to generate instruction files that teach **GitHub Copilot** how to write cop rules in your project:

```bash
cop init
```

Commit the generated files (`.github/copilot-instructions.md`, `AGENTS.md`) to your repo.

<sub>Using Claude Code? Run `cop init --claude` to generate Claude Code instruction files (`.claude/commands/cop.md`) instead.</sub>

---

## 4. Write a Simple Rule

Create a file called `checks.cop` in your project root:

<!-- cop norun: `cb.Types.Methods:<methodPredicate>` fatals at runtime (expects Method, got collection) while `cop verify` passes — tracked in #50 -->
```cop norun
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
cop run csharp-checks                      # all C# conventions
cop run csharp-checks -c no-var            # just the "no var" check
cop run csharp-library-checks              # library API design rules
cop run csharp-library-azure-checks        # Azure SDK conventions
```

---

## 7. Enforce Project Layering

Cop discovers your C# projects and their dependencies (from each `.csproj`). The
language-agnostic **`code-layering`** package lets you enforce architectural rules across
projects — for example, that UI projects must not depend directly on data projects.

Create `layering.cop`:

```cop
import csharp
import code
import code-layering

let cb = codebase(csharp.parse())

# UI projects must not depend directly on data projects.
let ui-projects = ['MyApp.Web' 'MyApp.Presentation']
let data-projects = ['MyApp.Data']

predicate isUiProject(Project) => Project.Name:in(ui-projects)
predicate isDataProjectName(string) => string:in(data-projects)
predicate dependsOnData(Project) => Project.References:any(isDataProjectName)

let violations = cb.Projects:isUiProject:dependsOnData
    :toError('UI project {item.Name} must not depend directly on a data project')

command MAIN = CHECK(violations)
```

Run it against your solution root:

```bash
cop layering.cop -t .
```

The check exits non-zero (and prints each offending project) when a UI project
references a data project, so you can wire it into CI.

> Tip: `cb.Projects` exposes each project's `Name` and `References` (its referenced project names).
> Use `Project.References:any(predicate)` to test whether a project depends on a set of projects.

---

## 8. Explore Further

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
