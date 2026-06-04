# Static Analysis with Agent Cop

Agent Cop is designed for coding agents to write and maintain custom static analysis rules. You describe conventions in plain English, the agent writes `.cop` rules, and those rules run deterministically in CI — catching violations before they reach code review.

The workflow has three phases: start with built-in check packages, exclude what doesn't apply, then ask your coding agent to encode project-specific conventions as custom rules.

## Setting Up for Agents

Before asking an agent to write rules, set up agent context:

```bash
cop init
```

This generates instruction files that agents auto-discover:
- `.github/copilot-instructions.md` — for GitHub Copilot
- `AGENTS.md` — for Claude Code

The files teach the agent the Cop language and point it to help commands:

```bash
cop help language        # Full language reference (syntax, types, operators)
cop help <package>       # Package documentation (types, functions, examples)
cop package list         # List all available packages
```

Commit these files so every contributor's agent has context automatically.

## Running Checks

Run `cop` in your repository with one or more check packages:

```bash
cop csharp-checks                        # C# naming, style, FDG, error handling
cop python-checks                        # Python conventions
cop csharp-checks javascript-checks      # multiple languages at once
cop csharp-checks -t src/                # analyze a specific directory
cop csharp-checks -c interface-prefix    # run only specific checks
```

Packages are auto-downloaded from the default feed on first use. No setup required.

Output uses the standard `file(line): severity: message` format:

```
src/Services/BlobService.cs(42): error: Do not use 'var' for client
src/Services/BlobService.cs(88): warning: Do not swallow Exception — rethrow or catch specific type
src/Utils/Helpers.cs(15): warning: SA1633: File should begin with a header comment
```

### Discovering What a Package Checks

Use `cop package commands` to see every check in a package:

```bash
cop package commands csharp-checks
```

```
csharp-checks — C# checks: correctness, style, and Framework Design Guidelines

Checks:
  var-declarations                     Disallow implicit typing with var — use explicit types
  dynamic-declarations                 Disallow dynamic typing
  thread-sleep-calls                   Use Task.Delay instead of blocking Thread.Sleep
  configure-await-true-calls           ConfigureAwait(true) is the default...
  ...

Groups:
  csharp-correctness-checks            Combined correctness checks
  csharp-style-checks                  Combined style checks
  fdg-checks                           Combined FDG checks
  csharp-checks                        All C# checks combined
```

### Available Packages

To see all packages available in your configured feeds:

```bash
cop package list
```

Some commonly used check packages:

| Package | What it checks |
|---|---|
| `csharp-checks` | C# conventions: naming, formatting, documentation, error handling, FDG |
| `csharp-library-checks` | Library API design: sealed clients, method conventions, patterns |
| `csharp-library-azure-checks` | Azure SDK for .NET conventions |
| `python-checks` | Python conventions: print, bare except, eval, naming, docstrings |
| `python-library-checks` | Python library patterns: naming, kwargs, LRO, paging |
| `python-library-azure-checks` | Azure SDK for Python conventions |
| `javascript-checks` | JS/TS conventions: console, eval, var, debugger |
| `javascript-library-checks` | JS/TS library patterns: verbs, cancellation, pagination |
| `javascript-library-azure-checks` | Azure SDK for JS conventions |

---

## Excluding Checks and Violations

When a package flags something you don't care about, create a `.cop` file in your repo root to exclude it.

### Excluding Entire Checks

Create `cop.cop` in your repository:

```ruby
import csharp-checks
import code-analysis

# We allow var — remove that check entirely
let my-checks = csharp-checks - var-declarations

function MAIN() = { CHECK(my-checks) }
```

Now `cop cop.cop` will skip the `var-declarations` check. You can subtract multiple:

```ruby
import csharp-checks
import code-analysis

# Our style: allow var, allow tabs, don't require file headers
let my-checks = csharp-checks - var-declarations - no-tabs - file-header-required

function MAIN() = { CHECK(my-checks) }
```

### Excluding a Group of Checks

Packages organize checks into groups. You can redefine the collection to include only the groups you want:

```ruby
import csharp-checks
import code-analysis

# Only run correctness and FDG checks — skip all style checks
let my-checks = csharp-correctness-checks + fdg-checks

function MAIN() = { CHECK(my-checks) }
```

### Excluding Individual Violations by Path

Use inline path filters to exclude specific directories from a check:

```ruby
import csharp-checks

# Don't flag var usage in test code
predicate isTestFile(Statement) => Statement.File.Path:contains('/test/')
let var-declarations = Statements:isVarDeclaration:!isTestFile
    :toError('Do not use \'var\'')
```

### Zero-Arg Invocation with Config

Once you have a `cop.cop` file that imports packages and defines `MAIN()`, you can just run:

```bash
cop cop.cop
```

Example `cop.cop` that customizes checks and provides a `MAIN` function:

```ruby
import csharp-checks
import code-analysis

# Our style: allow var, skip style checks
let my-checks = csharp-correctness-checks + fdg-checks - var-declarations

function MAIN() = { CHECK(my-checks) }
```

---

## Adding Your Own Checks

Beyond excluding built-in checks, you can add project-specific rules. Add them to your `cop.cop` file or create separate `.cop` files in the same directory.

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

### The Data Model

Cop parses every source file and provides four collections:

| Collection | Item Type | What it contains |
|---|---|---|
| `Code.Types` | `Type` | Classes, structs, interfaces, enums |
| `Code.Statements` | `Statement` | Calls, declarations, error handlers |
| `Code.Lines` | `Line` | Raw source lines (text + metadata) |
| `Code.Files` | `File` | Source files |

When you import a language package (e.g., `import csharp`), its `Statements`, `Types`, `Lines` collections are pre-filtered to that language.

### Severity Functions

| Function | When to use |
|---|---|
| `:toError('msg')` | Bug or correctness issue — must fix |
| `:toWarning('msg')` | Convention violation — should fix |
| `:toInfo('msg')` | Informational — consider fixing |

### Combining Checks

Group your checks with `+` and export the group:

```ruby
import csharp
import code-analysis

predicate usesDateTime(Statement) => Statement.Kind == 'call'
    && Statement.TypeName == 'DateTime' && Statement.MemberName == 'Now'
predicate usesThread(Statement) => Statement.Kind == 'call'
    && Statement.TypeName == 'Thread' && Statement.MemberName == 'Sleep'

export let datetime-now = Statements:usesDateTime
    :toError('Use DateTimeOffset.UtcNow instead of DateTime.Now')
export let thread-sleep = Statements:usesThread
    :toError('Use Task.Delay instead of Thread.Sleep')

export let my-project-checks = datetime-now + thread-sleep
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

function MAIN() = { CHECK(all-checks) }
```

---

## Writing Rules with a Coding Agent

Once `cop init` is set up (see [Setting Up for Agents](#setting-up-for-agents) above), you can ask the agent to write rules directly:

> "Write a cop rule that flags any method longer than 50 statements"

> "Create a cop check that ensures all public types in src/ have XML documentation"

> "Add a cop rule that no file in src/Core/ imports from src/Infrastructure/"

The agent reads the instruction files, runs `cop help language` and `cop help <package>` as needed, and produces working `.cop` code.

### The Self-Check Loop

When a coding agent makes a change you don't like, ask it to encode that feedback as a permanent rule:

1. Agent produces code with a pattern you dislike (e.g., uses `DateTime.Now`)
2. You say: **"Add a self-check that flags DateTime.Now — we use DateTimeOffset.UtcNow here"**
3. Agent adds a check to your `cop.cop` file:

```ruby
import csharp
import code-analysis

predicate usesDateTimeNow(Statement) => Statement.Kind == 'call'
    && Statement.TypeName == 'DateTime' && Statement.MemberName == 'Now'

## Use DateTimeOffset.UtcNow instead of DateTime.Now
export let datetime-now = Statements:usesDateTimeNow
    :toError('Use DateTimeOffset.UtcNow instead of DateTime.Now')

function MAIN() = { CHECK(datetime-now) }
```

4. From now on, `cop` catches this pattern before it reaches code review.

### What Makes a Good Self-Check Prompt

Tell the agent:
- **What** the pattern is (specific type/method/keyword)
- **Why** it's wrong (so the message is helpful)
- **What** to do instead (so the fix is clear)

Examples:

> "Add a self-check that we never use `Console.WriteLine` in the `src/` directory — we use our Logger class"

> "Add a self-check that all public methods in `*Client` classes must be async"

> "Add a self-check that no file in `src/Core/` imports from `src/Infrastructure/` — we enforce layering"

### Self-Check as CI Gate

Once checks live in your `cop-checks/` folder, they run in CI like any other check:

```yaml
- name: Run cop checks
  run: cop csharp-checks
  # Exits 1 if violations found — blocks merge
```

The coding agent's own changes are now checked against the rules before the PR merges. The repo's conventions are enforced automatically, not just through review comments.

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
  run: cop csharp-checks
```

### Azure DevOps

```yaml
- script: cop csharp-checks
  displayName: 'Run cop checks'
  failOnStderr: false
```

---

## Running External Analyzers

Cop can run external analysis tools (like Ruff, ESLint, or any tool that produces structured output) and present their results in the same unified format as native checks. This lets you combine results from multiple tools into a single report, filter them with `.cop` predicates, or compose them with your own custom rules.

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
cop analysis-semgrep -t path/to/project      # Security scan
cop analysis-trivy -t path/to/project        # Vulnerability scan
```

All produce the same unified output format:

```
src/app.py(1): warning: F401: `os` imported but unused
src/app.py(9): error: E711: Comparison to `None` should be `cond is None`
tests/test_main.py(15): warning: F841: Local variable `result` is assigned to but never used
```

### Combining External and Native Checks

You can import external analyzer results into a `.cop` file and combine them with native checks or your own rules:

```ruby
import python-ruff
import python-checks
import code-analysis

# Combine ruff findings with native cop checks
let all-checks = checks + python-checks

command MAIN = CHECK(all-checks)
```

Or filter the external results:

```ruby
import python-ruff

# Only show errors (not warnings)
predicate isError(item) => item.Severity == 'error'

command MAIN = foreach diagnostics():isError
    => '{item.FilePath}({item.Line}): {item.RuleId}: {item.Message}'
```

### Performance

External analyzer packages delegate the heavy work to purpose-built tools. For example, `python-ruff` runs Ruff (written in Rust) under the hood — it processes the entire Azure SDK for Python repo (55,000+ files, 52,000+ findings) in about 20 seconds.

---

## Further Reading

- [Language Reference](language-reference.md) — full DSL syntax
- [Code Package Reference](packages/code.md) — Type, Statement, Line, File properties
- [Testing with Cop](testing-with-cop.md) — ASSERT and test mode
- [Extensibility](extensibility.md) — adding providers and external analyzers
