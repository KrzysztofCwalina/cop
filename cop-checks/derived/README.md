# Derived Cop self-checks

Run: `cop cop-checks/main.cop -t . -c DERIVED`

## Layering & general-purpose core
| Rule | Description |
|------|-------------|
| `language-no-code-model.cop` | The Cop language (`cop/shared`) must not import the C# code model (`Cop.Providers.SourceModel`). |
| `core-domain-purity.cop` | No code-review/linting class names (`Analyzer`/`Linter`/`CodeSmell`/`CodeReview`) in `cop/shared` or `cop/runtime`. |
| `core-network-isolation.cop` | No network I/O (`System.Net*`/`HttpClient`) in `cop/shared/interpreter` or `cop/runtime`. |
| `core-no-cli-dependency.cop` | `cop/shared` and `cop/runtime` must not reference the CLI (`Cop.Cli`/`Cop.Repl`). |
| `core-no-roslyn.cop` | `cop/shared` and `cop/runtime` must not depend on Roslyn (`Microsoft.CodeAnalysis`). |
| `commandline-confined-to-cli.cop` | Only `cop/cli` may use `System.CommandLine`. |
| `providers-no-cli-dependency.cop` | Provider plugins must not reference the CLI (`Cop.Cli`/`Cop.Repl`). |
| `interpreter-no-process-launch.cop` | The evaluator (`cop/shared/interpreter`) must not launch processes (`System.Diagnostics.Process`). |
| `no-console-in-pure-core.cop` | No `Console.*` in `cop/shared/{interpreter,ast,parser,tokenizer}` or `cop/runtime`. |

## Provider plugins
| Rule | Description |
|------|-------------|
| `provider-naming.cop` | Classes extending `DataProvider` must be named `*Provider`. |
| `provider-namespace.cop` | Provider classes live in the `Cop.Providers` namespace (TypeSpec providers allowlisted). |
| `provider-sources-under-src.cop` | A provider's C# source must live under `providers/<name>/src/`. |
| `provider-isolation.cop` | A provider project must not reference another provider project (only the core `cop`). |

## cop-checks authoring
| Rule | Description |
|------|-------------|
| `single-command-in-checks.cop` | Within `cop-checks/`, only `main.cop` may define a `command`. |
| `no-foreach-in-checks.cop` | Check files must report via `CHECK`, not `foreach`. |
| `ai-imports-only-in-ai-checks.cop` | Only `core-purity.cop` may `import ai`; the default suite stays deterministic and offline. |

## CLI
| Rule | Description |
|------|-------------|
| `cli-commands-are-static.cop` | CLI command classes (`cop/cli/commands/*Command`) must be `static`. |

## C# naming
| Rule | Description |
|------|-------------|
| `exception-naming.cop` | Types deriving from `Exception` must be named `*Exception`. |
| `exception-suffix-implies-base.cop` | Types named `*Exception` must derive from `Exception`. |

## C# safety
| Rule | Description |
|------|-------------|
| `no-async-void.cop` | No `async void` methods; use `async Task`. |
| `no-datetime-now.cop` | Use `DateTimeOffset`/`UtcNow`, not `DateTime.Now`. |
| `no-nullable-disable.cop` | No `#nullable disable` directives. |

## Project settings
| Rule | Description |
|------|-------------|
| `target-framework.cop` | Every C# project targets `net10.0`. |
| `nullable-enabled.cop` | Every C# project sets `<Nullable>enable</Nullable>`. |
| `implicit-usings.cop` | Every C# project sets `<ImplicitUsings>enable</ImplicitUsings>`. |
| `single-target-framework.cop` | A single `<TargetFramework>`, never `<TargetFrameworks>`. |

## Tests
| Rule | Description |
|------|-------------|
| `tests-location.cop` | NUnit `[TestFixture]` classes must live under `tests/`. |
| `test-class-naming.cop` | NUnit `[TestFixture]` classes must be named `*Tests`. |
| `test-projects-use-nunit.cop` | Projects under `tests/` must reference `NUnit`. |

## Filesystem invariants (run via `install/repo-invariants.ps1`)
| Rule | Description |
|------|-------------|
| provider references core privately | Provider `.csproj` reference `cop.csproj` with `<Private>false</Private>`. |
| all projects in solution | Every `.csproj` is listed in `cop.sln`. |
| external providers ship `cop.json` | With `provider: clr` and `providerEntry`. |
