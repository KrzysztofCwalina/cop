---
name: javascript-lister
version: 2.0.0
title: JavaScript/TypeScript API Surface Analysis
description: API surface tracking, export, and diff for JavaScript/TypeScript libraries
authors: cop-team
tags: javascript, typescript, api, surface, diff, breaking-changes
language: JavaScript
dependencies:
  - github.com/cop/cop/javascript-library-checks
---

# JavaScript/TypeScript API Surface Analysis

Commands for tracking public API surface, detecting breaking changes,
and validating API listings against baseline files for JavaScript/TypeScript libraries.

## Reusable language queries

Import `javascript` and obtain a codebase with `javascript.parse()`. Common
declaration, modifier, signature, receiver and argument queries come from `code`.
Narrow declarations/statements with `asJavaScript` for language-specific helpers.
These overloads do not classify statements from other languages.

| C# query/concept | JavaScript/TypeScript applicability |
|---|---|
| `isInterface` | TypeScript interface declarations; not a JavaScript runtime concept. |
| `isGenericType`, `isGenericMethod` | Parsed TypeScript type parameters, including generic functions and arrows. |
| Expression-bodied methods | `isArrowMethod` recognizes arrows; not every arrow has an expression body. |
| Iterator/yield, async iteration | `isGeneratorMethod`, `isYield`, `isAwaitForeach` (`for await ... of`). |
| Properties | `isGetter` / `isSetter`; no C# extension-method equivalent. |
| Environment variables | `isEnvironmentVariableAccess` / `isEnvironmentVariableRead` recognize actual `process.env` property/index accesses, not invented getter calls. |
| Original rethrows | `alwaysRethrowsOriginalException` proves supported explicit catch paths throw the unchanged caught value. |
| Cancellation handlers/preservation | `isCancellationHandler` recognizes a catch whose **first statement** positively compares the caught value's `name` to `'AbortError'`; `isCallerCancellationPreserved` proves that branch throws the original value. |
| Ownership transfer | `transfersResourceOwnership` recognizes constructions directly returned or assigned to a `this` member. |
| Resource management | `isUsingResource` recognizes calls directly initializing `using` / `await using` declarations; arguments and calls inside the resource body are not initializers. |

The remaining C# exports were reviewed but are not given fake JavaScript
equivalents: assembly loading, `async void` (JS async functions return promises),
nullable directives, records/record structs, readonly/ref/file/partial types,
primary constructors, extension/partial/unsafe/extern methods, lock/unsafe/fixed/
checked blocks, goto, catch filters and type-restricted catches. JavaScript
constructors, exports, `extends`, `abstract` and `implements` already have parsed
language-specific facts and helpers.

### Semantic limits

* `TypeName` on calls describes a **syntactic receiver**, even when unbound.
  `ReceiverType`, `ValueType` and `ReferencedCallable` separately describe
  compiler bindings when available.
* Environment recognition is syntactic, not symbol binding: it does not resolve
  aliases or distinguish a shadowing variable named `process`. Property and
  string/computed index access, writes and deletes are recognized. A plain write
  or deletion is an access but not a read; compound updates also read. No claim
  is made about browser environments or non-Node environment APIs.
* Catch proofs track blocks, `if`/`else`, explicit throw/return and fallthrough.
  A conditional throw without a final throw does not prove all paths.
  Calls may implicitly throw; those effects are deliberately outside the proof.
  Assignments/updates, declarations, nested functions/classes, awaits/yields,
  nested try/finally and unsupported control flow conservatively prevent a proof.
  An enclosing `finally` also prevents a proof, even if it looks harmless.
* JavaScript catch clauses are **untyped**. An `AbortError` name guard is a
  convention, not a resolved `DOMException` type or an exception filter.
  Preservation refers only to the caught cancellation value: caller
  `AbortSignal` lineage, linked signals, and operation-to-signal ownership are
  not proven. Arbitrary/negative guards do not qualify.
* Ownership is a direct syntactic transfer from an explicit `return` statement
  or member assignment, not proof that a constructed object
  implements a disposal protocol or that an arbitrary member really owns it.
  Factories, local aliases, implicit arrow returns and ordinary call arguments do not qualify. Returns
  inside an enclosing try/finally are conservatively excluded.

Both compiler-backed analysis and the built-in no-runtime parser support the
documented simple forms, including conservative branch/reassignment checks.
Compiler-backed analysis handles the TypeScript AST more completely. The
fallback explicitly warns that complete JS/TS/JSX facts and type binding are
unavailable; complex computed receivers, parenthesized resource initializers,
typed/multiple `using` declarations, complex generic syntax and automatic
semicolon insertion are not fully analyzed. A negative result outside the
supported subset is **not** evidence that code is safe.
