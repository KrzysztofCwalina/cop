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

### Python toolchain and fallback

Cop automatically discovers and probes an installed Python 3 interpreter. The richer
analysis uses Python's standard-library `ast` module to read `.py` and `.pyi` files;
no pip packages, project environment activation, or Cop-specific setup are required.
The installed interpreter determines which Python syntax the native parser accepts.

The helper runs in isolation without importing or executing the analyzed project. It
provides parsed declarations, signatures, defaults, imports, source spans, and nested
calls, not runtime evaluation or a Python type checker's whole-program type inference.
A bounded helper process is reused as files are requested during one Cop execution,
then stopped; Cop does not start a fresh interpreter for every file.

If no usable interpreter is available, Cop emits a **yellow warning** and continues with
its built-in, simpler syntax parser. Its coverage is more limited, so a clean fallback
run is not evidence that every native-parser fact was available. Warnings are plain text
when redirected or when `--no-color` is used, and are repeated on a later execution even
when fallback results come from cache. Cop never downloads Python or installs dependencies.

Malformed source or a failure after native analysis has started is reported as an error,
not silently retried as successful fallback analysis. To use the richer path, make a
working Python 3 interpreter available to the process running Cop and rerun the check.

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

Phrase the request as a cop check, so the agent reaches for a permanent rule instead of a
one-off edit:

> "Write a cop rule that flags bare `except:` clauses — we catch specific exceptions"

> "Add a cop rule that bans `print()` — use the logging module"

> "Create a cop rule that every public class in `src/` has a docstring"

> "Add a cop rule that forbids `eval()` anywhere in the codebase"

It works as a tight feedback loop: the moment your agent writes code in a shape you don't
like, ask it to add a cop check — `cop` then catches that pattern on every future run, before
it reaches code review. The next sections show what such a rule looks like and how to run it
yourself.

---

## 4. Write and Run a Rule by Hand

You don't need an agent — you can author `.cop` files directly. cop analyzes the `.py` and `.pyi` files
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
cop run python-library-checks              # library API design rules
cop run python-library-azure-checks        # Azure SDK conventions
```

### Excluding checks and violations

You won't want every rule on every project. There are two ways to opt out.

Each violation in the output ends with the **name of the check** that produced it, in brackets — e.g.
`app.py(3): warning: Avoid print() — use logging instead [print-calls]`. That bracketed name is
exactly the identifier you subtract.

**Exclude a whole rule** — take that bracketed name and subtract one or more checks (or whole groups) from the package in
a small `.cop` file. The `-` operator removes those violations; everything else still runs:

```cop
import python-checks
import code

# run every python-checks rule except print-calls and trailing-whitespace
let my-checks = python-checks - print-calls - trailing-whitespace

command MAIN = CHECK(my-checks)
```

```bash
cop my-checks.cop -t .
```

Checks are also grouped, so you can compose just the groups you want with `+`
(`python-correctness-checks`, `python-style-checks`, `python-idiom-checks`,
`python-quality-checks`, `python-doc-checks`):

```cop
import python-checks
import code

# only correctness and idioms — skip style, quality, and docs
let my-checks = python-correctness-checks + python-idiom-checks

command MAIN = CHECK(my-checks)
```

**Exclude a single violation** — add a `# cop-ignore: <check>` comment on the line directly
above the one to exempt. Only that line is silenced; the rule keeps firing everywhere else:

```python
def save():
    # cop-ignore: print-calls
    print("debug")   # exempted — NOT flagged
    print("more")    # still flagged
```

`cop-ignore` works for the statement- and line-level checks (print/eval calls, bare/empty
except, imports, whitespace, idioms). Type- and method-level checks (naming, docstrings,
mutable defaults, parameter counts) have no per-line anchor — exclude those with the
whole-rule approach above.

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
| `cb.Methods` | Module functions, nested functions, class methods and constructors |
| `cb.Statements` | Calls, assignments, control flow, returns, yields and exception handling |
| `cb.Calls` | Every statically present invocation, including nested calls and calls in arguments, defaults and decorators |
| `cb.Files` | Source files with import modules in `Usings` and parsed alias/member/relative facts in `Imports` |
| `cb.Lines` | Every line of code (with kind: code/comment/blank) |
| `cb.Projects` | pyproject.toml / setup.py projects with dependencies |

### Python-Specific Features

- **Docstring detection**: Triple-quoted docstrings are detected as documentation
- **Decorators**: `@staticmethod`, `@abstractmethod`, etc. are captured in `Decorators`
- **Exception handling**: `except` clauses are parsed as error handler statements with type info
- **Async**: `async def` functions are flagged with the `Async` modifier
- **Project discovery**: Parses `pyproject.toml` and `setup.py` for dependencies
- **Class members**: Class fields, properties (including setters) and nested classes are available
- **Parameters**: Explicit receiver parameters (`self`/`cls`), variadics, annotations, defaults and source locations are retained
- **Structured calls**: `CallTarget` and `ArgumentExpressions` expose parsed identifiers,
  member access, invocations and decoded string literals. Python keyword arguments retain
  their names; splats remain explicit rather than pretending their runtime values are known.
- **Statement ownership**: `Method`, `Parent` and `Children` describe lexical ownership and
  control nesting. A nested function's `yield` does not make its enclosing function a generator.

### Syntax-error reporting

When Python 3.8 or newer is installed, `python.parse()` uses its standard-library `ast`
parser. No packages, pip installs or additional setup are required. The interpreter is
discovered from `python`, `python3`, or the Windows `py -3` launcher using bounded probes,
including protection against unusable Microsoft Store aliases. Source is parsed only:
target modules, decorators and `setup.py` scripts are never executed or imported.

The installed interpreter's syntax version applies. Its genuine syntax errors are surfaced
as warnings of the form `path(line,col): error: message`. The malformed file has no invented
partial declarations; valid files continue to be analyzed. Timeouts, helper failures and
output-limit failures are surfaced explicitly, not disguised as successful fallback results.

When no usable interpreter is available, cop prints a **yellow degraded-analysis warning**
and uses its simpler lexer/recursive-descent parser. That fallback retains basic classes,
functions and call sites, reports basic malformed brackets/headers, and terminates on
class-level compound statements. It does not provide the full AST-backed structure. Native
and simpler results use different cache identities, and the warning is replayed on cache hits.

Within one analysis, a bounded helper is reused across demanded files and disposed when the
analysis ends. Lazy queries do not read later files just to batch them into a Python process.

Project discovery includes nested projects, literal `setup(...)` metadata, required and
optional PEP 621 dependencies, and Poetry dependencies. `pyproject.toml` uses `tomllib` on
Python 3.11 or newer; older interpreters use the explicitly warned simpler metadata reader.
Dynamic setup metadata is not executed and is reported as unavailable. Discovery honors
excluded directories and does not descend into directory links.

Python is dynamic: these are source facts, not whole-program type inference. Runtime
monkey-patching, dynamically generated declarations, inferred argument types, overload
resolution and transitive class/interface resolution remain unknown. Python-specific
decorator/base-class flags describe the written syntax and naming conventions.

---

## Tips

- Use `cop verify checks.cop` to check your rule for errors before running
- Use `-t path/` to limit analysis to a specific directory
- Run `cop help python-checks` to see all built-in Python checks
- Run `cop help code` to see available types and predicates
- Combine with other providers: `import python` + `import javascript` for polyglot projects
