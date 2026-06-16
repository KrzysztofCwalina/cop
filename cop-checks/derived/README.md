# Derived Cop self-checks

These checks codify **architecture and design decisions that are specific to the cop
codebase** — the kind of thing a newcomer wouldn't learn from standard .NET tooling. Run them
with:

```bash
cop cop-checks/main.cop -t . -c DERIVED    # the 23 rules below
cop cop-checks/main.cop -t . -c MAIN       # the original hand-written suite
cop verify cop-checks/                      # type-check every check file
```

`main.cop` builds a shared `codebase` and defines the `DERIVED` command; running it also loads
the `.cop` files in this `derived/` subfolder, so they all run as one program.

## Background: the codebase layers (read this first)

Cop is a static-analysis **language** — you write `.cop` programs and `cop.exe` runs them
against source code. The C# implementation is layered, and most rules here protect the
boundaries between those layers:

| Layer | Folder / namespace | What it is |
|-------|--------------------|------------|
| **Language** | `cop/shared` (`Cop.Lang`, `Cop.Core`) | The Cop language itself: tokenizer, parser, evaluator, type system. General-purpose — it knows nothing about any particular thing being analyzed. |
| **Runtime** | `cop/runtime` (`Cop.Providers`) | The engine that loads providers and executes a program. |
| **CLI** | `cop/cli` (`Cop.Cli`) | The `cop.exe` app: argument parsing, commands, REPL. |
| **Providers** | `providers/` | Plugins that feed data to programs (e.g. `csharp-provider` parses C#). Each extends the `DataProvider` base class. |
| **Packages** | `packages/` | `.cop` libraries (e.g. `code`, `code-analysis`). |

The single most important design rule: **the language and runtime stay general-purpose.**
Anything domain-specific — understanding C#, reviewing code for problems, reaching the network —
belongs in a provider or a `.cop` package, never baked into the core. Many checks below enforce
one edge of that boundary.

## Rules (23)

### Keeping the language & runtime general-purpose
| Check | What it enforces and why |
|-------|--------------------------|
| `language-no-code-model.cop` | The Cop language (`cop/shared`) must not reference the C# code model (`Cop.Providers.SourceModel`: `TypeDeclaration`, `StatementInfo`, …). A `.cop` program can analyze C#, Python, JSON or anything else, so the language only ever sees **generic** provider data — naming C#-specific types in the language would tie it to one domain. |
| `core-domain-purity.cop` | The language and runtime (`cop/shared`, `cop/runtime`) must not contain code-review/linting logic. Flags C# classes there named like `Analyzer`/`Linter`/`CodeSmell`/`CodeReview`, which suggest such logic has leaked into the engine instead of living in a `.cop` package like `code-analysis`. (A quick name-based heuristic; the AI `core-purity` check judges the same thing by meaning.) |
| `core-network-isolation.cop` | The evaluator (`cop/shared/interpreter`) and runtime (`cop/runtime`) must not make network calls (`System.Net*`/`HttpClient`). Running a program is offline; the only things that touch the network — the `http`/`ai` providers and package download — live outside the core. |
| `core-no-cli-dependency.cop` | The language and runtime (`cop/shared`, `cop/runtime`) must not reference the command-line app (`Cop.Cli`/`Cop.Repl`). They are a host-agnostic library; the CLI depends on them, never the other way around. |
| `core-no-roslyn.cop` | The language and runtime must not depend on Roslyn (`Microsoft.CodeAnalysis`). Understanding C# is the `csharp-provider`'s job — the core stays language-agnostic, with each source language handled by its own provider. |
| `commandline-confined-to-cli.cop` | Only the CLI app (`cop/cli`) may use `System.CommandLine`. Argument parsing is a CLI concern; the language, runtime, and providers must not pull it in. |
| `providers-no-cli-dependency.cop` | Provider plugins must not reference the CLI app (`Cop.Cli`/`Cop.Repl`). Providers are loaded *by* the runtime and sit below the CLI, so dependencies must not point upward into it. |
| `interpreter-no-process-launch.cop` | The evaluator (`cop/shared/interpreter`) must not start external processes (`System.Diagnostics.Process`). Evaluating an expression stays pure and in-process; running external tools is a provider's job (e.g. `tool-providers`). |
| `no-console-in-pure-core.cop` | Parsing/evaluation/runtime code (`cop/shared/{interpreter,ast,parser,tokenizer}`, `cop/runtime`) must not call `Console.*` directly. All output flows through cop's output layer (`cop/shared/output`) so it can be styled, redirected, or captured. |

### Provider plugin conventions
| Check | What it enforces and why |
|-------|--------------------------|
| `provider-naming.cop` | A C# class that extends `DataProvider` must be named `*Provider`. This is load-bearing, not style: the base class computes a provider's registered name by stripping the `Provider` suffix, so a misnamed class registers under the wrong name. |
| `provider-namespace.cop` | Provider classes (the `DataProvider` subclasses) must live in the `Cop.Providers` namespace (or a sub-namespace) so every plugin shares one root. (The two TypeSpec providers are a known, allowlisted exception.) |
| `provider-sources-under-src.cop` | A provider's C# source files must live under `providers/<name>/src/` — the standard provider package layout that the build and packaging rely on. |
| `provider-isolation.cop` | A provider project must not reference *another* provider project — only the core `cop` project. Providers are independent plugins; referencing each other couples them and causes load-order and duplicate-assembly problems. |

### How this self-check suite is written
| Check | What it enforces and why |
|-------|--------------------------|
| `single-command-in-checks.cop` | Within `cop-checks/`, only `main.cop` may define a `command`. The suite is one program with a single entry point (`main.cop` defines `MAIN`/`AI`/`DERIVED`); every other file just exports its list of violations. |
| `no-foreach-in-checks.cop` | Check files must report findings by passing a violation list to `CHECK`, never by hand-printing with `foreach`. `CHECK` gives consistent formatting and sets the process exit code. |
| `ai-imports-only-in-ai-checks.cop` | Only the designated AI check (`core-purity.cop`) may `import ai`. The default suite must stay deterministic and offline (no API key, no network, no cost); AI/LLM checks are opt-in and run only via the separate `AI` command. |

### CLI conventions
| Check | What it enforces and why |
|-------|--------------------------|
| `cli-commands-are-static.cop` | CLI command classes (`cop/cli/commands/*Command`) must be `static`. Each command is a stateless entry point with no instance data — the established pattern for this repo's CLI. |

### Per-project build settings
There is no central `Directory.Build.props` in this repo, so every `.csproj` sets these itself.
The checks stop a newly-added project from silently drifting from the rest.

| Check | What it enforces |
|-------|------------------|
| `target-framework.cop` | Every C# project must target `net10.0`. |
| `nullable-enabled.cop` | Every C# project must set `<Nullable>enable</Nullable>`. |
| `implicit-usings.cop` | Every C# project must set `<ImplicitUsings>enable</ImplicitUsings>`. |
| `single-target-framework.cop` | Projects use a single `<TargetFramework>`, never multi-targeting (`<TargetFrameworks>`). |

### Test layout
| Check | What it enforces and why |
|-------|--------------------------|
| `tests-location.cop` | NUnit test fixtures (`[TestFixture]` classes) must live under `tests/`. A fixture anywhere else won't be compiled or run by the test projects. |
| `test-projects-use-nunit.cop` | Projects under `tests/` must reference the `NUnit` package — the repo's single test framework. |

## Also implemented, as a script
Three invariants need to read `.sln`/`.csproj`/`cop.json` files, which the cop Codebase model
doesn't expose. They live in **`install/repo-invariants.ps1`** (`pwsh install/repo-invariants.ps1`,
non-zero exit on violation):

- **provider references core with `Private=false`** — provider `.csproj` reference `cop.csproj` with `<Private>false</Private>` (so a provider DLL doesn't ship its own copy of the cop assembly).
- **all projects in the solution** — every `.csproj` is listed in `cop.sln`.
- **external providers ship a `cop.json`** — with `provider: clr` and `providerEntry`.

## Intentionally *not* included
Generic .NET conventions that any analyzer or StyleCop already enforces are out of scope here —
this suite is for cop-specific architecture. Examples deliberately left out: exception/test-class
naming (`*Exception`, `*Tests`), `async void`, `DateTime.Now`, and `#nullable disable`.

## Deferred (proposed but not shippable yet, each with a concrete reason)

### Not actually a convention the repo follows (would fail on day one)
- **test-projects-flag** — no project sets `IsTestProject` (0 of 19), so there's nothing to codify.
- **predicate-camelcase** — the repo intentionally uses PascalCase predicates (`Clients`, `Models`,
  `PythonClients` in the check packages); casing is documented as deliberate
  (`docs/internal/language-critique.md` §3.1).
- **uppercase-commands** — packages/samples use non-uppercase commands (`Lines`, `PythonClients`).
- **async-method-suffix** — flags a real current case (`HttpSource`); needs a human decision
  (rename vs. allowlist), not a silent skip.

### The cop Codebase model can't express it reliably
- **package-sources-under-src** — `cop.parse()` excludes `packages/` `.cop` files from the codebase
  (0 of 465 `cb.Files`), so the rule can't see its targets.
- **file-scoped-namespaces** — file-scoped vs block `namespace` is syntactic; the model only exposes
  the `File.Namespace` string.
- **check-files-export-violations** — `cop.parse()` exposes let names but not export-ness, and the
  rule needs per-file grouping the query model doesn't support.
- **no-public-mutable-fields** — `Type.Fields` is unreliable here (flags `StatementInfo`, which has
  only properties).
- **single-quoted-strings** — only detectable via `Line.Text` containing `"`, which false-positives
  on single-quoted strings that embed JSON/quotes.
- **no-bare-block-comments** — bare `#` is used legitimately in `core-purity.cop`.

### Real exceptions exist; need triage, not suppression
- **no-blocking-async-in-core** — 3 current core uses (`Engine.cs`, `ProcessObjectProvider.cs`,
  `QueryFingerprint.cs`), some likely intentional.
- **no-hardcoded-abs-paths**, **code-model-record-naming** — low signal / underspecified.

## Notes for authors
All `cop-checks/**/*.cop` load into **one** program, so:
- use **unique predicate names** per file (prefix per rule, e.g. `pnExtendsDataProvider`);
- combine conditions by **chaining filters** (`codebase.Types:predA:predB`);
- inside `:any(...)` use a **named predicate** (an inline `&&` returns a collection, not a bool);
- `Project`-level rules `import code-layering` and use its `:toError(Project, …)`.
