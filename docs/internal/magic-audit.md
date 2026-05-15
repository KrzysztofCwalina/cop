# Comprehensive Audit: All Magic, Intrinsics & Implicit Behavior

This document catalogs every instance of "magic" behavior in the Cop runtime — things that happen implicitly, are hardcoded in C#, or cannot be replicated by a `.cop` package author through declarations alone.

---

## Auto-Execution / Injected Behavior

| # | What | File:Line | Behavior |
|---|------|-----------|----------|
| 1 | Action-let auto-execution | ScriptInterpreter.cs:389-497 | Lets with terminal filters (`toWarning`, `toError`, `toInfo`, `toOutput`, `toSave`, `assert`, `assertEmpty`) auto-run without explicit invocation |
| 2 | CHECK command injection | Engine.cs:568-571 | Built-in `CHECK` command always injected into package projects |
| 3 | RUN CHECK synthesis | Engine.cs:573-666 | If no rules specified, auto-synthesizes `RUN CHECK(name)` for exported violation lets |
| 4 | Violation detection | Engine.cs:680-727 | Detects violation collections by presence of `toError`/`toWarning`/`toInfo` terminal filter |
| 5 | Streaming auto-detection | Engine.cs:361-383 | Automatically picks streaming mode when any command uses a streaming collection |
| 6 | Built-in sinks | Engine.cs:832-834 | `console` and `file` sinks always registered |
| 7 | Built-in providers | Engine.cs:13-37 | `filesystem`, `code`, `markdown` providers always loaded |

---

## Provider Auto-Registration

How providers make their collections/types/functions available without `.cop` declarations.

| # | What | File:Line | Behavior |
|---|------|-----------|----------|
| 1 | Collections auto-exposed | ProviderLoader.cs:QueryAndRegister | Provider query results auto-added to `_nsCollections[ns][name]` — accessible via qualified names (`ns.Collection`) or explicit `export let` in `.cop` packages |
| 2 | Streaming sources auto-registered | ProviderLoader.cs:RegisterSourceProvider | Each SourceProvider collection → registered streaming source via `RegisterStreamingSource` |
| 3 | Sink providers auto-registered | ProviderLoader.cs:RegisterSinkProvider | Each SinkProvider → registered sink via `RegisterSink` |
| 4 | Provider functions auto-registered | ProviderLoader.cs:QueryAndRegister | Functions registered under namespace (e.g., `http.Get`) |
| 5 | Schema types auto-registered | TypeRegistry.cs:RegisterProviderSchema | Provider types become available without `.cop` type declaration |
| 6 | DLL discovery by convention | ProviderLoader.cs:FindProviderDll | Finds provider DLL matching `"{pkg}-provider"` or any DLL containing `"provider"` |

---

---

## Parser Magic

Syntax-level behavior that's not available to `.cop` declarations.

| # | What | File:Line | Behavior |
|---|------|-----------|----------|
| 1 | Implicit output | ScriptParser.cs:154-177 | Bare string/expression at top level → output command |

---

## Import Resolution (no export filtering)

| # | What | File:Line | Behavior |
|---|------|-----------|----------|
| 1 | ALL symbols imported | ImportResolver.cs:61-97 | types, collections, lets, functions, predicates are ALL accumulated from imported packages — only commands check `IsExported` |
| 2 | Package discovery by convention | ImportResolver.cs:103-140 | Directories with `{name}.md`, `src/`, or `types/` are treated as packages |

**Impact:** When you `import foo`, you get ALL of foo's types, predicates, functions, and lets — not just the ones marked `export`. Export filtering only applies to commands.

---

## CLI Implicit Behavior

| # | What | File:Line | Behavior |
|---|------|-----------|----------|
| 1 | Auto-restore imports | RunCommand.cs:241-242 | Missing packages auto-downloaded from GitHub feeds |
| 2 | Feed path discovery | RunCommand.cs:384-403 | Walks up directories for `packages/` dirs + always adds `~/.cop/packages` |
| 3 | Package mode detection | RunCommand.cs:230-239 | When no local `.cop` files exist, switches to package mode |
| 4 | Remote URL execution | RunCommand.cs:308-377 | `http://` args download and execute as temp files |

---

## Recently Eliminated Magic

| What | Commit | Description |
|------|--------|-------------|
| Action keyword recognition | 030dc4d | Commands (PRINT, SAVE, etc.) are now regular intrinsic functions resolved by name like any other symbol. No parser heuristics. |
| ActionName-based dispatch | 030dc4d | Interpreter no longer branches on action names. Side effects happen inside `CallIntrinsicFunction` via sink delegates. |
| ALL-CAPS parsing heuristic | 34861aa | Removed `ToUpperInvariant()` normalization and `IntrinsicCommands` HashSet. |
| Bool property fallback | 84262d8 | Removed camelCase→PascalCase mapping from predicate evaluator. |

---

## Recommendations

Remaining magic to consider eliminating:

1. **Enforce export filtering on imports** — only `export`-marked symbols should be available to importers (not just commands)
2. **Streaming source/sink bare-name fallback** — still present in TypeRegistry for streaming sources and sinks

Language-level features (Collection/String Members, Query Operators, Predicates, Language Filters) are appropriate to keep as built-in — they define the language itself and are documented in the language reference.
