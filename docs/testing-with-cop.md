# Testing with Cop

This guide covers how to write and run tests for cop packages and programs using the `ASSERT` and `ASSERT_EMPTY` commands.

## Overview

Cop provides built-in test commands that verify collection properties. Tests are regular `.cop` files that use `ASSERT` and `ASSERT_EMPTY` instead of `PRINT` or `CHECK`. Run them with `cop test`.

```ruby
import csharp

command test-has-types = ASSERT(csharp.Types)
command test-has-public = ASSERT(csharp.Types:isPublic)
command test-no-var = ASSERT_EMPTY(csharp.Statements:isVar)
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

Passes when the collection is **non-empty** (at least one item matches).

```ruby
command test-name = ASSERT(collection)
command test-name = ASSERT(collection:filter, 'optional message')
```

| Part | Required | Description |
|---|---|---|
| `collection` | yes | A collection name or filtered chain |
| `'message'` | no | Custom failure message (defaults to command name) |

Examples:

```ruby
import csharp

# Basic: assert types exist
command test-has-types = ASSERT(csharp.Types)

# Filtered: assert at least one public type exists
predicate isPublic(Type) => Type.Public
command test-public-types = ASSERT(csharp.Types:isPublic)

# With message
command test-has-files = ASSERT(Files, 'expected source files in project')
```

## ASSERT_EMPTY

Passes when the collection is **empty** (zero items match). This is the inverse of `ASSERT` — use it to verify that bad patterns are absent.

```ruby
command test-name = ASSERT_EMPTY(collection)
command test-name = ASSERT_EMPTY(collection:filter, 'optional message')
```

Examples:

```ruby
import csharp

# Verify no var declarations
predicate isVar(Statement) => Statement.Kind == 'declaration' && Statement.Keywords:contains('var')
command test-no-var = ASSERT_EMPTY(csharp.Statements:isVar)

# Verify no Thread.Sleep calls
predicate threadSleep(Statement) => Statement.Kind == 'call'
    && Statement.TypeName == 'Thread' && Statement.MemberName == 'Sleep'
command test-no-sleep = ASSERT_EMPTY(csharp.Statements:threadSleep, 'Thread.Sleep should not be used')
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

`ASSERT` and `ASSERT_EMPTY` commands only run via `cop test`. They are skipped during `cop run`, just like `SAVE` commands require explicit invocation. This means you can safely mix test files alongside regular `.cop` files — `cop run` will ignore the assertions.

## Writing Good Tests

### Test what your predicates filter

The most valuable tests verify that predicates match the right items:

```ruby
import csharp

# ── Predicates under test ──
predicate isClient(Type) => Type.Name:endsWith('Client')
predicate isPublic(Type) => Type.Public

# ── Tests ──
command test-clients-found = ASSERT(csharp.Types:isClient, 'expected Client types in sample')
command test-public-clients = ASSERT(csharp.Types:isClient:isPublic)
```

### Test collection unions

```ruby
import csharp
import python

let public-csharp = csharp.Types:isPublic
let public-python = python.Types:isPublic
let all-public = public-csharp + public-python

command test-union-not-empty = ASSERT(all-public)
```

### Test absence of bad patterns

Use `ASSERT_EMPTY` to verify that rules catch nothing in clean code:

```ruby
import csharp

predicate isVar(Statement) => Statement.Kind == 'declaration' && Statement.Keywords:contains('var')

# Run against known-clean code: should find zero violations
command test-clean-no-var = ASSERT_EMPTY(csharp.Statements:isVar)
```

### Name tests descriptively

Use `command test-...` naming to make output clear:

```
  ✓ test-clients-found
  ✓ test-public-clients
  ✗ test-clean-no-var: Thread.Sleep should not be used (found 2 items)
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
