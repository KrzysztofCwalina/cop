# Comprehensive Audit: All Magic, Intrinsics & Implicit Behavior

This document catalogs every instance of "magic" behavior in the Cop runtime — things that happen implicitly, are hardcoded in C#, or cannot be replicated by a `.cop` package author through declarations alone.

---

## Implicit Identifiers (always available without import)

| # | Name | File:Line | Description |
|---|------|-----------|-------------|
| 1 | `item` | PredicateEvaluator.cs:372 | Current iteration item in any predicate/filter |
| 2 | `null` | PredicateEvaluator.cs:373 | Null literal |
| 3 | `error` (bare) | PredicateEvaluator.cs:376 | ErrorValue with no message |
| 4 | `isError` (bare) | PredicateEvaluator.cs:379 | Check if current item is an error |
| 5 | `empty` (bare) | PredicateEvaluator.cs:396 | Check if item/collection/string is empty |
| 6 | `Program` | ScriptInterpreter.cs:251 | Built-in ProgramInfo from CLI args |
| 7 | Flags constants | PredicateEvaluator.cs:466 | All `flags` members are global identifiers (e.g., `Public`, `Static`) |
| 8 | Enum constants | PredicateEvaluator.cs:477 | All `enum` members are global identifiers (e.g., `Class`, `Interface`) |

---

## Built-in Collection/String Members

Hardcoded member access, resolved before type registry lookup.

| # | Target Type | Members | File:Line |
|---|-------------|---------|-----------|
| 1 | `IList` (any collection) | `Count`, `First`, `Last`, `Single`, `Tail` + flatten on unknown member | PredicateEvaluator.cs:824-850 |
| 2 | `string` | `Length`, `Lower`, `Upper`, `Normalized`, `Words` | PredicateEvaluator.cs:854-865 |
| 3 | Typed object | `Keys`, `Values`, `Count` (map-like access) | PredicateEvaluator.cs:875-882 |

---

## Built-in Collection Query Operators

All defined in PredicateEvaluator.cs:1038-1434. These are collection method calls (dot or colon syntax):

- **Filtering:** `Where`
- **Quantifiers:** `any`, `none`, `all`, `count`, `contains`, `containsAny`, `empty`
- **Element access:** `First`, `Last`, `Single`, `ElementAt`
- **Projection:** `Select`
- **Ordering:** `OrderBy`, `OrderByDescending`
- **Aggregation:** `Sum`, `Min`, `Max`, `Average`, `Reduce`
- **Grouping:** `Distinct`, `GroupBy`

---

## Built-in String/Object Predicates

Hardcoded predicate dispatch on values. All in PredicateEvaluator.cs:951-1006.

**String predicates:**
- `equals` / `eq`, `notEquals` / `ne`
- `startsWith` / `sw`, `endsWith` / `ew`
- `contains` / `ct`, `containsAny` / `ca`
- `matches` / `rx`, `sameAs` / `sm`
- `Trim`, `Replace`
- `in`, `empty`

**Numeric predicates:**
- `equals` / `eq`, `notEquals` / `ne`
- `greaterThan` / `gt`, `lessThan` / `lt`
- `greaterOrEqual` / `ge`, `lessOrEqual` / `le`
- `isSet`, `isClear`

**Object predicates:**
- `Get`, `containsKey`

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

## Language Filter & Bool Property Fallback

Implicit identifier resolution in `EvalIdentifier` that allows bare names to act as filters.

| # | What | File:Line | Behavior |
|---|------|-----------|----------|
| 1 | Language name as filter | PredicateEvaluator.cs:515-540 | Any bare identifier matching `File.Language` becomes a boolean filter (e.g., `Types:csharp`) |
| 2 | Bool property fallback | PredicateEvaluator.cs:573 | Bare identifier matching a bool property on the item's type returns its value (e.g., `Lines:isComment`) |

---

## Parser Magic

Syntax-level behavior that's not available to `.cop` declarations.

| # | What | File:Line | Behavior |
|---|------|-----------|----------|
| 1 | Action keyword recognition | ScriptParser.cs:610 | ALL-UPPERCASE identifiers followed by `(` are parsed as action invocations |
| 2 | `ASSERT_EMPTY` | ScriptParser.cs:907 | Variant of ASSERT that checks collection IS empty — not declared in core.cop |
| 3 | Implicit output | ScriptParser.cs:154-177 | Bare string/expression at top level → output command |
| 4 | `export` keyword scope | ScriptParser.cs:77-105 | Only works before type/collection/let/command/predicate/function/flags/enum |
| 5 | `Load(...)`, `Parse(...)` | ScriptParser.cs:529-586 | Recognized as special file parser bindings |

`PRINT`, `SAVE`, `DEBUG`, and `ASSERT` are now declared as `command = intrinsic` in `core.cop` and appear in reference.html.

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

## Recommendations

Remaining magic to consider eliminating:

1. **Enforce export filtering on imports** — only `export`-marked symbols should be available to importers (not just commands)
2. **Streaming source/sink bare-name fallback** — still present in TypeRegistry for streaming sources and sinks

Language-level features (Collection/String Members, Query Operators, Predicates, Language Filters) are appropriate to keep as built-in — they define the language itself and are documented in the language reference.
