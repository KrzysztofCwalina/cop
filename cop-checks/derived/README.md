# Derived Cop Self-Checks

This folder holds **derived** architectural/design checks for the cop repo — conventions the
codebase already follows, codified so future contributors can't silently break them.

**28 of the ~50 proposed rules ship here** (plus 3 more as a script — see below). The rest are
deferred — each for a concrete, verified technical reason (cop-model limitations, or because the
repo doesn't actually follow the convention), documented in **Deferred** below. None were skipped
merely for being "more work."

## Running

```bash
cop cop-checks/main.cop -t . -c DERIVED     # run only the derived suite
cop cop-checks/main.cop -t . -c MAIN        # run the original curated suite
cop verify cop-checks/                       # verify all check files
```

`main.cop` builds the shared `codebase` and exposes `command DERIVED`. Running `main.cop`
loads `.cop` files in subfolders too, so files in this `derived/` folder join the same program.

## Authoring conventions (important, learned the hard way)

All `cop-checks/**/*.cop` load into **one** program, so:
- **Unique predicate names** per file (prefix per rule, e.g. `pnExtendsDataProvider`).
- Each file exports one `export let derived-<rule> = … :toError(...)`; never declare a `command` here.
- **AND = chained filters** at the call site (`codebase.Types:predA:predB`).
- Inside `:any(...)` use a **named predicate** — inline `:any(a && b)` returns a collection, not a bool.
- **Project**-level rules `import code-layering` and use its `:toError(Project, …)` (base
  `code-analysis` `toError` only supports `Statement`/`Type`/`Line`/`Region`/`Api`/`Folder`).

## Rules (28)

### Architecture & layering
| File | Enforces |
|------|----------|
| `core-domain-purity.cop` | No code-analysis domain type names (`Analyzer`/`Linter`/`CodeSmell`/`CodeReview`) in `cop/shared` or `cop/runtime` (static complement to the AI core-purity check). |
| `core-network-isolation.cop` | The evaluator (`cop/shared/interpreter`) and `cop/runtime` must not use `System.Net*`/`HttpClient`. |
| `core-no-cli-dependency.cop` | `cop/shared` must not depend on the CLI host (`Cop.Cli.Commands`/`Cop.Repl`). |
| `core-no-roslyn.cop` | `cop/shared`/`cop/runtime` must not depend on Roslyn (`Microsoft.CodeAnalysis*`). |
| `commandline-confined-to-cli.cop` | `System.CommandLine` is used only under `cop/cli`. |
| `providers-no-cli-dependency.cop` | Providers must not depend on the CLI host. |
| `interpreter-no-process-launch.cop` | The evaluator must not launch processes (`System.Diagnostics.Process`). |
| `no-console-in-pure-core.cop` | No `Console.*` in the pure core (interpreter/ast/parser/tokenizer/runtime). |
| `ai-imports-only-in-ai-checks.cop` | Only the designated AI check (`core-purity.cop`) may `import ai`; the default suite stays deterministic/offline. |

### Naming
| File | Enforces |
|------|----------|
| `provider-naming.cop` | Classes extending `DataProvider` are named `*Provider`. |
| `exception-naming.cop` | Types deriving from `Exception` are named `*Exception`. |
| `exception-suffix-implies-base.cop` | Types named `*Exception` actually derive from `Exception`. |
| `test-class-naming.cop` | NUnit `[TestFixture]` types are named `*Tests`. |

### Type design & safety
| File | Enforces |
|------|----------|
| `no-async-void.cop` | No `async void` methods. |
| `no-nullable-disable.cop` | No `#nullable disable` directives. |
| `no-datetime-now.cop` | Use `DateTimeOffset`/`UtcNow`, not `DateTime.Now`. |
| `cli-commands-are-static.cop` | CLI `*Command` classes are `static`. |

### Project configuration
| File | Enforces |
|------|----------|
| `target-framework.cop` | Every project targets `net10.0`. |
| `nullable-enabled.cop` | Every project sets `<Nullable>enable</Nullable>`. |
| `implicit-usings.cop` | Every project sets `<ImplicitUsings>enable</ImplicitUsings>`. |
| `single-target-framework.cop` | Single `TargetFramework`, no multi-targeting. |

### Structure & providers
| File | Enforces |
|------|----------|
| `provider-sources-under-src.cop` | Provider source files live under `providers/<name>/src/`. |
| `tests-location.cop` | NUnit test fixtures live under `tests/`. |
| `provider-namespace.cop` | Provider classes live in the `Cop.Providers` namespace (TypeSpec providers allowlisted). |
| `provider-isolation.cop` | Provider projects do not reference other provider projects. |
| `test-projects-use-nunit.cop` | Test projects under `tests/` reference NUnit. |

### cop-checks authoring
| File | Enforces |
|------|----------|
| `single-command-in-checks.cop` | Only `cop-checks/main.cop` declares a `command`. |
| `no-foreach-in-checks.cop` | cop-checks never use `foreach` to emit output — always `CHECK`. |

## Deferred (with concrete reasons)

### Not actually a convention the repo follows (would fail immediately)
- **test-projects-flag** — **no** project sets `IsTestProject` (0 of 19), so there's nothing to codify.
- **predicate-camelcase** — the repo intentionally uses PascalCase predicates (`Clients`,
  `Models`, `PythonClients` in the check packages); casing is documented as deliberate
  (`docs/internal/language-critique.md` §3.1).
- **uppercase-commands** — packages/samples use non-uppercase commands (`Lines`, `PythonClients`).
- **async-method-suffix** — flags a real current case (`HttpSource`); needs a human decision
  (rename vs. allowlist), not a silent skip.

### The cop Codebase model can't express it reliably
- **package-sources-under-src** — `cop.parse()` **excludes `packages/` `.cop` files** from the
  codebase (verified: 0 of 465 `cb.Files` are under `packages/`; an injected stray file is not
  seen), so the rule can't see its targets.
- **file-scoped-namespaces** — file-scoped vs block `namespace` is syntactic; the semantic model
  only exposes the `File.Namespace` string.
- **check-files-export-violations** — `cop.parse()` exposes let names but **not** export-ness, and
  the rule needs per-file grouping the query model doesn't support.
- **no-public-mutable-fields** — `Type.Fields` is unreliable here (flags `StatementInfo`, which has
  only properties; `.Fields`/`.Properties` render empty).
- **single-quoted-strings** — only detectable via `Line.Text` containing `"`, which false-positives
  on single-quoted strings that embed JSON/quotes (`ai.cop`, `code-metrics.cop`).
- **no-bare-block-comments** — bare `#` is used legitimately in `core-purity.cop` (lines 2/8/12).

### Real exceptions exist; need triage, not suppression
- **no-blocking-async-in-core** — 3 current core uses (`Engine.cs`, `ProcessObjectProvider.cs`,
  `QueryFingerprint.cs`), some likely intentional.
- **no-hardcoded-abs-paths**, **code-model-record-naming** — low signal / underspecified.

### Implemented as a script (not expressible as a `.cop` check)
The following require parsing `.sln`/`.csproj` XML/`cop.json`, which the `Codebase` model
doesn't surface. They are implemented in **`install/repo-invariants.ps1`** (run `pwsh
install/repo-invariants.ps1`; exits non-zero on violation):
- **provider-references-core-private-false** — provider `.csproj` reference `cop.csproj` with `<Private>false</Private>`.
- **all-projects-in-solution** — every `.csproj` is in `cop.sln` (this surfaced 10 projects that were missing; they were added with `dotnet sln add`).
- **external-provider-has-cop-json** — every external provider package ships a `cop.json` with `provider: clr` + `providerEntry`.
