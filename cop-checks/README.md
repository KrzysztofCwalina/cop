# Cop self-checks

Rules are grouped by the source they inspect:

- `cop/`: Cop declarations and check-authoring rules.
- `csharp/`: C# source and .NET project rules; `csharp/ai/` contains opt-in AI checks.
- `general/`: language-independent project dependency and layering rules.

The root `main.cop` defines the shared `codebase` and composes the default rules.
Run it with `cop cop-checks/main.cop -t .`; opt into AI checks with `-c AI`.
Group folders are not standalone programs. The separate package-source suite is
`cop cop-checks/cop/packages/main.cop -t packages`.

## Reading and maintaining a rule

Each file starts with a short description of what it actually detects. Predicates name
the scope, the offending fact, and any exemptions separately; the final filter chain
shows how those pieces combine. Longer expressions are split at meaningful concepts,
not into a helper for every comparison. Rule-local names stay distinct because sibling
files share one program-wide namespace.

The checks inspect parsed facts, but some are deliberately limited: import rules report
types in importing files, name rules are heuristics, and project-setting rules reject
nonblank invalid values without detecting empty or missing settings. Path matching and existing
allowlists are unchanged. Exported violation-list names remain stable for callers.

## Default and package-source rules

| Rule | Description |
|------|-------------|
| `cop/intrinsic-declaration-location.cop` | Runtime-backed Cop API declarations belong in files ending with `intrinsics.cop`. |
| `csharp/namespaces.cop` | C# types outside `tests/` and `samples/` must have a namespace. |
| `csharp/no-interfaces.cop` | Reject interfaces except `ICopCallable` and sample/example/test fixtures. |
| `general/runtime-no-provider-dependencies.cop` | Reject references from `runtime` to listed provider projects; `filesystem-provider` is allowed. |
| `csharp/cli-no-aspnet-framework.cop` | Keep ASP.NET framework references out of the single-file CLI publish project. |
| `csharp/no-new-ffi.cop` | FFI registrations require an approved first literal argument, with one narrowly scoped invocation-test exception. |
| `csharp/no-regex.cop` | Flag types in files importing regex unless their names identify approved regex infrastructure. |
| `csharp/no-environment.cop` | Reject unreviewed environment-variable access; allow secret names, PATH reads, installer PATH access, and reviewed frontend context reads. |
| `cop/packages/language-checks-require-parser.cop` | Reject unqualified `codebase(Program.Providers)` calls in known language-specific package paths; run through the package-source entry point. |

## Derived rule catalog

Derived rules live in each group's `derived/` subfolder. Unless stated otherwise
below, the catalog's filenames refer to `csharp/derived/`.

Run: `cop cop-checks/main.cop -t . -c DERIVED`

All self-checks use parsed model facts, not regex or source-text parsing. Verify the whole
suite with `cop verify cop-checks/ --strict-structure` (no text-check exceptions).
Names, paths, imports, project settings, and decoded literal values can still be compared
as strings. Calls use `CallTarget`/`ArgumentExpressions`; nullable directives use their
parsed `Kind` and `Keywords`. The environment check exempts secrets only by the actual
first string argument, never by a comment or unrelated text on the same line.

## Layering & general-purpose core
| Rule | Description |
|------|-------------|
| `language-no-code-model.cop` | The Cop language (`cop/shared`) must not reference the C# code model (`Cop.Providers.SourceModel`: `TypeDeclaration`, `StatementInfo`, …). A `.cop` program can analyze C#, Python, JSON or anything else, so the language only ever sees **generic** provider data — naming C#-specific types in the language would tie it to one domain. |
| `core-domain-purity.cop` | The language and runtime (`cop/shared`, `cop/runtime`) must not contain code-review/linting logic. Flags C# classes there named like `Analyzer`/`Linter`/`CodeSmell`/`CodeReview`, which suggest such logic has leaked into the engine instead of living in a `.cop` package like `code-analysis`. (A quick name-based heuristic; the AI `core-purity` check judges the same thing by meaning.) |
| `core-network-isolation.cop` | Flag types in `cop/shared/interpreter/` and `cop/runtime/` whose files import `System.Net`, `System.Net.Http`, or `System.Net.Sockets`. This import check supports the offline-core boundary; it does not detect every possible network call. |
| `core-no-cli-dependency.cop` | The language and runtime (`cop/shared`, `cop/runtime`) must not reference the command-line app (`Cop.Cli`/`Cop.Repl`). They are a host-agnostic library; the CLI depends on them, never the other way around. |
| `core-no-roslyn.cop` | The language and runtime must not depend on Roslyn (`Microsoft.CodeAnalysis`). Understanding C# is the `csharp-provider`'s job — the core stays language-agnostic, with each source language handled by its own provider. |
| `commandline-confined-to-cli.cop` | Only the CLI app (`cop/cli`) may use `System.CommandLine`. Argument parsing is a CLI concern; the language, runtime, and providers must not pull it in. |
| `providers-no-cli-dependency.cop` | Provider plugins must not reference the CLI app (`Cop.Cli`/`Cop.Repl`). Providers are loaded *by* the runtime and sit below the CLI, so dependencies must not point upward into it. |
| `interpreter-no-process-launch.cop` | The evaluator (`cop/shared/interpreter`) must not start external processes (`System.Diagnostics.Process`). Evaluating an expression stays pure and in-process; running external tools is a provider's job (e.g. `tool-providers`). |
| `no-console-in-pure-core.cop` | Parsing/evaluation/runtime code (`cop/shared/{interpreter,ast,parser,tokenizer}`, `cop/runtime`) must not call `Console.*` directly. All output flows through cop's output layer (`cop/shared/output`) so it can be styled, redirected, or captured. |

## Provider plugins
| Rule | Description |
|------|-------------|
| `provider-naming.cop` | A C# class that extends `DataProvider` must be named `*Provider`. This is load-bearing, not style: the base class computes a provider's registered name by stripping the `Provider` suffix, so a misnamed class registers under the wrong name. |
| `provider-namespace.cop` | Require types with a `DataProvider` base to have a namespace starting with `Cop.Providers`. This is a string-prefix test, not an exact namespace/subnamespace test. Two TypeSpec providers and sample/example files are exempt. |
| `provider-sources-under-src.cop` | A provider's C# source files must live under `providers/<name>/src/` — the standard provider package layout that the build and packaging rely on. |
| `general/derived/provider-isolation.cop` | Reject references between the listed provider projects. References to projects outside that list are not restricted by this rule; the intent is to keep provider plugins independent. |

## cop-checks authoring

These rules live under `cop/derived/`.

| Rule | Description |
|------|-------------|
| `commands-only-in-main.cop` | Within `cop-checks/`, only files ending with `main.cop` may define commands. The root entry point defines `MAIN`/`AI`/`DERIVED`; the package-source entry point defines its own `MAIN`. Individual rules export violation lists. |
| `no-foreach-in-checks.cop` | Check files must report findings by passing a violation list to `CHECK`, never by hand-printing with `foreach`. `CHECK` gives consistent formatting and sets the process exit code. |
| `ai-imports-only-in-ai-checks.cop` | Only the designated AI check (`core-purity.cop`) may `import ai`. The default suite must stay deterministic and offline (no API key, no network, no cost); AI/LLM checks are opt-in and run only via the separate `AI` command. |

## CLI
| Rule | Description |
|------|-------------|
| `cli-commands-are-static.cop` | CLI command classes (`cop/cli/commands/*Command`) must be `static`. Each command is a stateless entry point with no instance data — the established pattern for this repo's CLI. |

## C# naming
| Rule | Description |
|------|-------------|
| `exception-naming.cop` | Types deriving from `Exception` must be named `*Exception`. |
| `exception-suffix-implies-base.cop` | Conversely, a type named `*Exception` must actually derive from `Exception` (guards against a non-exception accidentally named that way). |

## C# safety
| Rule | Description |
|------|-------------|
| `no-async-void.cop` | No `async void` methods — they swallow exceptions and make failures unobservable in the async runtime; use `async Task`. |
| `no-datetime-now.cop` | Use `DateTimeOffset`/`UtcNow`, not `DateTime.Now` (locale/timezone-dependent and non-deterministic across machines). |
| `no-nullable-disable.cop` | No `#nullable disable` directives — nullable reference types stay on everywhere. |

## Project settings
| Rule | Description |
|------|-------------|
| `target-framework.cop` | Reject nonblank `TargetFramework` values other than `net10.0`; empty/missing values are not detected. |
| `nullable-enabled.cop` | Reject nonblank `Nullable` values other than `enable`; empty/missing values are not detected. |
| `implicit-usings.cop` | Reject nonblank `ImplicitUsings` values other than `enable`; empty/missing values are not detected. |
| `single-target-framework.cop` | Reject any parsed `TargetFrameworks` property, even with only one framework. Empty properties are omitted by the provider and cannot be detected. |

## Tests
| Rule | Description |
|------|-------------|
| `test-fixtures-under-tests.cop` | Types with a `TestFixture` decorator must live under `tests/`. |
| `test-fixture-naming.cop` | Types with a `TestFixture` decorator must be named `*Tests`. |
| `test-projects-use-nunit.cop` | Projects under `tests/` must reference the `NUnit` package — the repo's single test framework. |

## Filesystem invariants (run via `install/repo-invariants.ps1`)
| Rule | Description |
|------|-------------|
| provider references core privately | Provider `.csproj` reference `cop.csproj` with `<Private>false</Private>` (so a provider DLL doesn't ship its own copy of the cop assembly). |
| all projects in solution | Every `.csproj` is listed in `cop.sln`. |
| external providers ship `cop.json` | With `provider: clr` and `providerEntry`. |
