# CLI Reference

Complete reference for `cop.exe` commands and options.

```bash
cop <command> [options]
```

Global options:

| Option | Description |
|--------|-------------|
| `-h` | Show help and usage information |
| `-v` | Show version |

> **Note:** Cop uses short flags only — there are no long-form `--help` or `--version` equivalents.

## Exit Codes

All commands use a consistent set of exit codes:

| Code | Meaning |
|------|---------|
| `0` | Clean run — no output, no violations, all tests passed |
| `1` | Output produced — violations found, tests failed, or items printed |
| `2` | Fatal error — parse error, missing file, or invalid arguments |

Use exit codes in CI pipelines:

```bash
cop run checks.cop || exit 1
```

## cop run

Run `.cop` programs. This is the primary command for executing checks, queries, and transformations.

```bash
cop run [<command>] [<args>] [-t <target>] [-c <commands>] [-f text|json] [-d]
```

| Argument / Option | Description |
|-------------------|-------------|
| `<command>` | Command name, `.cop` file path, or HTTPS URL to run. When omitted, all `.cop` files in the current directory are loaded. |
| `<args>` | Extra arguments passed to the program |
| `-t <target>` | Target directory, file, or comma-separated file list. Overrides the root path that providers scan. Defaults to the current directory. |
| `-f <format>` | Output format: `text` (default) or `json` |
| `-c <commands>` | Comma-separated list of commands to run. By default, all unnamed statements run; named commands only run when invoked by name or with `-c`. |
| `-d` | Enable diagnostic mode: print timing, collection counts, filter traces (`[trace]`), and `DEBUG` action output (`[debug]`) to stderr |

### Discovery behavior

When no `<command>` argument is given, cop discovers and loads all `.cop` files in the current directory. Unnamed statements (bare `PRINT`, `CHECK`, `foreach`) always execute. Named commands (`command my-check = ...`) only execute when invoked by name or listed in `-c`.

When a `.cop` file path is given, cop loads scripts from that file's directory.

When an HTTPS URL is given, cop downloads the remote `.cop` file, executes it against the current directory (or `-t` target), and resolves imports from locally available packages.

### Examples

Run all `.cop` files in the current directory:

```bash
cop run
```

Run a specific file:

```bash
cop run checks.cop
```

Run a named command:

```bash
cop run my-check
```

Run multiple named commands:

```bash
cop run -c lint,format
```

Target a specific directory:

```bash
cop run checks.cop -t src/
```

Target specific files:

```bash
cop run checks.cop -t Program.cs,Startup.cs
```

Run a remote `.cop` file from a URL:

```bash
cop run https://raw.githubusercontent.com/owner/repo/main/checks.cop
```

Run a remote file against a specific target:

```bash
cop run https://raw.githubusercontent.com/owner/repo/main/checks.cop -t src/
```

Output as JSON:

```bash
cop run checks.cop -f json
```

Show diagnostics (timing, collection counts, filter traces, and DEBUG output):

```bash
cop run checks.cop -d
```

## cop check

Run pre-built analysis checks from packages against your code. This is the fastest way to run checks without writing `.cop` files.

```bash
cop check <packages>... [-t <target>] [-c <rules>] [-f text|json] [-d]
```

| Argument / Option | Description |
|-------------------|-------------|
| `<packages>` | One or more package names to run (e.g., `csharp-style`, `csharp-library`) |
| `-t <target>` | Target directory to analyze. Defaults to the current directory. |
| `-c <rules>` | Comma-separated list of specific rules to run. When omitted, all exported checks in the package run. |
| `-f <format>` | Output format: `text` (default) or `json` |
| `-d` | Enable diagnostic mode |

### How it works

`cop check` loads the specified package's `.cop` files, resolves their imports, runs all providers against the target directory, and executes the `CHECK` command on all exported violation collections.

Packages are discovered from `packages/` directories in the project tree and from the user's restored package cache (`~/.cop/packages/`).

### Examples

Run C# style checks on the current directory:

```bash
cop check csharp-style
```

Run multiple packages:

```bash
cop check csharp-style csharp-library
```

Target a specific directory:

```bash
cop check csharp-style -t src/
```

Run only specific rules from a package:

```bash
cop check csharp-style -c interface-prefix,type-name-casing
```

## cop test

Run `ASSERT` and `ASSERT_EMPTY` commands in `.cop` files and report pass/fail results.

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

See [Testing with Cop](testing-with-cop.md) for a full guide on writing tests.

## cop help

List commands defined in a `.cop` program.

```bash
cop help [<file>]
```

| Argument | Description |
|----------|-------------|
| `<file>` | `.cop` file to inspect. When omitted, all `.cop` files in the current directory are used. |

### Examples

List all commands in the current directory:

```bash
cop help
```

List commands in a specific file:

```bash
cop help checks.cop
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

## cop package

Manage cop packages — restore dependencies, scaffold new packages, validate, publish, and search.

```bash
cop package <subcommand>
```

### cop package restore

Restore packages declared in a `.cop` file. Downloads packages from GitHub feeds, resolves transitive dependencies, and places files in the local `packages/` directory.

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
