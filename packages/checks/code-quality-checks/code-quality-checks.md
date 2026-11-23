---
name: code-quality-checks
version: 1.0.0
title: Code Quality Checks
description: Shared deterministic quality checks for the standard source-language suites
authors: cop-team
tags: checks, quality
dependencies: ['github.com/KrzysztofCwalina/cop/code: 1.2.0']
---

# Code Quality Checks

Shared quality checks are included in `csharp-checks`, `python-checks`,
`javascript-checks` (including TypeScript), `java-checks`, `go-checks`, and
`rust-checks`. They target the problems developers repeatedly report in
AI-assisted ("agentic") code: swallowed errors, hollow tests, suppressed
diagnostics, insecure string building, invented dependencies, and leftover
clutter. They detect concrete code patterns, not whether an AI wrote the code.
This package is a reusable library; it does not select a parser or run a command.

Call `codeQualityChecks(cb)` with a parsed codebase, or
`scopedCodeQualityChecks(production, tests, all)` with your own scopes.
`qualityProductionScope(cb)` and `qualityTestScope(cb)` build the standard
scopes. Every rule reads parsed facts (statement kinds, decoded literals,
decorators, imports, manifests, method shapes); none reconstructs syntax from
raw source text. Statement checks honor `cop-ignore`.

| Function | Scope | Detection |
|----------|-------|-----------|
| `emptyErrorHandlers` | production | Specific-type catch/except with no parsed body statements (catch-alls and empty Python handlers are reported by the language suites) |
| `silentLiteralFallbacks` | production | Specific-type handler whose entire body returns a scalar literal (catch-alls are reported by the language suites; `Try*`, `*OrDefault`/`*OrNull`/`*OrNone` methods are exempt because their name declares the fallback) |
| `unfinishedImplementations` | production | NotImplemented exceptions; Rust `todo!`/`unimplemented!` are reported by rust-checks `unfinished-code` |
| `constantAssertions` | all | Assertions given an unconditional success literal |
| `disabledTests` | tests | Ignore/Disabled/skip decorators, `it.skip`/`xit`, unconditional `t.Skip`/`pytest.skip` |
| `assertionFreeTests` | tests | Tests with no assert/expect/verify/fail call (custom `assert*`/`expect*`/`verify*`/`check*`/`should*` helpers count) |
| `existenceOnlyAssertions` | tests | Tests whose only assertions are NotNull/toBeDefined/toBeTruthy-style |
| `mockedTestSubjects` | tests | Test mocks the type/module it is named after (Moq, Mockito, Python Mock spec/autospec, `jest.mock`) |
| `typeErasingCasts` | production | C# `(dynamic)`, TS `as any`, Python `cast(Any, ...)` |
| `blanketWarningSuppressions` | all | Bare `#pragma warning disable`, `# type: ignore`, `# noqa`, `pylint: disable=all`, rule-less `eslint-disable`, `@ts-ignore`/`@ts-nocheck`, `//nolint`, `#[allow(warnings)]` |
| `credentialLiterals` | production | Nonempty literal assigned to a credential-named variable |
| `weakCryptoCalls` | production | MD5, SHA-1, DES/3DES, RC2/RC4, Blowfish, ECB mode |
| `injectionProneStrings` | production | SQL, shell, and raw-HTML sinks (`eval` is reported by the JavaScript and Python suites) given concatenated, interpolated, or formatted text |
| `undeclaredDependencies` | production | Third-party imports missing from `package.json`, `go.mod`, or `Cargo.toml` |
| `deprecatedCalls` | production | Calls to `[Obsolete]`/`@Deprecated`/`@deprecated` declarations |
| `blockingTestSleeps` | tests | Fixed sleeps/delays in tests |
| `fixedDelays` | production | Hard-coded sleeps/delays with literal durations (`time.sleep`, `asyncio.sleep`, Java `Thread.sleep`, Rust `thread::sleep`, `Task.Delay`); C# `Thread.Sleep` and Go `time.Sleep` are reported by their language suites |
| `debuggerLeftovers` | production | `Debugger.Break`/`Launch`, `pdb.set_trace`/`breakpoint` |
| `overCommentedMethods` | production | Methods of 10+ lines where 40%+ of lines are comments |
| `emojiInCode` | production | Emoji in identifiers and string literals |
| `clutterNames` | production | Types/methods named after an edit: `FooFixed`, `BarEnhanced`, `run_improved` (`IsFixed` is allowed) |
| `backupSourceFiles` | production | Source files named `_copy`, `.bak`, `.orig`, `.old` |
| `agentReportFiles` | repository | Agent report markdown such as `IMPLEMENTATION_SUMMARY.md`, `FIX_NOTES.md` |
| `duplicateMethodBodies` | production | Methods (6+ statements) with identical parsed body structure, including nested statements, conditions, called members, and literal/operator expressions; no semantic binding is required |

Production scope excludes test, sample, example, fixture, vendor,
node_modules, and generated directories, conventional test filenames, and
recognized test methods.

## Language coverage

A rule runs only where its concept and parsed facts exist:

- Error-handler rules apply to C#, Python, JavaScript/TypeScript, and Java;
  Go and Rust have no catch clauses. Java's source-only parser does not expose
  handler bodies with local declarations, so such handlers report as
  `emptyErrorHandlers`.
- `undeclaredDependencies` runs for JavaScript/TypeScript, Go, and Rust. C#,
  Python, and Java imports name namespaces/modules, not packages, and no
  namespace-to-package mapping exists in the model.
- `existenceOnlyAssertions` does not apply to JavaScript, where `it(...)`
  callbacks are not modeled as methods.
- The Rust parser skips inline `#[cfg(test)] mod tests`, so Rust test rules
  apply to `tests/*.rs` integration tests.
- `mockedTestSubjects` supports Moq, Mockito, Python `unittest.mock`, and Jest;
  NSubstitute, Go, and Rust mocking libraries are not modeled.
- Rules each language suite already owns (console output, `dbg!`, `printStackTrace`,
  catch-all handlers that swallow errors, empty Python handlers, Rust `todo!`/`unimplemented!`,
  `eval`, C# `Thread.Sleep`, Go `time.Sleep`)
  are not repeated here, so a line is reported once.

## Disposition of the research proposals

| Research proposal | Disposition |
|-------------------|-------------|
| 1 Swallowed exceptions | `emptyErrorHandlers` (specific types); language suites own catch-alls and empty Python handlers |
| 2 Catch-all without rethrow | Language suites: C# `swallowed-exceptions`, Python/JavaScript swallowed-exception rules, Java `swallowed-exceptions` |
| 3 Silent fallback returns | `silentLiteralFallbacks` (specific types); catch-alls are covered by language suites |
| 4 Checker suppressions | `blanketWarningSuppressions` (all six languages; rule-specific suppressions allowed) |
| 5 Any casts | `typeErasingCasts` |
| 6 Skipped tests | `disabledTests` |
| 7 Tautological assertions | `constantAssertions`, `existenceOnlyAssertions` |
| 8 Tests must assert | `assertionFreeTests` |
| 9 Mocking the subject | `mockedTestSubjects` |
| 10 Stub implementations | `unfinishedImplementations`; Rust macros via rust-checks `unfinished-code` |
| 11 Hardcoded secrets | `credentialLiterals` (review signal, not a secret scanner) |
| 12 Weak crypto | `weakCryptoCalls` |
| 13 SQL/HTML/shell injection | `injectionProneStrings` (sink + built-string shape; no data-flow analysis; log injection not covered) |
| 14 Hallucinated dependencies | `undeclaredDependencies` for manifest-backed languages; registry existence and pinning are **not** checked (no offline registry facts) |
| 15 Obsolete APIs | `deprecatedCalls` |
| 16 Sleep as synchronization | `blockingTestSleeps`, `fixedDelays` |
| 17 Comment narration | `overCommentedMethods` (density only; narration itself is not detectable) |
| 18 Emoji/decorative glyphs | `emojiInCode` for identifiers and strings; comment emoji are **not** checked because comments are raw text |
| 19 Clutter artifacts | `clutterNames`, `backupSourceFiles`, `agentReportFiles`, `debuggerLeftovers`; console/debug printing stays in each language suite |
| 20 Near-duplicate methods | `duplicateMethodBodies` (exact structural duplicates, not fuzzy similarity) |
| A–D Change-aware rules (weakened tests, scope creep, unrequested renames, deleted-instead-of-fixed) | **Not implemented**: they need a parsed model of the base revision and task intent, which the change provider does not expose |