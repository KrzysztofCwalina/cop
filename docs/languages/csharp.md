# C# Walkthrough

This guide walks you through analyzing a C# project with cop. The main workflow is
**agent-driven**: as you build, you ask your coding agent to turn problems you notice into
permanent, enforceable cop rules. Later sections cover writing rules by hand, running the
built-in C# checks, and enforcing project layering.

---

## 1. Install Cop

Download the latest release for your platform from [GitHub Releases](https://github.com/KrzysztofCwalina/cop/releases) and add it to your PATH.

Verify the installation:

```bash
cop --version
```

---

## 2. Your Code

You don't add anything to your project to use cop — it analyzes the source files you already
have. A typical C# project looks like this:

```
src/
  Models/
    User.cs
    Order.cs
  Services/
    UserService.cs
MyProject.csproj
```

There's nothing to run yet: cop needs a rule first. You'll create one with your agent
(section 4) or by hand (section 5), then point cop at this code.

---

## 3. Set Up Agent Context

Run `cop init` once, in your **repository root** (not in `src/` or any other subfolder):

```bash
cop init
```

This generates instruction files (`.github/copilot-instructions.md`, `AGENTS.md`) that teach
**GitHub Copilot** how to write and run cop rules. Commit them to your repo.

<sub>Using Claude Code? Run `cop init --claude` to generate Claude Code instruction files (`.claude/commands/cop.md`) instead.</sub>

---

## 4. Create Rules with Your Agent

This is the primary way to use cop. As you build, you (or your coding agent) will notice
patterns you want to ban going forward — a `var` where you want explicit types, a `DateTime.Now`,
a missing XML doc comment. Instead of leaving a code-review comment that gets forgotten, ask
your agent to capture the problem as a cop rule. Because `cop init` taught the agent how cop
works, it writes the rule into your `cop-checks/` folder, runs it, and fixes the violations —
just like a compiler error.

Just ask:

> "Write a cop rule that flags any method longer than 30 statements"

> "Create a cop rule that every public type has an XML doc comment"

> "Add a cop rule that all `*Client` types must be sealed"

> "Ban `DateTime.Now` — we use `DateTimeOffset.UtcNow`"

### The self-check loop

When your agent produces code in a shape you don't like, turn that feedback into a permanent rule:

1. The agent writes code with a pattern you dislike (e.g. it uses `DateTime.Now`).
2. You say: **"Add a self-check that flags `DateTime.Now` — we use `DateTimeOffset.UtcNow` here."**
3. The agent adds a focused check to your `cop-checks/` folder.
4. From now on, `cop` catches that pattern before it reaches code review.

The next sections show what such a rule looks like and how to run it yourself.

---

## 5. Write and Run a Rule by Hand

You don't need an agent — you can author `.cop` files directly. Create a file called
`checks.cop` in your project root:

<!-- cop norun: `cb.Types.Methods:<methodPredicate>` fatals at runtime (expects Method, got collection) while `cop verify` passes — tracked in #50 -->
```cop norun
import csharp
import code
import code

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

Verify it, then run it from your project root. By default cop analyzes the current directory;
`-t <path>` narrows analysis to a subfolder (here `src/`):

```bash
cop verify checks.cop      # catch syntax/type errors first
cop checks.cop -t src/
```

Example output:

```
src/Models/Order.cs: warning: Public type Order is missing XML documentation
src/Services/UserService.cs: warning: Method ProcessBatch has 45 statements (max 30)

2 violation(s) found.
```

Exit code is `0` when clean and `1` when violations are found — suitable for CI. To organize
many rules, put one check per file in a `cop-checks/` folder with a `main.cop` entry point and
run `cop cop-checks/main.cop -t .` (this is exactly what your agent does for you).

---

## 6. Use Built-In Checks

Cop ships with comprehensive C# check packages — no `.cop` files needed:

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
import code

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
import code

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
