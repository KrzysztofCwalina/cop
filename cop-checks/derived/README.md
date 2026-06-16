# Derived Cop self-checks

Run: `cop cop-checks/main.cop -t . -c DERIVED`

## Layering & general-purpose core
| Rule | Description |
|------|-------------|
| `language-no-code-model.cop` | The Cop language (`cop/shared`) must not reference the C# code model (`Cop.Providers.SourceModel`: `TypeDeclaration`, `StatementInfo`, …). A `.cop` program can analyze C#, Python, JSON or anything else, so the language only ever sees **generic** provider data — naming C#-specific types in the language would tie it to one domain. |
| `core-domain-purity.cop` | The language and runtime (`cop/shared`, `cop/runtime`) must not contain code-review/linting logic. Flags C# classes there named like `Analyzer`/`Linter`/`CodeSmell`/`CodeReview`, which suggest such logic has leaked into the engine instead of living in a `.cop` package like `code-analysis`. (A quick name-based heuristic; the AI `core-purity` check judges the same thing by meaning.) |
| `core-network-isolation.cop` | The evaluator (`cop/shared/interpreter`) and runtime (`cop/runtime`) must not make network calls (`System.Net*`/`HttpClient`). Running a program is offline; the only things that touch the network — the `http`/`ai` providers and package download — live outside the core. |
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
| `provider-namespace.cop` | Provider classes (the `DataProvider` subclasses) must live in the `Cop.Providers` namespace (or a sub-namespace) so every plugin shares one root. (The two TypeSpec providers are a known, allowlisted exception.) |
| `provider-sources-under-src.cop` | A provider's C# source files must live under `providers/<name>/src/` — the standard provider package layout that the build and packaging rely on. |
| `provider-isolation.cop` | A provider project must not reference *another* provider project — only the core `cop` project. Providers are independent plugins; referencing each other couples them and causes load-order and duplicate-assembly problems. |

## cop-checks authoring
| Rule | Description |
|------|-------------|
| `single-command-in-checks.cop` | Within `cop-checks/`, only `main.cop` may define a `command`. The suite is one program with a single entry point (`main.cop` defines `MAIN`/`AI`/`DERIVED`); every other file just exports its list of violations. |
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
| `target-framework.cop` | Every C# project must target `net10.0`. |
| `nullable-enabled.cop` | Every C# project must set `<Nullable>enable</Nullable>`. |
| `implicit-usings.cop` | Every C# project must set `<ImplicitUsings>enable</ImplicitUsings>`. |
| `single-target-framework.cop` | Projects use a single `<TargetFramework>`, never multi-targeting (`<TargetFrameworks>`). |

## Tests
| Rule | Description |
|------|-------------|
| `tests-location.cop` | NUnit test fixtures (`[TestFixture]` classes) must live under `tests/`. A fixture anywhere else won't be compiled or run by the test projects. |
| `test-class-naming.cop` | NUnit test fixtures (`[TestFixture]` classes) must be named `*Tests`. |
| `test-projects-use-nunit.cop` | Projects under `tests/` must reference the `NUnit` package — the repo's single test framework. |

## Filesystem invariants (run via `install/repo-invariants.ps1`)
| Rule | Description |
|------|-------------|
| provider references core privately | Provider `.csproj` reference `cop.csproj` with `<Private>false</Private>` (so a provider DLL doesn't ship its own copy of the cop assembly). |
| all projects in solution | Every `.csproj` is listed in `cop.sln`. |
| external providers ship `cop.json` | With `provider: clr` and `providerEntry`. |
