# Samples

All samples in this repository pass `cop verify` (syntax + binding validation).

The standalone samples live under `samples/`, organized into two categories:

- **`samples/language/`** — a tour of Cop language features.
- **`samples/static-analysis/`** — common, real-world static-analysis checks.

Each sample folder is self-contained: a `.cop` file plus a small source fixture.
Run one from its folder with `cop <file>.cop -t .`, where `-t` is the target
directory whose code is analyzed.

## Language Samples

Located in `samples/language/`.

| Sample | Description |
|--------|-------------|
| [hello-world](../samples/language/hello-world/hello-world.cop) | Your first program: import, predicate, filter, print |
| [strings](../samples/language/strings/strings.cop) | String literals, triple-quoted text, and styled output |
| [filtering](../samples/language/filtering/filtering.cop) | Narrowing collections: `:` filter, `!` negate, property predicates, `+` |
| [predicates](../samples/language/predicates/predicates.cop) | Defining and composing predicates with `&&` and `:any()` |
| [transforms](../samples/language/transforms/transforms.cop) | `.Where` / `.Select` / `.text` / `.First` / `.Count` |
| [commands](../samples/language/commands/commands.cop) | Named, runnable commands selected with `-c` |
| [json](../samples/language/json/json.cop) | Typed schemas over JSON data with `json.Parse` |

## Static-Analysis Samples

Located in `samples/static-analysis/`. These show common checks — the kind you
enforce in CI. The check shape is always a predicate → `:toError` / `:toWarning`
/ `:toInfo` → `CHECK(...)`. Several rules are modeled on real analyzer/lint rules
(StyleCop, Azure SDK analyzers, ESLint, Pylint).

| Sample | Description |
|--------|-------------|
| [no-var](../samples/static-analysis/no-var/no-var.cop) | Flag C# `var` usage (the simplest possible check) |
| [todo-comments](../samples/static-analysis/todo-comments/todo-comments.cop) | Flag unresolved TODO / FIXME comments |
| [naming-conventions](../samples/static-analysis/naming-conventions/naming-conventions.cop) | Interface `I` prefix, `*Exception` and `*Async` suffixes |
| [sealed-types](../samples/static-analysis/sealed-types/sealed-types.cop) | Public client types should be sealed |
| [no-public-fields](../samples/static-analysis/no-public-fields/no-public-fields.cop) | Public types must not expose public fields |
| [documented-apis](../samples/static-analysis/documented-apis/documented-apis.cop) | Public types must be documented |
| [method-length](../samples/static-analysis/method-length/method-length.cop) | Flag methods with too many statements |
| [banned-calls](../samples/static-analysis/banned-calls/banned-calls.cop) | Forbid Console output / blocking `Thread.Sleep` in libraries |
| [csharp-specific-ast](../samples/static-analysis/csharp-specific-ast/csharp-specific-ast.cop) | Downcast with `:asCSharp` for records, partial, and lock |
| [cross-language](../samples/static-analysis/cross-language/cross-language.cop) | One model across C#, Python, and JS/TS |
| [typespec-http](../samples/static-analysis/typespec-http/typespec-http.cop) | Domain check over a TypeSpec / HTTP API spec |
| [slop](../samples/static-analysis/slop-metrics/slop.cop) | Score a codebase with `METRICS` |
| [custom-slop](../samples/static-analysis/slop-metrics/custom-slop.cop) | Extend the built-in slop rule set with a custom rule |

## Extension Examples

Authoring a custom data provider in C# is shown under `examples/providers/`.

| Example | Description |
|---------|-------------|
| [sample-provider](../examples/providers/sample-provider/) | A custom provider package (see [use-widgets.cop](../examples/providers/sample-provider/package/samples/use-widgets.cop) and [sample.cop](../examples/providers/sample-provider/package/src/sample.cop)) |
| [streaming-provider](../examples/providers/streaming-provider/) | A streaming source/sink provider (see [ticker.cop](../examples/providers/streaming-provider/ticker/src/ticker.cop)) |

## Package Samples

Each package includes samples in its `samples/` directory showing real-world usage.

### Core

| Sample | Description |
|--------|-------------|
| [hello-world](../packages/core/samples/hello-world.cop) | Minimal hello world |
| [variables-and-arithmetic](../packages/core/samples/variables-and-arithmetic.cop) | Variables and arithmetic operations |
| [string-and-collection-operations](../packages/core/samples/string-and-collection-operations.cop) | String and collection operations |

### Code Analysis

| Sample | Description |
|--------|-------------|
| [count-by-language](../packages/code/samples/count-by-language.cop) | Count source files by language |
| [find-long-methods](../packages/code/samples/find-long-methods.cop) | Find methods with too many statements |
| [list-all-public-classes](../packages/code/samples/list-all-public-classes.cop) | List all public classes |
| [create-a-simple-check](../packages/code-analysis/samples/create-a-simple-check.cop) | Create a simple CHECK violation |
| [compute-metrics](../packages/code-metrics/samples/compute-metrics.cop) | Compute aggregate slop metrics as JSON |
| [exclude-generated](../packages/code-metrics/samples/exclude-generated.cop) | Compute metrics excluding generated code |
| [custom-slop](../packages/code-metrics/samples/custom-slop.cop) | Customize slop rules and weights |
| [enforce-layering-rules](../packages/code-layering/samples/enforce-layering-rules.cop) | Enforce dependency layering rules |

### C# (.NET)

| Sample | Description |
|--------|-------------|
| [list-public-types](../packages/dotnet/csharp/samples/list-public-types.cop) | List all public C# types |
| [run-csharp-style-checks](../packages/dotnet/csharp-checks/samples/run-csharp-style-checks.cop) | Run built-in C# style checks |
| [check-client-conventions](../packages/dotnet/csharp-library-checks/samples/check-client-conventions.cop) | Check SDK client library conventions |
| [run-azure-sdk-checks](../packages/dotnet/csharp-library-azure-checks/samples/run-azure-sdk-checks.cop) | Run Azure SDK-specific checks |
| [api-listing](../packages/dotnet/csharp-lister/samples/api-listing.cop) | Generate API listing |
| [api-diff](../packages/dotnet/csharp-lister/samples/api-diff.cop) | Generate API diff |
| [run-format](../packages/dotnet/csharp-format/samples/run-format.cop) | Run dotnet format checks |
| [run-stylecop](../packages/dotnet/csharp-stylecop/samples/run-stylecop.cop) | Run StyleCop analysis |
| [run-snippet-checks](../packages/dotnet/csharp-snippets-checks/samples/run-snippet-checks.cop) | Verify C# snippet sync with docs |

### JavaScript / TypeScript

| Sample | Description |
|--------|-------------|
| [list-public-types](../packages/js/javascript/samples/list-public-types.cop) | List all public JS/TS types |
| [run-js-ts-checks](../packages/js/javascript-checks/samples/run-js-ts-checks.cop) | Run built-in JS/TS style checks |
| [check-client-conventions](../packages/js/javascript-library-checks/samples/check-client-conventions.cop) | Check SDK client library conventions |
| [run-azure-sdk-checks](../packages/js/javascript-library-azure-checks/samples/run-azure-sdk-checks.cop) | Run Azure SDK-specific checks |
| [api-listing](../packages/js/javascript-lister/samples/api-listing.cop) | Generate API listing |
| [api-diff](../packages/js/javascript-lister/samples/api-diff.cop) | Generate API diff |
| [run-biome](../packages/js/javascript-biome/samples/run-biome.cop) | Run Biome linting |
| [run-eslint](../packages/js/javascript-eslint/samples/run-eslint.cop) | Run ESLint checks |
| [run-tsc](../packages/js/typescript-tsc/samples/run-tsc.cop) | Run TypeScript compiler checks |
| [run-snippet-checks](../packages/js/javascript-snippets-checks/samples/run-snippet-checks.cop) | Verify JS snippet sync with docs |

### Python

| Sample | Description |
|--------|-------------|
| [list-public-types](../packages/python/python/samples/list-public-types.cop) | List all public Python types |
| [run-python-style-checks](../packages/python/python-checks/samples/run-python-style-checks.cop) | Run built-in Python style checks |
| [check-client-conventions](../packages/python/python-library-checks/samples/check-client-conventions.cop) | Check SDK client library conventions |
| [run-azure-sdk-checks](../packages/python/python-library-azure-checks/samples/run-azure-sdk-checks.cop) | Run Azure SDK-specific checks |
| [api-listing](../packages/python/python-lister/samples/api-listing.cop) | Generate API listing |
| [api-diff](../packages/python/python-lister/samples/api-diff.cop) | Generate API diff |
| [run-bandit](../packages/python/python-bandit/samples/run-bandit.cop) | Run Bandit security scanner |
| [run-mypy](../packages/python/python-mypy/samples/run-mypy.cop) | Run mypy type checker |
| [run-pylint](../packages/python/python-pylint/samples/run-pylint.cop) | Run Pylint checks |
| [run-ruff](../packages/python/python-ruff/samples/run-ruff.cop) | Run Ruff linter |
| [run-snippet-checks](../packages/python/python-snippets-checks/samples/run-snippet-checks.cop) | Verify Python snippet sync with docs |

### Java

| Sample | Description |
|--------|-------------|
| [run-java-checks](../packages/java/java-checks/samples/run-java-checks.cop) | Run built-in Java style checks |

### Rust

| Sample | Description |
|--------|-------------|
| [list-public-types](../packages/rust/rust/samples/list-public-types.cop) | List all public Rust types |
| [run-rust-checks](../packages/rust/rust-checks/samples/run-rust-checks.cop) | Run built-in Rust correctness and style checks |

### Specialized

| Sample | Description |
|--------|-------------|
| [find-files](../packages/files/samples/find-files.cop) | Find empty folders and large files |
| [load-and-query-json-data](../packages/json/samples/load-and-query-json-data.cop) | Load and query JSON data |
| [check-for-broken-links](../packages/markdown/samples/check-for-broken-links.cop) | Check for broken links in markdown |
| [list-all-models](../packages/typespec/samples/list-all-models.cop) | List all TypeSpec models |
| [find-get-operations](../packages/typespec-http/samples/find-get-operations.cop) | Find HTTP GET operations |
| [http-server](../packages/http/samples/http-server.cop) | Simple HTTP server |
| [http-client](../packages/http/samples/http-client.cop) | HTTP client with API calls |
| [list-cop-types](../packages/cop/samples/list-types.cop) | List types in .cop files |
| [query-results](../packages/analysis-codeql/samples/query-results.cop) | Query CodeQL SARIF results |
| [generate-query](../packages/csharp-codeql-export/samples/generate-query.cop) | Generate a CodeQL query |
| [run-checkov](../packages/analysis-checkov/samples/run-checkov.cop) | Run Checkov security checks |
| [run-semgrep](../packages/analysis-semgrep/samples/run-semgrep.cop) | Run Semgrep static analysis |
| [run-spectral](../packages/analysis-spectral/samples/run-spectral.cop) | Run Spectral API linting |
| [run-trivy](../packages/analysis-trivy/samples/run-trivy.cop) | Run Trivy vulnerability scanner |
