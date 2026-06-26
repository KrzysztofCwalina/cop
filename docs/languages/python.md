# Python Walkthrough

This guide walks you through analyzing a Python project with cop. The main workflow is
**agent-driven**: as you build, you ask your coding agent to turn problems you notice into
permanent, enforceable cop rules. Later sections cover writing rules by hand, running the
built-in Python checks, and enforcing package layering.

---

## 1. Install Cop

Download the latest release for your platform from [GitHub Releases](https://github.com/KrzysztofCwalina/cop/releases) and add it to your PATH.

Verify the installation:

```bash
cop --version
```

---

## 2. Set Up Agent Context

Run `cop init` once, in your **repository root** (not in `src/` or any other subfolder):

```bash
cop init
```

This generates instruction files (`.github/copilot-instructions.md`, `AGENTS.md`) that teach
**GitHub Copilot** how to write and run cop rules. Commit them to your repo.

<sub>Using Claude Code? Run `cop init --claude` to generate Claude Code instruction files (`.claude/commands/cop.md`) instead.</sub>

---

## 3. Create Rules with Your Agent

This is the primary way to use cop. As you build, you (or your coding agent) will notice
patterns you want to ban going forward — a bare `except:`, a stray `print()`, a missing
docstring. Instead of leaving a code-review comment that gets forgotten, ask your agent to
capture the problem as a cop rule. Because `cop init` taught the agent how cop works, it
writes the rule into your `cop-checks/` folder, runs it, and fixes the violations — just like
a compiler error.

Just ask:

> "Write a cop rule that flags bare `except:` clauses — we catch specific exceptions"

> "Add a cop rule that bans `print()` — use the logging module"

> "Create a cop rule that every public class in `src/` has a docstring"

> "Add a cop rule that forbids `eval()` anywhere in the codebase"

### The self-check loop

When your agent produces code in a shape you don't like, turn that feedback into a permanent rule:

1. The agent writes code with a pattern you dislike (e.g. it uses `print()` for diagnostics).
2. You say: **"Add a self-check that flags `print()` — we use the logging module here."**
3. The agent adds a focused check to your `cop-checks/` folder.
4. From now on, `cop` catches that pattern before it reaches code review.

The next sections show what such a rule looks like and how to run it yourself.

---

## 4. Write and Run a Rule by Hand

You don't need an agent — you can author `.cop` files directly. cop analyzes the `.py` files
you already have; a typical Python project looks like this:

```
src/
  mypackage/
    __init__.py
    models.py
    services.py
pyproject.toml
```

Create a file called `checks.cop` in your project root:

```cop
import python
import code
import code

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

Verify it, then run it from your project root. By default cop analyzes the current directory;
`-t <path>` narrows analysis to a subfolder (here `src/`):

```bash
cop verify checks.cop      # catch syntax/type errors first
cop checks.cop -t src/
```

Example output:

```
src/mypackage/models.py: warning: Public class Order is missing a docstring
src/mypackage/services.py: warning: Bare except at line 42 — catch a specific exception

2 violation(s) found.
```

Exit code is `0` when clean and `1` when violations are found — suitable for CI. To organize
many rules, put one check per file in a `cop-checks/` folder with a `main.cop` entry point and
run `cop cop-checks/main.cop -t .` (this is exactly what your agent does for you).

---

## 5. Use Built-In Checks

Cop ships with comprehensive Python check packages — no `.cop` files needed:

```bash
cop run python-checks                      # all Python conventions
cop run python-checks -c no-print          # just the "no print" check
cop run python-library-checks              # library API design rules
cop run python-library-azure-checks        # Azure SDK conventions
```

---

## 6. Enforce Package Layering

Cop discovers your Python packages/distributions and their dependencies (from each
`pyproject.toml` or `setup.py`). The language-agnostic **`code-layering`** package lets
you enforce architectural rules across distributions — for example, that core packages
must not depend on higher-level service packages.

Create `layering.cop`:

```cop
import python
import code
import code

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

## 7. Explore Further

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
import code

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
import code

let cb = python.parse()

predicate hasNoReturnType(Method) => Method.ReturnType == nic && Method:isPublic

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

### Syntax-error reporting

`python.parse()` uses a real lexer + recursive-descent parser (not a line scanner). When a `.py`
file contains a syntax error — an unterminated string, a malformed `def`/`class` header, or
unbalanced `()`/`[]`/`{}` — cop surfaces it as a **warning** of the form
`path(line,col): error: message` and still analyzes the rest of that file and every other file.
Malformed sources are reported, never silently skipped.

---

## Tips

- Use `cop verify checks.cop` to check your rule for errors before running
- Use `-t path/` to limit analysis to a specific directory
- Run `cop help python-checks` to see all built-in Python checks
- Run `cop help code` to see available types and predicates
- Combine with other providers: `import python` + `import javascript` for polyglot projects
