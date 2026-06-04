# Static Analysis with Agent Cop

Agent Cop ships with built-in check packages that enforce conventions out of the box. You run them with a single command, inspect what they check, exclude what doesn't apply, and optionally add your own project-specific rules.

---

## Running Built-In Checks

Run `cop` with one or more check packages:

```bash
cop csharp-checks                        # C# naming, style, FDG, error handling
cop python-checks                        # Python conventions
cop javascript-checks                    # JS/TS conventions
cop csharp-checks javascript-checks      # multiple languages at once
cop csharp-checks -t src/                # analyze a specific directory
cop csharp-checks -c interface-prefix    # run only a specific check
```

Packages are auto-downloaded from the default feed on first use. No setup required.

Output uses the standard `file(line): severity: message` format:

```
src/Services/BlobService.cs(42): error: Do not use 'var' for client
src/Services/BlobService.cs(88): warning: Do not swallow Exception — rethrow or catch specific type
src/Utils/Helpers.cs(15): warning: SA1633: File should begin with a header comment
```

Exit code 1 if violations found, 0 if clean — suitable for CI.

---

## Inspecting What a Package Checks

Use `cop package commands` to see every check in a package:

```bash
cop package commands csharp-checks
```

```
csharp-checks — C# checks: correctness, style, and Framework Design Guidelines

Lets:
  var-declarations                     Disallow implicit typing with var — use explicit types
  dynamic-declarations                 Disallow dynamic typing
  thread-sleep-calls                   Use Task.Delay instead of blocking Thread.Sleep
  ...

Groups:
  csharp-correctness-checks            Combined correctness checks
  csharp-style-checks                  Combined style checks
  fdg-checks                           Combined FDG checks
  csharp-checks                        All C# checks combined
```

Each "Let" is an individual check you can include or exclude by name. Groups combine multiple checks and can also be used for selective runs.

To see all packages available in your configured feeds:

```bash
cop package list
```

---

## Available Check Packages

### Language Checks (general conventions)

| Package | What it checks |
|---|---|
| `csharp-checks` | C# conventions: naming, formatting, documentation, error handling, FDG |
| `python-checks` | Python conventions: print, bare except, eval, naming, docstrings, idioms |
| `javascript-checks` | JS/TS conventions: console, eval, var, debugger, swallowed exceptions |

### Library Design Checks

| Package | What it checks |
|---|---|
| `csharp-library-checks` | C# library API design: sealed clients, async patterns, cancellation |
| `python-library-checks` | Python library patterns: naming, kwargs, LRO, paging |
| `javascript-library-checks` | JS/TS library patterns: verbs, cancellation, pagination |

### Azure SDK Checks

| Package | What it checks |
|---|---|
| `csharp-library-azure-checks` | Azure SDK for .NET conventions |
| `python-library-azure-checks` | Azure SDK for Python conventions |
| `javascript-library-azure-checks` | Azure SDK for JS conventions |

### Snippet and Documentation Checks

| Package | What it checks |
|---|---|
| `csharp-snippets-checks` | C# snippet/docs sync: missing docs, orphaned refs, stale content |
| `javascript-snippets-checks` | JS/TS snippet/docs sync |
| `python-snippets-checks` | Python snippet/docs sync |

### Test Checks

| Package | What it checks |
|---|---|
| `test-nunit-checks` | NUnit testing patterns and conventions |

---

## Excluding Checks and Violations

When a package flags something you don't care about, create a `.cop` file in your repo to customize which checks run.

### Excluding Individual Checks

```ruby
import csharp-checks
import code-analysis

# We allow var — remove that check entirely
let my-checks = csharp-checks - var-declarations

command MAIN = CHECK(my-checks)
```

Run with `cop my-checks.cop`. The `-` operator subtracts checks from a set. You can subtract multiple:

```ruby
# Our style: allow var, allow tabs, don't require file headers
let my-checks = csharp-checks - var-declarations - no-tabs - file-header-required

command MAIN = CHECK(my-checks)
```

### Running Only Specific Groups

Packages organize checks into groups. You can include only the groups you want:

```ruby
import csharp-checks
import code-analysis

# Only run correctness and FDG checks — skip all style checks
let my-checks = csharp-correctness-checks + fdg-checks

command MAIN = CHECK(my-checks)
```

### Excluding by Path

Use inline path filters to exclude specific directories from a check:

```ruby
import csharp-checks
import code-analysis

# Don't flag var usage in test code
predicate isTestFile(Statement) => Statement.File.Path:contains('/test/')
let my-var-check = Statements:isVarDeclaration:!isTestFile
    :toError('Do not use \'var\'')
```

### Running a Subset of Checks via CLI

You can also filter checks from the command line without writing a `.cop` file:

```bash
cop csharp-checks -c interface-prefix,type-name-casing    # run only these checks
```

---

## Adding Your Own Checks

Beyond built-in checks, you can add project-specific rules. Create `.cop` files in a `cop-checks/` folder:

### Anatomy of a Check

A check is three things: a predicate (what to match), a collection (what to search), and a severity (how bad it is):

```ruby
import csharp
import code-analysis

# 1. Predicate: what pattern are we looking for?
predicate usesDateTime(Statement) => Statement.Kind == 'call'
    && Statement.TypeName == 'DateTime'
    && Statement.MemberName == 'Now'

# 2. Check: filter + severity + message
## Use DateTimeOffset.UtcNow instead of DateTime.Now
export let datetime-now = Statements:usesDateTime
    :toError('Use DateTimeOffset.UtcNow instead of DateTime.Now')
```

The `##` comment above the `export let` becomes the check's description (shown in `cop package commands`).

### Organizing Checks in a Folder

Place all checks in a `cop-checks/` folder with a `main.cop` entry point:

```
cop-checks/
  main.cop           # Composes all checks and runs them
  naming.cop         # One check per file
  layering.cop       # Dependency layering rules
  no-interfaces.cop  # "No new interfaces" check
```

Each file exports its violations:

```ruby
# naming.cop
import csharp
import code-analysis

predicate hasBadName(Type) => Type.Name:startsWith('_')

export let naming-violations = csharp.Types:hasBadName
    :toError('{item.Name} must not start with underscore')
```

And `main.cop` composes them:

```ruby
# main.cop
export let all-violations =
    naming-violations +
    layering-violations +
    no-new-interfaces

command MAIN = CHECK(all-violations)
```

Run with:

```bash
cop cop-checks/main.cop -t .
```

### Mixing Built-In and Custom Checks

```ruby
import csharp-checks
import code-analysis

# Start with all built-in checks, minus the ones we don't want
let my-checks = csharp-checks - var-declarations

# Add our own project-specific checks
predicate isHardcodedUrl(Statement) => Statement.Kind == 'declaration'
    && Statement.Source:contains('http://')
## Do not hardcode URLs — use configuration
export let no-hardcoded-urls = Statements:isHardcodedUrl
    :toWarning('Do not hardcode URLs — use configuration')

let all-checks = my-checks + no-hardcoded-urls

command MAIN = CHECK(all-checks)
```

### The Data Model

Cop parses every source file and provides these collections:

| Collection | Item Type | What it contains |
|---|---|---|
| `Code.Types` | `Type` | Classes, structs, interfaces, enums |
| `Code.Methods` | `Method` | Methods and functions |
| `Code.Statements` | `Statement` | Calls, declarations, error handlers |
| `Code.Lines` | `Line` | Raw source lines (text + metadata) |
| `Code.Files` | `File` | Source files |

When you import a language package (e.g., `import csharp`), its collections are pre-filtered to that language.

### Severity Functions

| Function | When to use |
|---|---|
| `:toError('msg')` | Bug or correctness issue — must fix |
| `:toWarning('msg')` | Convention violation — should fix |
| `:toInfo('msg')` | Informational — consider fixing |

---

## Writing Rules with a Coding Agent

Once `cop init` is set up (see README), you can ask your coding agent to write rules:

> "Write a cop rule that flags any method longer than 50 statements"

> "Create a cop check that ensures all public types in src/ have XML documentation"

> "Add a cop rule that no file in src/Core/ imports from src/Infrastructure/"

The agent reads the instruction files, runs `cop help language` and `cop help <package>` as needed, and produces working `.cop` code.

### The Self-Check Loop

When a coding agent makes a change you don't like, ask it to encode that feedback as a permanent rule:

1. Agent produces code with a pattern you dislike (e.g., uses `DateTime.Now`)
2. You say: **"Add a self-check that flags DateTime.Now — we use DateTimeOffset.UtcNow here"**
3. Agent adds a check to your `cop-checks/` folder
4. From now on, `cop` catches this pattern before it reaches code review

---

## Running External Analyzers

Cop can run external analysis tools and present their results in the same unified format as native checks.

### Available External Analyzer Packages

| Package | Tool | What it checks | Prerequisite |
|---------|------|----------------|--------------|
| `python-ruff` | [Ruff](https://docs.astral.sh/ruff/) | Python lint (800+ rules, fast) | `pip install ruff` |
| `python-mypy` | [mypy](https://mypy-lang.org/) | Python type errors | `pip install mypy` |
| `python-bandit` | [Bandit](https://bandit.readthedocs.io/) | Python security vulnerabilities | `pip install bandit` |
| `python-pylint` | [Pylint](https://pylint.readthedocs.io/) | Python conventions + bugs | `pip install pylint` |
| `javascript-eslint` | [ESLint](https://eslint.org/) | JS/TS lint | `npm install eslint` |
| `javascript-biome` | [Biome](https://biomejs.dev/) | JS/TS lint + format (fast) | `npm install @biomejs/biome` |
| `typescript-tsc` | TypeScript | Type errors | `npm install typescript` |
| `csharp-stylecop` | [StyleCop](https://github.com/DotNetAnalyzers/StyleCopAnalyzers) | C# style/naming | StyleCop NuGet + `dotnet` |
| `csharp-format` | dotnet format | C# formatting | `dotnet` SDK |
| `analysis-semgrep` | [Semgrep](https://semgrep.dev/) | Security patterns (any language) | `pip install semgrep` |
| `analysis-trivy` | [Trivy](https://trivy.dev/) | Vulnerabilities + misconfig | `trivy` binary |
| `analysis-checkov` | [Checkov](https://www.checkov.io/) | IaC security (Terraform, etc.) | `pip install checkov` |
| `analysis-spectral` | [Spectral](https://stoplight.io/spectral) | OpenAPI linting | `npm install @stoplight/spectral-cli` |

### Running an Analyzer

```bash
cop python-ruff -t path/to/project          # Python lint
cop python-mypy -t path/to/project          # Python type check
cop javascript-eslint -t path/to/project    # JS/TS lint
cop csharp-stylecop -t path/to/project      # C# style
cop analysis-semgrep -t path/to/project     # Security scan
```

All produce the same unified output format.

### Combining External and Native Checks

```ruby
import python-ruff
import python-checks
import code-analysis

# Combine ruff findings with native cop checks
let all-checks = ruff-checks + python-checks

command MAIN = CHECK(all-checks)
```

---

## CI Integration

### Exit Codes

| Code | Meaning |
|---|---|
| 0 | No violations found |
| 1 | Violations found |
| 2 | Configuration error (missing package, parse error) |

### GitHub Actions

```yaml
- name: Install cop
  run: |
    curl -sL https://github.com/.../cop-linux-x64.zip -o cop.zip
    unzip cop.zip -d /usr/local/bin

- name: Run checks
  run: cop cop-checks/main.cop -t .
```

### Azure DevOps

```yaml
- script: cop cop-checks/main.cop -t .
  displayName: 'Run cop checks'
  failOnStderr: false
```

---

## Further Reading

- [Language Reference](language-reference.md) — full DSL syntax
- [Testing Cop Rules](testing.md) — writing and running tests for your `.cop` programs
- [Extensibility](extensibility.md) — adding providers and external analyzers
- [Package Reference](https://krzysztofcwalina.github.io/cop/reference.html) — types, predicates, and checks
