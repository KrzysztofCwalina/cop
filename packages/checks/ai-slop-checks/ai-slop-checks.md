---
name: ai-slop-checks
version: 1.0.0
title: AI Slop Checks
description: Deterministic AI-slop quality and operational safeguards for all standard source-language suites
authors: cop-team
tags: checks, quality, ai-slop, agentic-coding
dependencies: ['github.com/KrzysztofCwalina/cop/code: 1.2.0']
---

# AI Slop Checks

Shared quality checks are included in `csharp-checks`, `python-checks`,
`javascript-checks` (including TypeScript), `java-checks`, `go-checks`, and
`rust-checks`. They target the problems developers repeatedly report in
AI-assisted ("agentic") code: swallowed errors, hollow tests, suppressed
diagnostics, insecure string building, invented dependencies, and leftover
clutter. They detect concrete code patterns, not whether an AI wrote the code.
This package is a reusable library; it does not select a parser or run a command.

Call `aiSlopChecks(cb)` with a parsed codebase, or
`scopedCodeQualityChecks(production, tests, all)` with your own scopes.
`qualityProductionScope(cb)` and `qualityTestScope(cb)` build the standard
scopes. Every rule reads parsed facts (statement kinds, decoded literals,
decorators, imports, manifests, method shapes); none reconstructs syntax from
raw source text. Statement checks honor `cop-ignore`.
The four logging/deserialization/randomness rules are also exposed as
`securityChecks(statements)` for callers that want that subset separately;
the standard quality suite includes them.
`operationalSafetyChecks(production, tests)` exposes the thirty operational
safeguards below; it is also included in the standard quality suite.

## Browsing and namespace

Browse all packages in the [Package Reference](https://krzysztofcwalina.github.io/cop/reference.html).
Select **ai-slop-checks** under Checks for the API, rule descriptions and samples,
or run `cop help ai-slop-checks`. Every shared rule belongs to the `ai-slop-checks`
namespace; qualified calls such as `ai-slop-checks.constantAssertions(cb.Statements)`
make the source of a rule explicit.

`code-quality-checks` is a compatibility package: its existing exported functions
delegate here, including `codeQualityChecks(cb)`. Existing imports, check identifiers
and `cop-ignore` suppressions remain valid. Import the canonical package for new code.
The six standard language suites include this package automatically. Existing
language-specific checks such as Python eval and Rust unfinished-code checks stay in
their own packages to avoid duplicate reports; the tables below identify these cases.

## Shared rule catalog

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
| `interpolatedLogMessages` | production | Dynamically built messages passed to recognized logging APIs |
| `sensitiveDataInLogs` | production | Credential- or identity-named values passed to recognized logging APIs |
| `unsafeDeserialization` | production | Calls to known unsafe object deserializers; safe PyYAML loaders are exempt |
| `insecureRandomForSecrets` | production | Recognized non-cryptographic random calls assigned to secret-like names |
| `hardcodedLocalUrls` | production | URL string literals targeting localhost, loopback, or 0.0.0.0 |
| `ioInLoops` | production | Recognized database, HTTP, or filesystem operations nested in loop statements |
| `asyncWithoutAwait` | production | Async methods with no owned await statement or expression |
| `blockingCallsInAsync` | production | Selected blocking sleep, HTTP, or filesystem APIs inside async methods |
| `testScaffoldingInProduction` | production | Imports of recognized test and mocking frameworks |
| `speculativeAbstractions` | production | Pass-through methods and interfaces with exactly one parsed implementation |
| `securityChecks` | production | Aggregate of `interpolatedLogMessages`, `sensitiveDataInLogs`, `unsafeDeserialization`, and `insecureRandomForSecrets` |
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

## Operational safeguards (third research round)

These thirty additions were checked against both the shared library and the six
language suites before implementation. Existing eval, dynamic shell injection,
weak algorithms, constant-success assertions, swallowed exceptions, and async
without await checks remain their own rules; the additions target distinct forms.

| Function | Recognized forms / applicability |
|----------|----------------------------------|
| `certificateValidationDisabled` | Python requests/httpx `verify=False`, invalid-certificate opt-ins on Rust clients, explicit TLS bypass properties and .NET's accept-any validator |
| `missingRequestTimeout` | requests without a timeout; requests/httpx with explicit `timeout=None`. httpx's default timeout is not flagged |
| `unsafeArchiveExtraction` | Python tar extraction without `filter='data'` or an explicit member list; review signal |
| `insecureTemporaryNames` | Python `tempfile.mktemp` and recognized non-atomic temporary-name calls |
| `wildcardCorsOrigins` | .NET `AllowAnyOrigin`, wildcard origin headers, and Python CORS options; review signal |
| `developmentServerDebug` | Explicit `debug=True` on server run calls or literal `DEBUG=True` assignments |
| `jwtSignatureBypass` | Legacy Python jwt verification opt-out; JavaScript jsonwebtoken decode (not verify); review the trust boundary |
| `fastPasswordHashes` | SHA-2 applied to password-named arguments on recognized Python, .NET, Java and Go APIs; not general weak-crypto duplication |
| `insecureCookieFlags` | Explicit false Secure/HttpOnly flags on cookie setters or configuration properties |
| `worldWritableModes` | Literal 0666/0777 (including their decimal spellings) on recognized permission-changing APIs |
| `xmlEntityExpansion` | Explicit Python entity expansion, Java SAX external-entity feature enabling, or .NET DTD parsing |
| `dynamicCodeLoading` | Python exec, JavaScript Function/vm execution, .NET CSharpScript execution; existing eval checks stay in their language suites |
| `literalShellCommands` | Python subprocess shell execution with a literal command; dynamically constructed commands stay with `injectionProneStrings` |
| `unboundedRegex` | Static .NET Regex calls without an explicit timeout; review signal because an application-wide timeout may exist |
| `abruptFinally` | Return/throw and escaping loop jumps inside parsed finally blocks; languages with finally only |
| `redundantCatchRethrow` | A catch/except body consisting solely of rethrow; languages with exception handlers only |
| `constantBranchConditions` | Literal boolean if conditions; tested across all six languages |
| `selfComparisonConditions` | Direct identifier-to-itself comparison conditions; tested across all six languages |
| `selfAssignments` | Identifier assigned back to itself; tested across all six languages |
| `duplicateSourceImports` | Repeated identical imports, preserving distinct imported names, aliases and static/wildcard forms |
| `excessiveControlNesting` | First control statement at depth five; tested across all six languages; deeper descendants are not repeatedly reported |
| `sameValueAssertions` | Same-identifier equality assertions in NUnit/xUnit-style, unittest, Node assert, JUnit-style, Go testify and Rust macros |
| `productionAssertValidation` | Python production assert, which disappears under optimized execution |
| `asyncVoidMethods` | .NET async void; conventional two-parameter EventArgs handlers are exempt |
| `unsafeThreadStopping` | Recognized .NET/Java abrupt thread termination/suspension APIs |
| `forcedGarbageCollection` | Python, .NET, Java and Go manual collection calls; review signal, not a prohibition on measured GC tuning |
| `unboundedProcessWait` | Recognized parameterless .NET/Java/process waits; no proof that a child will hang |
| `publicListenerBinding` | Literal all-interface listener addresses or named host options; review signal for intentional public servers |
| `errorMessageControlFlow` | Direct message-property/string-literal comparisons in branch conditions; no exception data-flow inference |
| `legacyTlsProtocols` | Explicit SSL2/SSL3/TLS1.0/TLS1.1 constants on recognized configuration expressions |

All thirty are composed into every standard language suite, but this is **not**
thirty universally applicable rules per language. Go/Rust have no finally or
catch clauses, Rust has no garbage collector, and async void and static Regex
timeouts are .NET-specific. API rules recognize the listed forms, not every
library, alias, wrapper or fluent configuration. Optional compiler-backed paths
project the new structural facts too; native-toolchain runtime coverage depends
on which toolchains are installed.

No rule proves that AI authored code or that a review finding is exploitable.
Literal defaults and explicit safe alternatives are tested. A validated archive
member list is not itself proven safe; Python 3.14 also changed tar extraction's
default filter. Self-comparisons may deliberately test NaN, message properties may
belong to non-exceptions, and API-name/assignment-name matching does not establish
symbol identity or data flow. Suppression remains available for intentional use.

### Research evidence and selection

This is a recurring-complaint synthesis, **not a measured popularity ranking of
thirty individual rules**. Evidence was cross-checked across several kinds of
sources rather than treating a single post or catalog as proof:

| Evidence | What it supports | Limitation |
|----------|------------------|------------|
| [CodeRabbit's AI-vs-human PR report](https://www.coderabbit.ai/blog/state-of-ai-vs-human-code-generation-report) | Repeated logic, error-handling, readability, security and performance findings across 470 reviewed PRs | Vendor study; inferred AI labels, unequal groups, not thirty-rule frequency counts |
| [Sonar authors' Java code-quality study](https://arxiv.org/html/2508.14727) | Static-quality problems persist despite functional correctness across 4,442 assignments and five models | Benchmark-generated Java, not production agent PRs or a complaint survey |
| [Agent field notes](https://github.com/KbWen/agent-field-notes) | Concrete reports of unsafe configuration, broad permissions and false-success behavior | Small anecdotal corpus, not prevalence evidence |
| [AI-slop rule catalog](https://github.com/scanaislop/aislop/blob/main/docs/rules.md) | Independent selection of redundant catches, constant conditions, repeated imports, nesting and async-void problems | Curated linter catalog, not a frequency study |
| [Ruff request timeout rule](https://docs.astral.sh/ruff/rules/request-without-timeout/) | requests/httpx timeout semantics and safe alternatives | API-specific authority, not AI-specific complaint counts |
| [Ruff archive rule](https://docs.astral.sh/ruff/rules/tarfile-unsafe-members/), [finally rule](https://docs.astral.sh/ruff/rules/jump-statement-in-finally/), [TLS defaults](https://docs.astral.sh/ruff/rules/ssl-with-bad-defaults/) | Concrete extraction, masked-outcome and obsolete-protocol hazards | Runtime defaults and applicability must be considered |
| [.NET XML guidance](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca3075) | Explicit XML entity/DTD configuration risks | Security guidance, not agent authorship evidence |

The selection prioritizes operational impact, useful safe alternatives, parsed
facts available in Cop, and non-overlap with existing checks. The broad complaint
clusters are corroborated; sources do not establish that each individual API
pattern occurs frequently in AI-generated production code.

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
- Several new rules intentionally use bounded API-name heuristics, not data flow:
  `unsafeDeserialization` recognizes selected Python, Java, and .NET APIs;
  `insecureRandomForSecrets` recognizes parsed call names and secret-like assignments
  when the language parser exposes the assignment value; simple identifier assignments
  are exposed by all six parsers, but aliases, indirect assignments and data flow are
  not resolved;
  `ioInLoops` recognizes common APIs and explicit loop ancestry, not callback-based
  collection iteration; and `blockingCallsInAsync` currently covers selected Python,
  JavaScript, and Rust APIs.
- `asyncWithoutAwait` currently runs only for C#. It includes nested statements and
  returned, initializer, and conditional await expressions, but excludes awaits owned by
  local functions or lambdas. Python does not provide a reliable parsed await fact.
- `speculativeAbstractions` is a review signal: a single implementation does not prove an
  interface is unnecessary, and the rule does not infer external implementations.
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

## Additional AI-slop checks

These are deterministic pattern checks, not claims that AI authored the code. Their
coverage is intentionally limited to parsed API names, call shapes, expressions, and
nesting available in the shared code model; they do not perform taint analysis or prove
that a value is a secret, untrusted, or production-only.