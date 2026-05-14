# Testing with Agent Cop

This guide covers how to write and run tests for cop packages and programs using the `ASSERT` command.

## Overview

Agent Cop provides a built-in test command that evaluates boolean conditions. Tests are regular `.cop` files that use `ASSERT` instead of producing output or using `CHECK`. Run them with `cop test`.

```ruby
import csharp-checks
import code

command test-has-types = ASSERT(csharp-checks.Types.Count > 0)
command test-has-public = ASSERT(csharp-checks.Types:isPublic.Count > 0)
command test-no-var = ASSERT(csharp-checks.Statements:isVar.Count == 0)
```

```bash
cop test checks-test.cop
```

```
  ✓ test-has-types
  ✓ test-has-public
  ✓ test-no-var

  3 tests, 3 passed, 0 failed
```

## ASSERT

Passes when the condition evaluates to **true**.

```ruby
command test-name = ASSERT(condition)
command test-name = ASSERT(condition, 'optional message')
```

| Part | Required | Description |
|---|---|---|
| `condition` | yes | A boolean expression to evaluate |
| `'message'` | no | Custom failure message (defaults to command name) |

Examples:

```ruby
import csharp-checks
import code

# Basic: assert types exist
command test-has-types = ASSERT(csharp-checks.Types.Count > 0)

# Filtered: assert at least one public type exists (isPublic comes from code package)
command test-public-types = ASSERT(csharp-checks.Types:isPublic.Count > 0)

# Assert absence of bad patterns (empty check)
command test-no-var = ASSERT(csharp-checks.Statements:isVar.Count == 0)

# With message
command test-has-files = ASSERT(Files.Count > 0, 'expected source files in project')
```

## Running Tests

### Single file

```bash
cop test my-tests.cop
```

### Directory

Run all `.cop` files in a directory:

```bash
cop test tests/cop/
```

### Exit codes

| Code | Meaning |
|------|---------|
| `0` | All assertions passed |
| `1` | One or more assertions failed |
| `2` | Parse error, fatal error, or no assertions found |

Use exit codes in CI pipelines:

```bash
cop test tests/cop/ || exit 1
```

## Test Isolation

`ASSERT` commands only run via `cop test`. They are skipped during `cop run`, just like `SAVE` commands require explicit invocation. This means you can safely mix test files alongside regular `.cop` files — `cop run` will ignore the assertions.

## Writing Good Tests

### Test what your predicates filter

The most valuable tests verify that predicates match the right items:

```ruby
import csharp-checks
import code

# ── Predicates under test ──
predicate isClient(Type) => Type.Name:endsWith('Client')

# ── Tests (isPublic comes from the code package) ──
command test-clients-found = ASSERT(csharp-checks.Types:isClient.Count > 0, 'expected Client types in sample')
command test-public-clients = ASSERT(csharp-checks.Types:isClient:isPublic.Count > 0)
```

### Test collection unions

```ruby
import csharp-checks
import python-checks
import code

let public-csharp = csharp-checks.Types:isPublic
let public-python = python-checks.Types:isPublic
let all-public = public-csharp + public-python

command test-union-not-empty = ASSERT(all-public.Count > 0)
```

### Test absence of bad patterns

Use `.Count == 0` to verify that rules catch nothing in clean code:

```ruby
import csharp-checks

predicate isVar(Statement) => Statement.Kind == 'declaration' && Statement.Keywords:contains('var')

# Run against known-clean code: should find zero violations
command test-clean-no-var = ASSERT(csharp-checks.Statements:isVar.Count == 0)
```

### Name tests descriptively

Use `command test-...` naming to make output clear:

```
  ✓ test-clients-found
  ✓ test-public-clients
  ✗ test-clean-no-var: Thread.Sleep should not be used
```

## Project Structure

A typical package with tests:

```
my-package/
  src/
    checks.cop          # The package rules
  tests/
    samples/
      GoodClient.cs     # Clean code (assertions should pass)
      BadClient.cs      # Code with violations
    test-checks.cop     # Test file
```

Run from the test directory so providers scan the sample files:

```bash
cd my-package/tests
cop test test-checks.cop
```
