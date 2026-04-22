# Code Analysis Package Reference

The `code-analysis` package provides structured violation reporting for source code checks. It defines the `Violation` type and severity functions (`toError`, `toWarning`, `toInfo`) that transform code items into typed results with file path, line number, and message.

**Source:** [`packages/code-analysis/src/code-analysis.cop`](../../packages/code-analysis/src/code-analysis.cop)

## Import

```ruby
import code-analysis
```

This also brings `code` and `filesystem` into scope (transitive dependencies).

## Violation Type

| Property | Type | Description |
|---|---|---|
| `Severity` | string | `'error'`, `'warning'`, or `'info'` |
| `Message` | string | Human-readable violation message |
| `File` | string | Path to the file containing the violation |
| `Line` | int | Line number (0 for folder-level violations) |
| `Source` | string | Source code text |

## Functions

Each function has overloads for `Statement`, `Type`, `Line`, and `Folder`:

| Function | Description |
|---|---|
| `toError(item, message)` | Creates a Violation with severity `'error'` |
| `toWarning(item, message)` | Creates a Violation with severity `'warning'` |
| `toInfo(item, message)` | Creates a Violation with severity `'info'` |

The `message` parameter supports template interpolation (e.g., `'Missing docs for {Type.Name}'`).

## CHECK Command

The package exports a `CHECK` command that prints formatted violations:

```ruby
import code-analysis

let errors = Code.Statements:csharp:varDeclaration
    :toError('Do not use var for {Statement.MemberName}')

CHECK(errors)
```

Output format: `file(line): severity: message`

## Usage

Most language-specific packages (e.g., `csharp`, `python`, `javascript`) import `code-analysis` and use these functions to produce their checks. You typically don't import `code-analysis` directly unless building custom checks.

```ruby
import code-analysis

let violations = Code.Types:csharp:publicType
    :!Name:endsWith('Base')
    :toWarning('{Type.Name} should not have a Base suffix')

CHECK(violations)
```
