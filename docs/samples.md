# Samples

All samples in this repository pass `cop verify` (syntax + binding validation).

## Walkthrough Samples

These are standalone examples in `samples/` demonstrating language features and patterns.

| Sample | Description |
|--------|-------------|
| [s1-helloworld](../samples/s1-helloworld/main.cop) | Basic code analysis rule: flags C# statements that use 'var' |
| [s2-json](../samples/s2-json/main.cop) | JSON parsing with custom type definitions |
| [s3-files](../samples/s3-files/main.cop) | File system analysis and querying |
| [s4-lister](../samples/s4-lister/main.cop) | Command composition with the & operator |
| [s5-namedcommands](../samples/s5-namedcommands/commands.cop) | Named commands for reusable operations |
| [s6-strings](../samples/s6-strings/strings.cop) | String handling and single-quote strings |
| [s7-typespec](../samples/s7-typespec/checks.cop) | Domain-specific analysis using TypeSpec/HTTP API |
| [s8-violations](../samples/s8-violations/checks.cop) | Simple code checks using action-let pattern |
| [s9-predicatechaining](../samples/s9-predicatechaining/checks.cop) | Predicate chaining and negation |
| [s10-transforms](../samples/s10-transforms/transforms.cop) | List transforms: Where, Select, text, First, Count |
| [s11-languageconstraints](../samples/s11-languageconstraints/checks.cop) | Language-constrained predicates (multi-dispatch) |
| [s12-savecommand](../samples/s12-savecommand/export.cop) | Save command for writing results to files |
| [s13-composition](../samples/s13-composition/composition.cop) | Predicate composition: building complex rules from simple ones |
| [s14-inlineexpressions](../samples/s14-inlineexpressions/checks.cop) | Inline expressions and predicate composition |
| [s15-httpserver](../samples/s15-httpserver/server.cop) | HTTP server using the push-provider pipeline |
| [s17-currying](../samples/s17-currying/currying.cop) | Partial application (currying) |
| [s18-Provider](../samples/s18-Provider/package/samples/use-widgets.cop) | Custom data provider package (see also [sample.cop](../samples/s18-Provider/package/src/sample.cop)) |
| [s19-providerstreaming](../samples/s19-providerstreaming/main.cop) | Streaming provider with source/sink pattern (see also [ticker.cop](../samples/s19-providerstreaming/ticker/src/ticker.cop)) |
| [s20-crosslanguage](../samples/s20-crosslanguage/checks.cop) | Cross-language code analysis |
| [s21-filtering](../samples/s21-filtering/filters.cop) | Filtering and subset patterns |

## Demo Samples

End-to-end demo scripts in `samples/demo/` covering the full cop workflow.

| Sample | Description |
|--------|-------------|
| [demo-multilang](../samples/demo/demo-multilang.cop) | Multi-language codebase analysis (C# + JS) |
| [demo-slop](../samples/demo/demo-slop.cop) | Computing aggregate slop score as JSON |
| [demo-custom](../samples/demo/demo-custom.cop) | Adding custom rules to slop scoring |

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
