# CLI Reference

Complete reference for `cop.exe`.

```bash
cop <program> [options]
```

Options:

| Option | Description |
|--------|-------------|
| `-t <dir>` | Target directory |
| `-c <commands>` | Filter to specific named functions (comma-separated) |
| `-f <format>` | Output format: `text` (default) or `json` |
| `-d` | Diagnostic mode (timing, traces, debug output to stderr) |
| `-cql` | Transpile to CodeQL instead of running |
| `-h` | Show help |
| `-v` | Show version |

> **Note:** Agent Cop uses short flags only — there are no long-form `--help` or `--version` equivalents.

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Clean run — no output, no violations, all tests passed |
| `1` | Output produced — violations found, tests failed, or items printed |
| `2` | Fatal error — parse error, missing file, or invalid arguments |

Use exit codes in CI pipelines:

```bash
cop checks.cop || exit 1
```

## cop \<program\>

Run a program. The argument can be a `.cop` file, a URL, a local UPPERCASE function name, or a package name from a feed.

```bash
cop <program> [-t <target>] [-c <commands>] [-f text|json] [-d] [-cql]
```

### Resolution order

1. If the argument ends in `.cop` → run that local file
2. If the argument is an HTTPS URL → download and run
3. If local `.cop` files define an UPPERCASE function with that name → run it
4. Otherwise → treat as a package name, auto-restore from feed, and run

### Examples

Run all `.cop` files in the current directory:

```bash
cop
```

Run a specific file:

```bash
cop checks.cop
```

Run a package from a feed:

```bash
cop csharp-checks
```

Run multiple packages:

```bash
cop csharp-checks csharp-library-checks
```

Run a named UPPERCASE function defined in local `.cop` files:

```bash
cop MY-COMMAND
```

Run multiple named UPPERCASE functions:

```bash
cop -c LINT,FORMAT
```

Target a specific directory:

```bash
cop checks.cop -t src/
```

Target specific files:

```bash
cop checks.cop -t Program.cs,Startup.cs
```

Run a remote `.cop` file from a URL:

```bash
cop https://raw.githubusercontent.com/owner/repo/main/checks.cop
```

Output as JSON:

```bash
cop checks.cop -f json
```

Show diagnostics:

```bash
cop checks.cop -d
```

### CodeQL generation (`-cql`)

Generate standalone CodeQL `.ql` query files from cop checks. This transpiles your `.cop` predicates and filter chains into equivalent CodeQL `from`/`where`/`select` queries. Generated files are written to a `codeql/` subdirectory next to the source `.cop` file.

```bash
cop checks.cop -cql
```

Only Code provider collections are supported (`Code.Types`, `Code.Statements`, `Code.Calls`). If a predicate uses constructs that cannot be fully expressed in CodeQL, the transpiler reports an error and produces no output for that check.

Example — given this cop check:

```ruby
import code

predicate isGodClass(Type) => Type.Name:endsWith('Manager') && Modifiers:isSet(Public)

export let god-classes = Code.Types:isCSharp:isGodClass:toWarning('Avoid God classes')
```

Running `cop my-checks.cop -cql` generates `codeql/god_classes.ql`:

```ql
/**
 * @name god-classes
 * @description god-classes
 * @kind problem
 * @problem.severity warning
 * @id cop/god_classes
 */

import csharp

from RefType t
where t.getName().toLowerCase().matches("%manager")
  and t.isPublic()
select t, "Avoid God classes"
```

**Supported patterns:**

| Cop construct | CodeQL equivalent |
|---|---|
| `Code.Types` collection | `from RefType t` (or narrowed by Kind) |
| `Code.Statements` with Kind filter | `from MethodAccess m`, `from ThrowStmt s`, etc. |
| `Modifiers:isSet(Public)` | `t.isPublic()` |
| `Name:startsWith('X')` | `t.getName().toLowerCase().matches("x%")` |
| `Name:endsWith('Y')` | `t.getName().toLowerCase().matches("%y")` |
| `Kind == 'call'` | `from MethodAccess` (type narrowing) |
| `BaseTypes:contains('IFoo')` | `exists(RefType base \| t.getABaseType() = base and ...)` |
| Language filter `:isCSharp` | `import csharp` |
| `toError` / `toWarning` | `@problem.severity error` / `warning` |

**Limitations:** Cross-provider checks (e.g., checks referencing both `Code.Types` and markdown documentation) cannot be expressed in CodeQL and will produce an error.

## cop test

Run `test` declarations in `.cop` files and report pass/fail results.

```bash
cop test [<file>] [-d]
```

| Argument / Option | Description |
|-------------------|-------------|
| `<file>` | `.cop` file or directory to test. When omitted, all `.cop` files in the current directory are used. |
| `-d` | Enable diagnostic mode (timing, traces, and DEBUG output) to stderr |

### Examples

Run all tests in the current directory:

```bash
cop test
```

Run tests in a specific file:

```bash
cop test tests/test-checks.cop
```

Run all tests in a directory:

```bash
cop test tests/cop/
```

Sample output:

```
  ✓ test-has-types
  ✓ test-public-types
  ✗ test-no-sleep: Thread.Sleep should not be used (found 2 items)

  3 tests, 2 passed, 1 failed
```

See [Testing with Agent Cop](testing-with-cop.md) for a full guide on writing tests.

## cop help

Show help for the Cop language, a package, or list commands in a `.cop` file.

```bash
cop help language         # Full language reference
cop help <package>        # Package documentation (types, functions, samples)
cop help [<file>]         # List commands defined in a .cop program
```

| Argument | Description |
|----------|-------------|
| `language` | Print the full Cop language reference (syntax, types, operators, patterns) |
| `<package>` | Print documentation for a package — exports, types, predicates, functions, and samples |
| `<file>` | `.cop` file to inspect for commands. When omitted, scans current directory. |

### Examples

Print the full language reference:

```bash
cop help language
```

Show documentation for the `code` package:

```bash
cop help code
```

Show documentation for the `code-analysis` package:

```bash
cop help code-analysis
```

List all named entry-point functions in the current directory:

```bash
cop help
```

List named entry-point functions in a specific file:

```bash
cop help checks.cop
```

### Package help resolution

`cop help <package>` looks for the package in this order:
1. `.cop/packages/<name>/` — locally restored packages
2. `packages/<name>/` — package repo layout
3. Parent directories containing `packages/<name>/`

If the package is not found locally, run `cop package restore` first.

## cop init

Generate agent instruction files for coding agents (GitHub Copilot, Claude Code) so they can write cop rules in your project.

```bash
cop init
```

Creates:
- `.github/copilot-instructions.md` — discovered automatically by GitHub Copilot
- `AGENTS.md` — discovered automatically by Claude Code

The files contain a concise cop language overview, common patterns, and instructions pointing agents to `cop help language` and `cop help <package>` for full reference.

Skips files that already exist. Run once per project, then commit the generated files.

### Example

```bash
cd my-project
cop init
# Created: .github/copilot-instructions.md
# Created: AGENTS.md
# 2 file(s) created. Agents will now discover cop language context automatically.
```

## cop lock

Lock files for tamper protection. Locked files are checksummed so modifications can be detected.

```bash
cop lock <files>
```

| Argument | Description |
|----------|-------------|
| `<files>` | One or more file paths to lock |

### Examples

Lock a single file:

```bash
cop lock checks.cop
```

Lock multiple files:

```bash
cop lock checks.cop rules.cop
```

## cop unlock

Unlock previously locked files. With no arguments, unlocks all locked files.

```bash
cop unlock [<files>]
```

| Argument | Description |
|----------|-------------|
| `<files>` | File paths to unlock. When omitted, all locked files are unlocked. |

### Examples

Unlock a specific file:

```bash
cop unlock checks.cop
```

Unlock all locked files:

```bash
cop unlock
```

## cop syntax

Validate `.cop` file syntax without executing.

```bash
cop syntax <path>
```

| Argument | Description |
|----------|-------------|
| `<path>` | `.cop` file or directory to validate |

```bash
cop syntax checks.cop
cop syntax src/
```

## cop repl

Launch an interactive REPL session. Loads `.cop` files from the current directory.

```bash
cop repl
```

See [Working with the REPL](working-with-repl.md) for a full walkthrough.

## cop package

Manage cop packages — browse, restore, scaffold, validate, publish, and search.

```bash
cop package <subcommand>
```

### cop package list

Browse all available packages across configured feeds.

```bash
cop package list
```

### cop package commands

Show the named entry-point functions and exports of a package.

```bash
cop package commands <package>
```

```bash
cop package commands csharp-checks
```

### cop package restore

Restore packages declared in a `.cop` file. Downloads packages from GitHub feeds, resolves transitive dependencies, and places files under the `.cop/` directory in the project root (e.g., `.cop/packages/`, `.cop/checks/`).

```bash
cop package restore [<file>]
```

| Argument | Description |
|----------|-------------|
| `<file>` | `.cop` file whose package declarations to restore. When omitted, all `.cop` files in the current directory are used. |

The `.cop` file must declare at least one GitHub feed (`feed 'github.com/owner/repo'`) and one or more `import` statements. The restore command reads these declarations, downloads the packages, and resolves dependencies transitively.

```bash
cop package restore
cop package restore checks.cop
```

Set `GITHUB_TOKEN` environment variable for private repos or to avoid rate limits.

### cop package new

Scaffold a new package directory with the standard structure.

```bash
cop package new <name>
```

| Argument | Description |
|----------|-------------|
| `<name>` | Name for the new package |

```bash
cop package new my-rules
```

### cop package validate

Validate a package's structure and metadata.

```bash
cop package validate <name>
```

| Argument | Description |
|----------|-------------|
| `<name>` | Package name or path to validate |

```bash
cop package validate my-rules
```

### cop package publish

Validate and publish a package version to a feed.

```bash
cop package publish <name>
```

| Argument | Description |
|----------|-------------|
| `<name>` | Package name or path to publish |

```bash
cop package publish my-rules
```

### cop package search

Search for packages across configured feeds.

```bash
cop package search <query>
```

| Argument | Description |
|----------|-------------|
| `<query>` | Search term to match against package names and descriptions |

```bash
cop package search csharp
cop package search 'naming conventions'
```

### cop package feed

Manage package feeds — add, remove, and list configured feed sources.

```bash
cop package feed <action>
```

```bash
cop package feed list
cop package feed add <url>
cop package feed remove <url>
```
