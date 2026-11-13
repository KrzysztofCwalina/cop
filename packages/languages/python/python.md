---
name: python-lister
version: 2.0.0
title: Python API Surface Analysis
description: API surface tracking, export, and diff for Python libraries
authors: cop-team
tags: python, api, surface, diff, breaking-changes
language: Python
dependencies:
  - github.com/cop/cop/python-library-checks
---

# Python API Surface Analysis

Commands for tracking public API surface, detecting breaking changes,
and validating API listings against baseline files for Python libraries.

## Reusable language queries

Import `python` and narrow common objects with `asPython` to access Python-only
facts. Every Python helper is language-constrained, so importing other language
packages does not make these predicates select foreign declarations.

| C# helper or concept | Python counterpart |
|---|---|
| `parse()` / `parse(path)` | Same functions, for `.py` and `.pyi` files |
| `isInterface` | `Protocol` bases, via the existing `IsProtocol` fact |
| Records and generated data members | Existing `isDataclass` and `isNamedTuple` (not C# value-type semantics) |
| `isGenericType` / `isGenericMethod` | Explicit PEP 695 type parameters, including bounds, variadics and defaults; native analysis requires Python 3.12+ |
| `isYield` / `isAwaitForeach` | `yield` / `yield from` and `async for`; `isAsyncFor` is also available |
| `isEnvironmentVariableAccess` | Parsed `os.getenv`, `os.putenv`, `os.unsetenv`, `os.environ` mapping methods and subscripts |
| `isEnvironmentVariableRead` | `os.getenv`, `os.environ.get`, and load subscripts; writes/deletes are not reads |
| `isUsingResource` | Calls directly supplying `with` / `async with` context expressions, including awaited calls |
| `transfersResourceOwnership` | Directly returned call results and direct call-result assignments to an unmodified instance receiver's member |
| `alwaysRethrowsOriginalException` | Conservative explicit handler-flow proof described below |
| `isCancellationHandler` | Exact qualified standard-library cancellation exception names, including exception tuples |
| `isCallerCancellationPreserved` | Original-exception preservation on every supported explicit handler path |

Environment recognition uses exact qualified syntax (`os.environ.get`, not an
arbitrary object's `get`). It does **not** resolve import aliases, local shadowing,
dynamic attribute access, or wrappers. Mapping methods recognized as access are
`get`, `pop`, `setdefault`, `update`, `clear`, `items`, `keys`, `values`, and `copy`.
Bare mapping references/iteration are not currently represented as environment
access statements. No target Python code is executed.

### Explicit exception-flow contract

The handler proof supports straight-line statements and `if`/`elif`/`else` paths.
Each reachable explicit path must end in bare `raise` or `raise <caught-name>`
without rebinding that name or adding an explicit cause. A conditional raise alone,
replacement exception, return path, or raise only inside a nested function is not
a proof. Calls and assertions may raise implicitly; this analysis is **not** an
exception-effects proof. It does not infer that logging or cleanup cannot throw.

Loops, `match`, nested `try` statements, context managers within the handler,
`await`/`yield`, and exception groups are unknown, not positive proofs. An enclosing
context manager also prevents a preservation proof because it may suppress the
exception. Enclosing `finally` suites must have only supported fall-through paths;
returning or raising from cleanup invalidates the proof. Nested function bodies
are not treated as executed paths. Conservative rebinding detection can reject
otherwise safe handlers.

Cancellation classification recognizes `asyncio.CancelledError`,
`asyncio.exceptions.CancelledError`, and `concurrent.futures.CancelledError`
syntactically, including tuple handlers. Aliases, imported unqualified names,
derived cancellation classes and symbol shadowing are not resolved. Preservation
also applies to broader handlers that always rethrow: it means **if cancellation
enters this handler, these supported explicit paths preserve that exception**.
There is no caller-task/token provenance, shield/uncancel analysis, cancellation
counter tracking, or inference about unrelated task completion.

### Resource escape contract

Only the outer call directly used as a context expression is a managed-resource
initializer; argument and body calls do not qualify. Only a directly returned
call result or a result assigned directly to an instance member is an escape.
Instance-member recognition requires an undecorated method, an unmodified first
parameter, and exactly one parsed write to that member in the method (including
nested declarations, conservatively). Reassigned/deleted members are not proved.
Ordinary arguments, local assignments, local aliases later returned,
decorated methods and arbitrary-object member assignments are not ownership proofs.
Returns overridden by unsupported or non-fall-through `finally` paths do not
qualify. Escape does not establish that a call creates a resource, that a receiver
will dispose it, or that implicit exceptions cannot prevent the transfer.

### Interpreter-free backend and caching

The built-in lexer/parser remains usable without Python. It emits the same
language-specific fields and conservatively supports straight-line and
`if`/`else` rethrows, qualified cancellation catches, direct returned calls, a first
simple `with`/`async with` call expression, qualified environment calls/subscripts,
explicit generic parameters, yield and async iteration.

Its incomplete syntax graph cannot prove instance-member transfers, awaited or
multiple context expressions, cancellation tuples, or flow through an enclosing
`try` suite. A handler with a caught-name assignment/import/declaration is
conservatively rejected. Unsupported proofs remain false; they are not claims
that code is incorrect. Native AST analysis additionally populates parsed call
arguments, value/condition expressions, and nesting; the simpler parser currently
populates only simple qualified call targets, not a complete expression graph.

All added statement facts round-trip through language cache flags and factories.
Python source caching remains disabled by the existing provider policy because
its separate deferred semantic binding requires a live toolchain.

### Concepts without a language-equivalent helper

The entire C# helper surface was audited. Python has no direct C# assembly `load`,
async-void return contract, nullable directive, record struct, readonly/ref/file
type, partial declaration, primary constructor, extension-method declaration,
unsafe/extern method, expression-bodied method declaration, lock/unsafe/fixed/
checked/unchecked block, goto, or filtered-catch syntax. These are not exposed as
placeholder false predicates. Python lambdas are not currently method declarations
in the common model; legacy `TypeVar`/`Generic[T]` relationships are not populated
as explicit `TypeParameters`. Existing dataclass, protocol, abstract-class,
staticmethod/classmethod/property, generator, dunder, assertion, comprehension,
global and nonlocal helpers remain available.
