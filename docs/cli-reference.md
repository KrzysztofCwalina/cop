# CLI Reference

Complete reference for `cop.exe`.

```bash
cop <program> [options]
```

Options:

| Option | Description |
|--------|-------------|
| `-t <dir>` | Target directory |
| `-p <provider>` | Load a data provider (can be repeated: `-p csharp -p python`) |
| `-c <commands>` | Filter to specific named functions (comma-separated) |
| `-f <format>` | Output format: `text` (default) or `json` |
| `-d` / `-dd` / `-ddd` | Diagnostics to stderr: `-d` summaries, `-dd` + phase/parse timing, `-ddd` + per-item trace |
| `--ai-log <path>` | Append a transcript of each `ai.judge` interaction to a file |
| `--no-color` | Disable ANSI color (also auto-disabled when output is not a terminal) |
| `--no-user-checks` | Skip personal checks in `~/.cop/checks/` for this run |
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

Load data providers to supply code collections:

```bash
cop csharp-checks -t src/ -p csharp
cop code-metrics -t . -p csharp -p python
cop csharp-checks python-checks -t . -p csharp -p python
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
| `Kind == call` | `from MethodAccess` (type narrowing) |
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
| `-d` / `-dd` / `-ddd` | Diagnostics to stderr (increasing verbosity: summaries, timing, per-item trace) |

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

Show help for the Cop language or a package.

```bash
cop help                  # General usage help
cop help language         # Full language reference
cop help <package>        # Package documentation (types, functions, samples)
```

| Argument | Description |
|----------|-------------|
| *(none)* | Print general usage help (same as `cop -h`) |
| `language` | Print the full Cop language reference (syntax, types, operators, patterns) |
| `<package>` | Print documentation for a package — exports, types, predicates, functions, and samples |

### Examples

Print the full language reference:

```bash
cop help language
```

Show documentation for the `code` package:

```bash
cop help code
```

### Package help resolution

`cop help <package>` looks for the package in this order:
1. `.cop/packages/<name>/` — locally restored packages
2. `packages/<name>/` — package repo layout
3. Parent directories containing `packages/<name>/`

If the package is not found locally, run `cop package restore` first.

## cop init

Generate agent integration files for coding agents (GitHub Copilot, Claude Code) so they can write and run cop rules in your project.

```bash
cop init [--al] [--ag] [--ch]
```

| Option | Description |
|--------|-------------|
| `--al` | Generate a local Claude Code hook (`.claude/settings.local.json`) |
| `--ag` | Generate a shared Claude Code hook (`.claude/settings.json`) |
| `--ch` | Generate a GitHub Copilot CLI hook (`.github/hooks/cop.json`) |

Always creates (merged in-place, never clobbering your own content):
- `.github/copilot-instructions.md` — cop language context, discovered automatically by GitHub Copilot
- `AGENTS.md` — cop language context, discovered automatically by Claude Code and other agents
- `.claude/commands/cop.md` — Claude Code custom command to run cop checks
- `.github/skills/cop/SKILL.md` — GitHub Copilot CLI agent skill (the Copilot analog of the Claude command) to run cop checks

The instruction files contain a concise cop language overview, common patterns, pointers to `cop help language` and `cop help <package>`, and guidance for reporting issues back to the cop project.

The optional hooks run `cop cop-checks/main.cop -t . -om` automatically after the agent finishes a turn (Claude's `Stop` event / Copilot CLI's `agentStop` event), surfacing any violations back to the agent. They are non-blocking — a failing run or pre-existing violations never trap the agent — and `-om` skips analysis when the git working tree is unmodified.

> GitHub Copilot CLI loads hook files at startup, so restart any running CLI session after `cop init --ch`.

The instruction and command/skill files are safe to run repeatedly; cop sections are updated in place. Commit the generated files so your whole team benefits.

### Example

```bash
cd my-project
cop init --ch
# Created: .github/copilot-instructions.md
# Updated: AGENTS.md
# Updated: .claude/commands/cop.md
# Updated: .github/skills/cop/SKILL.md
# Wrote: .../.github/hooks/cop.json
# 5 file(s) updated. Agents will now discover cop language context automatically.
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

## cop verify

Verify `.cop` program correctness without executing. Checks syntax, imports, name binding, types, and arity.

```bash
cop verify [<path>]
```

| Argument | Description |
|----------|-------------|
| `<path>` | `.cop` file or directory to verify. When omitted, verifies all `.cop` files in the current directory. |

```bash
cop verify checks.cop
cop verify src/
```

## cop repl

Launch an interactive REPL session. Loads `.cop` files from the current directory.

```bash
cop repl
```

See [Working with the REPL](repl.md) for a full walkthrough.

## cop update

Self-update cop to the latest release from GitHub. Detects your platform automatically and replaces the current binary.

```bash
cop update
```

### Updating

Cop does not update itself automatically. Run `cop update` to download and install the
latest release on demand (see [cop update](#cop-update)). This keeps runs predictable and
never overwrites a locally built/installed binary behind your back.

## User Checks

Personal `.cop` check files that run automatically alongside project checks. Place them in `~/.cop/checks/` and they'll be included in every cop run — no repo modification needed.

This is useful for:
- Personal coding rules you want across all your repos
- Augmenting team rules with your own checks
- Working on repos you don't own (no need to commit personal rules)

### Directory Layout

```
~/.cop/checks/
├── my-naming-rules.cop           # Global — runs on ALL repos
├── my-style.cop                  # Global
├── azure-sdk-for-net/            # Only when in azure-sdk-for-net repo
│   └── sdk-conventions.cop
└── my-project/                   # Only when in my-project repo
    └── extra-checks.cop
```

- **Top-level** `*.cop` files are global — they run on every repo.
- **Subdirectory** `*.cop` files are repo-specific — they only run when the current repo name matches the subdirectory name.

### Repo Name Matching

Cop determines the current repo name by:
1. Git remote origin URL (extracts the repo name, e.g., `github.com/org/my-repo` → `my-repo`)
2. Git root directory name (fallback)
3. Current working directory name (fallback)

### Example

Create a personal check that ensures all public C# types have XML doc comments:

```cop
# ~/.cop/checks/require-docs.cop
import csharp-checks
import code-analysis

predicate needsDocs(Type) => Type.IsPublic && Type.DocComment.Length == 0

export let missing-docs = csharp.Types:needsDocs
    :toWarning('{item.Name} in {item.File.Path} is public but has no doc comment')
```

Now `cop` will include this check in every run, alongside whatever checks the repo defines.

### Opt-out

Pass `--no-user-checks` to skip personal checks in `~/.cop/checks/` for a run.

## cop vscode

Download and install the Cop VS Code extension (syntax highlighting and IntelliSense for `.cop` files). Downloads from the latest GitHub release and installs to `~/.vscode/extensions/`.

```bash
cop vscode
```

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

## CI Integration

```yaml
# GitHub Actions
- name: Install cop
  run: |
    curl -L https://github.com/KrzysztofCwalina/cop/releases/latest/download/cop-linux-x64.zip -o cop.zip
    unzip cop.zip && chmod +x cop && mv cop /usr/local/bin/

- name: Run checks
  run: cop
```
