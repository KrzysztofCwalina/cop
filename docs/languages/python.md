# Python Walkthrough

This guide walks you through analyzing a Python project with cop — from setup to writing and running custom rules.

---

## 1. Install Cop

Download the latest release for your platform from [GitHub Releases](https://github.com/KrzysztofCwalina/cop/releases) and add it to your PATH.

Verify the installation:

```bash
cop --version
```

---

## 2. Target a Python Project

Navigate to any directory containing Python source files (`.py`). Cop scans all `.py` files in the target directory tree.

Example project structure:

```
src/
  mypackage/
    __init__.py
    models.py
    services.py
pyproject.toml
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

```cop
import python
import code
import code-analysis

let cb = python.parse()

# Flag public classes without docstrings
predicate isUndocumented(Type) => Type.Documented == false && Type:isPublic

# Flag bare except clauses (catch-all exception handling)
predicate isBareExcept(Statement) => Statement.ErrorHandler == true && Statement.Generic == true

let undocumented = cb.Types:isUndocumented
    :toWarning('Public class {item.Name} is missing a docstring')

let bareExcepts = cb.Statements:isBareExcept
    :toWarning('Bare except at line {item.Line} — catch a specific exception')

command MAIN = CHECK(undocumented + bareExcepts)
```

---

## 5. Run the Rule

From your project root:

```bash
cop checks.cop -t src/
```

Example output:

```
src/mypackage/models.py: warning: Public class Order is missing a docstring
src/mypackage/services.py: warning: Bare except at line 42 — catch a specific exception

2 violation(s) found.
```

---

## 6. Use Built-In Checks

Cop ships with comprehensive Python check packages:

```bash
cop run python-checks                      # all Python conventions
cop run python-checks -c no-print          # just the "no print" check
cop run python-library-checks              # library API design rules
cop run python-library-azure-checks        # Azure SDK conventions
```

---

## 7. Enforce Package Layering

Cop discovers your Python packages/distributions and their dependencies (from each
`pyproject.toml` or `setup.py`). The language-agnostic **`code-layering`** package lets
you enforce architectural rules across distributions — for example, that core packages
must not depend on higher-level service packages.

Create `layering.cop`:

```cop
import python
import code
import code-layering

let cb = codebase(python.parse())

# Core packages must not depend on service packages.
let core-packages = ['my-core']
let service-packages = ['my-storage' 'my-identity']

predicate isCorePackage(Project) => Project.Name:in(core-packages)
predicate isServicePackageName(string) => string:in(service-packages)
predicate dependsOnService(Project) => Project.References:any(isServicePackageName)

let violations = cb.Projects:isCorePackage:dependsOnService
    :toError('Core package {item.Name} must not depend on a service package')

command MAIN = CHECK(violations)
```

Run it against your project root:

```bash
cop layering.cop -t .
```

The check exits non-zero (and prints each offending package) when a core package
references a service package, so you can wire it into CI.

> Tip: `cb.Projects` exposes each package's `Name` and `References` (its distribution dependencies).
> Use `Project.References:any(predicate)` to test whether a package depends on a set of packages.

---

## 8. Explore Further

### List all classes

```cop
import python
import code

let cb = python.parse()
command MAIN = foreach cb.Types => '{item.Name} ({item.Kind}) - {item.Methods.count()} methods'
```

### Find functions using eval()

```cop
import python
import code-analysis

let cb = python.parse()

predicate isEval(Statement) => Statement.MemberName == 'eval'

let violations = cb.Statements:isEval
    :toError('Do not use eval() at line {item.Line}')

command MAIN = CHECK(violations)
```

### Check for missing type hints

<!-- cop norun: `cb.Types.Methods:<methodPredicate>` fatals at runtime (expects Method, got collection) while `cop verify` passes — tracked in #50 -->
```cop norun
import python
import code
import code-analysis

let cb = python.parse()

predicate hasNoReturnType(Method) => Method.ReturnType == null && Method:isPublic

let violations = cb.Types.Methods:hasNoReturnType
    :toInfo('Public method {item.Name} has no return type annotation')

command MAIN = CHECK(violations)
```

---

## Available Collections

The `python.parse()` function returns a `Codebase` with these collections:

| Collection | Description |
|------------|-------------|
| `cb.Types` | All classes |
| `cb.Statements` | Function calls, raise, except clauses |
| `cb.Files` | Source files with import info |
| `cb.Lines` | Every line of code (with kind: code/comment/blank) |
| `cb.Projects` | pyproject.toml / setup.py projects with dependencies |

### Python-Specific Features

- **Docstring detection**: Triple-quoted docstrings are detected as documentation
- **Decorators**: `@staticmethod`, `@abstractmethod`, etc. are captured in `Decorators`
- **Exception handling**: `except` clauses are parsed as error handler statements with type info
- **Async**: `async def` functions are flagged with the `Async` modifier
- **Project discovery**: Parses `pyproject.toml` and `setup.py` for dependencies

---

## Tips

- Use `cop verify checks.cop` to check your rule for errors before running
- Use `-t path/` to limit analysis to a specific directory
- Run `cop help python-checks` to see all built-in Python checks
- Run `cop help code` to see available types and predicates
- Combine with other providers: `import python` + `import javascript` for polyglot projects
