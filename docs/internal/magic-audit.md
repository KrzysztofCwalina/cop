# Magic Audit: Hardcoded Identifiers & Domain-Specific Behavior

This document tracks places where the C# engine/interpreter/CLI has hardcoded knowledge of domain-specific concepts. The goal is for cop to be a general-purpose runner where the interpreter has **no hardcoded identifiers** — all domain concepts belong in packages.

---

## Design Principle

The interpreter and CLI should be **completely domain-agnostic**. No hardcoded identifiers beyond a limited, clearly-defined set of intrinsics. All domain concepts (violations, checks, severity levels, error/warning/info) must live in `.cop` packages. The engine should provide general mechanisms (e.g., "this function is terminal", "this let produces output") that packages can use, without the engine knowing what the output *means*.

---

## Problems To Fix

### 1. `-cql` CodeQL transpilation in RunCommand

**Location**: `cop/cli/Commands/RunCommand.cs:30, 43-44`

```csharp
var cqlOption = new Option<bool>("-cql") { Description = "Transpile .cop checks to CodeQL .ql files" };
```

**Issue**: A specific third-party analysis format (CodeQL) is baked into the generic `run` command. This is a domain-specific export feature.

**Fix**: Move to a separate `cop export cql` command or a plugin. The `run` command should only run programs.

---

## Gray Areas (Need Decision)

### 2. `"main"` as default command name

**Location**: `cop/runtime/Engine.cs:550, 557-560`

```csharp
var commandsToRun = rules.Count > 0 ? rules : ["main"];
```

**Discussion**: Convention borrowed from C/Go/Java. Arguably general-purpose (every program needs an entry point), but still a hardcoded string. Could be replaced with "run the first/only command" or a configurable entry point. **Low severity** — widespread language convention.

---

### 3. `"error"` intrinsic function

**Location**: `cop/shared/PredicateEvaluator.cs:1621-1623`

```csharp
"error" => inputItem is string msg
    ? new ErrorValue(msg)
    : new ErrorValue(inputItem?.ToString()),
```

**Discussion**: Creates an `ErrorValue` for pipeline error propagation. If `ErrorValue` is considered a general-purpose runtime concept (like exceptions in other languages), then `error()` is a legitimate intrinsic. If it's domain-specific, it should move to a package.

---

### 4. `"RUN"` / `"feed"` / `"flags"` / `"nic"` keywords

**Location**: `cop/shared/Tokenizer.cs:367-387`

**Discussion**: Some of these keywords (especially `RUN`, `feed`, `nic`) feel more like package/runtime concepts than core language syntax. May warrant review of whether they should be contextual identifiers rather than reserved keywords.

---

## Official Intrinsics (Legitimate — Keep in C#)

These are general-purpose language primitives implemented in C# that any scripting language would need. Declared in `.cop` with `= intrinsic` and dispatched via `CallIntrinsicFunction`:

| Intrinsic | Purpose | Justification |
|---|---|---|
| `print(message)` | Output to stdout | Core I/O |
| `save(path, content)` | Write to file | Core I/O |
| `debug(message)` | Diagnostic output | Development aid |
| `assert(condition, desc)` | Runtime assertion | Testing primitive |
| `fail(message)` | Terminate execution | Error control flow |
| `text(value)` | Convert to string | Type conversion |
| `read(path)` | Read file contents | Core I/O |
| `pathMatches(path, pattern)` | Glob matching | Utility |
| `program()` | Access program metadata | Reflection |
| `object(provider)` | Dynamic provider object | Provider infrastructure |
| `source(provider)` | Streaming source handle | Streaming infrastructure |
| `sink(provider)` | Streaming sink handle | Streaming infrastructure |

### Collection Operations (also intrinsic — implemented in ScriptInterpreter)

| Operation | Purpose |
|---|---|
| `Select(expr)` | Map/project items |
| `Where(pred)` | Filter items |
| `OrderBy(expr)` / `OrderByDescending(expr)` | Sort |
| `First(pred?)` / `Last(pred?)` | Take first/last |
| `Distinct(expr?)` | Deduplicate |
| `GroupBy(expr)` | Group into buckets |
| `Reduce(expr, init, sep)` | Fold/aggregate |
| `Sum(expr)` / `Min(expr)` / `Max(expr)` / `Average(expr)` | Numeric aggregates |
| `count(pred)` / `any(pred)` / `all(pred)` / `none(pred)` | Collection predicates |