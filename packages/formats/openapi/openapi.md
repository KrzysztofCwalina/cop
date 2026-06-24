# openapi

Analyze OpenAPI/Swagger specifications by extracting the top-level `paths` mapping and
its HTTP operations. The provider is a hand-rolled parser with no third-party OpenAPI
library. YAML/YML OpenAPI 3.x and Swagger 2.0 documents are the primary target; JSON is
supported by a small best-effort object scanner for common specs.

Only `.yaml`, `.yml`, and `.json` files with a top-level `openapi` or `swagger` marker are
treated as specs. Ordinary YAML/JSON files are ignored.

## Types

- **OpenApiOperation** — an HTTP operation. Fields: `Method` (uppercase), `Path`,
  `OperationId`, `HasSummary`, `HasResponses`, `Line`, `File`, `Source`.
- **OpenApiPath** — an OpenAPI path item. Fields: `Path`, `Line`, `File`, `Source`.

## Usage

```cop
import openapi

let operations = openapi.parse().Operations
let paths = openapi.parse('specs').Paths
```

`OpenApiOperation` and `OpenApiPath` conform to `TextFilePosition`, so they work directly
with `toError` / `toWarning` / `toInfo` from `code-analysis`.

## Example — require documentation and responses

```cop
import openapi
import code

predicate isUndocumented(OpenApiOperation) => !OpenApiOperation.HasSummary
predicate hasNoResponses(OpenApiOperation) => !OpenApiOperation.HasResponses

let undocumented = openapi.parse().Operations:isUndocumented
    :toError('{item.Method} {item.Path} is missing a summary or description')

let noResponses = openapi.parse().Operations:hasNoResponses
    :toError('{item.Method} {item.Path} is missing responses')

command MAIN = CHECK(undocumented + noResponses)
```

See `samples/undocumented-operations.cop` for a runnable version.

