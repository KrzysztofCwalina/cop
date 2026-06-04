<img src="docs/mascot.png" alt="Agent Cop mascot" width="200" align="left" />

# Agent Cop

**Agent Cop** is a lightweight static analysis tool. You express code quality requirements as rules in a purpose-built DSL, and cop.exe enforces them deterministically — in CI, in your editor, or from the command line.

<br clear="left" />

## The Problem

Coding agents produce code at machine speed. Without deterministic enforcement, human engineers become bottlenecked reviewing everything for convention drift and design violations. Natural language instructions (copilot instructions, system prompts) are advisory — agents often ignore instructions as they get too complex and too large.

## The Solution

Write enforceable rules in Agent Cop's DSL and run them with `cop.exe`. Rule violations block PRs and feed back to your coding agent automatically, just like compiler errors.

## Installation

Download `cop.exe` from the [releases](https://github.com/KrzysztofCwalina/cop/releases) page and add it to your PATH. 

## Quick Start

### 1. Create Rules

You can write rules manually or use your coding agent — not only for the application code you're writing, but also for the rules themselves. Just ask:

> "Write a cop rule that flags any method longer than 50 statements"

> "Add a cop rule that there should not be empty folders in the repo"

> "Create a cop rule that all images should be in /img subfolder"

> "Create a cop rule that blocks dependencies from Foo.dll to Bar.dll"

To enable your coding agent to write rules, run `cop init` in your repo's root. This generates instruction files (`.github/copilot-instructions.md`, `AGENTS.md`) that teach the agent how to write cop rules. Commit them to your repo.

Here's what a rule for "method longer than 50 statements" looks like:

```ruby
import code

predicate tooLong(Method) => Method.Statements > 50

let violations = Code.Methods:tooLong

CHECK(violations)
```

### 2. Run the Rules

Ask the agent to run cop checks:

> "Run cop checks and fix any violations"

Or run them yourself:

```bash
cop <rules_source.cop>
```

By default, cop analyzes the current working directory. Use `-t` to target a different folder:

```bash
cop <rules_source.cop> -t src/
```

Exit code 1 if violations found, 0 if clean — suitable for CI. Agents see these errors and fix them automatically, just like compiler errors.

## Running Built-In Checks

Agent Cop ships with pre-built check packages for common rules. No `.cop` files needed:

```bash
cop csharp-checks                         # naming, formatting, documentation
cop csharp-checks csharp-library-checks   # run multiple packages
cop csharp-checks -t src/                 # analyze a specific directory
```

You can see all pre-built packages by executing `cop package list`.
You can see details of packages, including usage samples, with `cop help <package_namwe>`.

## VS Code Extension

For syntax highlighting, install the VS Code extension:

```bash
cop vscode
```

## Resources

- [Samples](docs/samples.md) — all runnable examples in this repo
- [Language Reference](docs/language-reference.md) — full DSL syntax
- [Language Design](docs/language-design.md) — philosophy and design decisions
- [Language Grammar](docs/cop-grammar.md) — formal EBNF grammar specification
- [CLI Reference](docs/cli-reference.md) — all commands and options
- [Static Analysis with Cop](docs/static-analysis.md) — writing source code checks
- [Testing with Cop](docs/testing-with-cop.md) — writing and running tests
- [Testing](docs/testing.md) — test keyword and test framework
- [Extensibility](docs/extensibility.md) — writing providers and wrapping external tools
- [Packaging](docs/packaging.md) — creating, publishing, and restoring packages
- [Working with the REPL](docs/repl.md) — interactive development and prototyping
- [CodeQL Integration](docs/codeql.md) — using cop with CodeQL
- [Package Reference](https://krzysztofcwalina.github.io/cop/reference.html) — types, predicates, and checks
- [Why Agent Cop](docs/why-agent-cop.md) — product strategy and rationale
