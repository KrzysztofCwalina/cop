# Agent Cop

**Agent Cop** lets you express code quality requirements as enforcable static analysis rules — stopping code slop before it lands. Describe what what requirements you have in plain English, your coding agent writes the rule in Ageent Cop DSL, and it runs deterministically, e.g. in CI.

## The Problem

Coding agents produce code at machine speed. Without deterministic enforcement, architects become bottlenecked reviewing everything for convention drift and design violations. Natural language instructions (copilot instructions, system prompts) are advisory — agents often ignore instructions as they get too complex and too large.

## The Solution

Use Agent Cop (cop.exe) to develop and execute static analysis rules. The workflow:

1. **Teach your agent about Agent Cop** — run `cop init` in your repo's root
2. **Create enforcable rules** — tell the agent what you want to enforce
3. **Run cop.exe** — rule violations block PRs and feed back to your agent automatically

## Installation

Download `cop.exe` from the [releases](https://github.com/KrzysztofCwalina/cop/releases) page and add it to your PATH. 

## Quick Start

### 1. Initialize Agent Context

Run `cop init` in the root folder of your repo.

```bash
cop init
```

This generates instruction files (`.github/copilot-instructions.md`, `AGENTS.md`) that teach your coding agent[s] how to write cop rules. Commit them to your repo.

### 2. Ask the Agent to Write Rules

With context in place, just ask:

> "Write a cop rule that flags any method longer than 50 statements"

> "Add a cop rule that there should not be empty folders in the repo"

> "Create a cop rule that all images should be in /img subfolder"

> "Create a cop rule that blocks dependencies from Foo.dll to Bar.dll"

The agent reads the instruction files setup using `cop init`, which tell it how to use cop.exe to write and execute rules.

### 3. Run the Rules

Ask the agent to run cop checks. It should execute something like the following:

```bash
cop <rules_source.cop>
```

Exit code 1 if violations found, 0 if clean — suitable for CI. Agents see these errors and fix them automatically, just like compiler errors.

### 4. The Self-Check Loop

When a coding agent makes a change you don't like, ask it to encode that feedback as a rule:

> "Add a self-check that we never use `Console.WriteLine` — we use our Logger class"

The agent writes the rule, and from that point forward the convention is enforced permanently. One-off code review feedback becomes automated policy.

## Running Built-In Checks

Agent Cop ships with pre-built check packages for common rules. No `.cop` files needed:

```bash
cop csharp-checks                         # naming, formatting, documentation
cop csharp-checks csharp-library-checks   # run multiple packages
cop csharp-checks -t src/                 # analyze a specific directory
```

## A Simple Rule

Most rules follow the same pattern: import a package, define predicates, filter collections, output violations.

```ruby
import code

# Predicates test individual items
predicate client(Type) => Type.Name:endsWith('Client')

# Let declarations create filtered subsets
let Clients = Code.Types:client

# foreach iterates over the subset — one line per item
foreach Clients:csharp:!isSealed => PRINT('{error:@red} {item.Name} should be sealed')
```

See [Samples](docs/samples.md) for more examples covering JSON, filesystem, cross-language, HTTP, and more.

## Testing

Write tests with `ASSERT` and `ASSERT_EMPTY`, run them with `cop test`:

```ruby
import csharp

command TEST-HAS-TYPES = ASSERT(csharp.Types)
command TEST-NO-VAR = ASSERT_EMPTY(csharp.Statements:varDeclaration)
```

```bash
cop test
```

See [Testing with Cop](docs/testing-with-cop.md) for the full testing guide.

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

| Exit Code | Meaning |
|-----------|---------|
| `0` | Clean — no output, all tests pass |
| `1` | Violations found or tests failed |
| `2` | Fatal error (parse error, missing package) |

## Agent Help Commands

Agents use these commands to learn the language on demand:

```bash
cop help language        # Full language reference (syntax, types, operators)
cop help <package>       # Package documentation (types, functions, examples)
cop package list         # List all available packages
```

## VS Code Extension

For syntax highlighting, install the VS Code extension:

1. Open VS Code
2. Press `Ctrl+Shift+P` → **Extensions: Install from VSIX...**
3. Select `install/vscode-cop/` from this repository

## Resources

- [Samples](docs/samples.md) — all runnable examples in this repo
- [Language Reference](docs/language-reference.md) — full DSL syntax
- [CLI Reference](docs/cli-reference.md) — all commands and options
- [Static Analysis with Cop](docs/static-analysis.md) — writing source code checks
- [Testing with Cop](docs/testing-with-cop.md) — writing and running tests
- [Package Reference](https://krzysztofcwalina.github.io/cop/cop-reference.html) — types, predicates, and checks
- [Why Agent Cop](docs/why-agent-cop.md) — product strategy and rationale
