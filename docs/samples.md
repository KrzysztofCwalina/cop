# Samples

All samples in this repository pass `cop verify` (syntax + binding validation).

## Walkthrough Samples

These are standalone examples in `samples/` demonstrating language features and patterns.

| Sample | Description |
|--------|-------------|
| [s1-helloworld](../samples/s1-helloworld/rules.cop) | Basic code analysis rule: flags C# statements that use 'var' |
| [s2-json](../samples/s2-json/young.cop) | JSON parsing with custom type definitions |
| [s3-files](../samples/s3-files/young.cop) | File system analysis and querying |
| [s4-lister](../samples/s4-lister/lister.cop) | Command composition with the & operator |
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
| [s18-Provider](../samples/s18-Provider/package/src/sample.cop) | Custom data provider package |
| [s19-providerstreaming](../samples/s19-providerstreaming/package/src/ticker.cop) | Streaming provider with source/sink pattern |
| [s20-crosslanguage](../samples/s20-crosslanguage/checks.cop) | Cross-language code analysis |
| [s21-filtering](../samples/s21-filtering/filters.cop) | Filtering and subset patterns |

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
| [enforce-layering-rules](../packages/code-layering/samples/enforce-layering-rules.cop) | Enforce dependency layering rules |

### C# (.NET)

| Sample | Description |
|--------|-------------|
| [run-csharp-style-checks](../packages/dotnet/csharp-checks/samples/run-csharp-style-checks.cop) | Run built-in C# style checks |
| [check-client-conventions](../packages/dotnet/csharp-library-checks/samples/check-client-conventions.cop) | Check SDK client library conventions |
| [run-azure-sdk-checks](../packages/dotnet/csharp-library-azure-checks/samples/run-azure-sdk-checks.cop) | Run Azure SDK-specific checks |
| [api-listing](../packages/dotnet/csharp-lister/samples/api-listing.cop) | Generate API listing |
| [api-diff](../packages/dotnet/csharp-lister/samples/api-diff.cop) | Generate API diff |

### JavaScript / TypeScript

| Sample | Description |
|--------|-------------|
| [run-js-ts-checks](../packages/js/javascript-checks/samples/run-js-ts-checks.cop) | Run built-in JS/TS style checks |
| [check-client-conventions](../packages/js/javascript-library-checks/samples/check-client-conventions.cop) | Check SDK client library conventions |
| [run-azure-sdk-checks](../packages/js/javascript-library-azure-checks/samples/run-azure-sdk-checks.cop) | Run Azure SDK-specific checks |
| [api-listing](../packages/js/javascript-lister/samples/api-listing.cop) | Generate API listing |
| [api-diff](../packages/js/javascript-lister/samples/api-diff.cop) | Generate API diff |

### Python

| Sample | Description |
|--------|-------------|
| [run-python-style-checks](../packages/python/python-checks/samples/run-python-style-checks.cop) | Run built-in Python style checks |
| [check-client-conventions](../packages/python/python-library-checks/samples/check-client-conventions.cop) | Check SDK client library conventions |
| [run-azure-sdk-checks](../packages/python/python-library-azure-checks/samples/run-azure-sdk-checks.cop) | Run Azure SDK-specific checks |
| [api-listing](../packages/python/python-lister/samples/api-listing.cop) | Generate API listing |
| [api-diff](../packages/python/python-lister/samples/api-diff.cop) | Generate API diff |

### Specialized

| Sample | Description |
|--------|-------------|
| [load-and-query-json-data](../packages/json/samples/load-and-query-json-data.cop) | Load and query JSON data |
| [check-for-broken-links](../packages/markdown/samples/check-for-broken-links.cop) | Check for broken links in markdown |
| [list-all-models](../packages/typespec/samples/list-all-models.cop) | List all TypeSpec models |
| [find-get-operations](../packages/typespec-http/samples/find-get-operations.cop) | Find HTTP GET operations |
| [http-server](../packages/http/samples/http-server.cop) | Simple HTTP server |
| [http-client](../packages/http/samples/http-client.cop) | HTTP client with API calls |
| [query-results](../packages/codeql/samples/query-results.cop) | Query CodeQL SARIF results |
| [generate-query](../packages/codeql-export/samples/generate-query.cop) | Generate a CodeQL query |
