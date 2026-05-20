# Static Analysis with Agent Cop

Agent Cop is a language independent static alanysis system that can be used to check source code for bugs, convention violations, and design issues. Check packages define rules as predicates over a language-neutral data model (types, statements, lines, files), so you get consistent analysis across C#, Python, JavaScript, and other languages without writing language-specific tooling.

You start by running pre-built check packages against your repo. As your team's conventions become clearer, you exclude rules that don't apply, add project-specific checks, and eventually ask coding agents to encode new rules as they work — turning one-off code review feedback into permanent, automated enforcement.

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
predicate hardcodedUrl(Statement) => Statement.Kind == 'declaration'
    && Statement.Source:contains('http://')
## Do not hardcode URLs — use configuration
export let no-hardcoded-urls = Statements:hardcodedUrl
    :toWarning('Do not hardcode URLs — use configuration')

let all-checks = my-checks + no-hardcoded-urls

function MAIN() = { CHECK(all-checks) }
```

---

## Self-Checks with a Coding Agent

When a coding agent (Copilot, etc.) makes a change you don't like, you can ask it to create a self-check that prevents the pattern in the future.

### The Workflow

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

Once self-checks live in your `cop.cop`, they run in CI like any other check:

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

## Further Reading

- [Language Reference](language-reference.md) — full DSL syntax
- [Code Package Reference](packages/code.md) — Type, Statement, Line, File properties
- [Testing with Cop](testing-with-cop.md) — ASSERT and test mode
