# Testing with Agent Cop

This guide covers how to write and run tests for cop packages and programs using the `test` keyword.

## Overview

Agent Cop provides a built-in test framework. Tests are declared with the `test` keyword in `.cop` files and run with `cop test`.

```cop
import csharp-checks
import code

test has-types = assert(csharp.Types.Count > 0)
test has-public = assert(csharp.Types:isPublic.Count > 0)
test no-var = assert(csharp.Statements:isVar.Count == 0)
```

```bash
cop test checks-test.cop
```

```
  ✓ has-types
  ✓ has-public
  ✓ no-var

  3 tests, 3 passed, 0 failed
```

## Syntax

```cop
test <name> = assert(<condition>)
test <name> = assert(<condition>, '<message>')
```

| Part | Required | Description |
|---|---|---|
| `<name>` | yes | Test identifier (shown in output) |
| `<condition>` | yes | A boolean expression to evaluate |
| `'<message>'` | no | Custom failure message (defaults to test name) |

Examples:

```cop
import code

# Basic: assert types exist
test has-types = assert(csharp.Types.Count > 0)

# Filtered: assert at least one public type exists
test public-types = assert(csharp.Types:isPublic.Count > 0)

# Assert absence of bad patterns (empty check)
test no-var = assert(csharp.Statements:isVar.Count == 0)

# With message
test has-files = assert(Files.Count > 0, 'expected source files in project')
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
| `0` | All tests passed |
| `1` | One or more tests failed |
| `2` | Parse error, fatal error, or no tests found |

Use exit codes in CI pipelines:

```bash
cop test tests/cop/ || exit 1
```

## Test Isolation

Tests declared with `test` only run via `cop test`. They are skipped during normal execution (`cop <package>` or `cop <file>`). This means you can safely put test declarations alongside regular code — they won't execute unless you explicitly run `cop test`.

## Writing Good Tests

### Test what your predicates filter

The most valuable tests verify that predicates match the right items:

```cop
import code

# ── Predicates under test ──
predicate isClient(Type) => Type.Name:endsWith('Client')

# ── Tests ──
test clients-found = assert(csharp.Types:isClient.Count > 0, 'expected Client types in sample')
test public-clients = assert(csharp.Types:isClient:isPublic.Count > 0)
```

### Test collection unions

```cop
import code

let public-csharp = csharp.Types:isPublic
let public-python = python.Types:isPublic
let all-public = public-csharp + public-python

test union-not-empty = assert(all-public.Count > 0)
```

### Test absence of bad patterns

Use `.Count == 0` to verify that rules catch nothing in clean code:

```cop
import code

predicate isVar(Statement) => Statement.Kind == declaration && Statement.Keywords:contains('var')

# Run against known-clean code: should find zero violations
test clean-no-var = assert(csharp.Statements:isVar.Count == 0)
```

### Name tests descriptively

Test names appear in the output:

```
  ✓ clients-found
  ✓ public-clients
  ✗ clean-no-var: assert failed: clean-no-var
```

## Project Structure

A typical package with tests:

```
my-package/
  src/
    checks.cop          # The package rules
  tests/
    samples/
      GoodClient.cs     # Clean code (tests should pass)
      BadClient.cs      # Code with violations
    test-checks.cop     # Test file
```

Run from the test directory so providers scan the sample files:

```bash
cd my-package/tests
cop test test-checks.cop
```
