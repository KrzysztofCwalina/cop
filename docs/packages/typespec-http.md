## typespec-http

HTTP protocol graph derived from TypeSpec specs. &nbsp; `import typespec-http`

**Source:** [`packages/typespec-http/src/typespec-http.cop`](../../packages/typespec-http/src/typespec-http.cop) &nbsp; **Depends on:** typespec

---

### Collections

| Collection | Type | Description |
|---|---|---|
| `Operations` | `[HttpOperation]` | All HTTP operations |
| `Services` | `[HttpService]` | All HTTP services |

---

### Types

#### HttpParameter

| Property | Type | Description |
|---|---|---|
| `Name` | string | Parameter name |
| `Type` | string | Type expression |
| `In` | string | Location: `query`, `header`, `path`, `body` |
| `Optional` | bool | True if optional |
| `Style` | string? | Serialization style |

#### HttpHeader

| Property | Type | Description |
|---|---|---|
| `Name` | string | Header name |
| `Type` | string | Header type |

#### HttpResponse

| Property | Type | Description |
|---|---|---|
| `StatusCode` | string | HTTP status code |
| `Description` | string? | Response description |
| `Body` | string? | Body type expression |
| `Headers` | `[HttpHeader]` | Response headers |

#### HttpOperation

| Property | Type | Description |
|---|---|---|
| `Name` | string | Operation name |
| `Verb` | string | HTTP verb (get, put, post, etc.) |
| `Path` | string | Resolved route path |
| `UriTemplate` | string | URI template with placeholders |
| `Parameters` | `[HttpParameter]` | Request parameters |
| `Responses` | `[HttpResponse]` | Possible responses |
| `Interface` | string? | Parent interface |

#### HttpService

| Property | Type | Description |
|---|---|---|
| `Name` | string | Service name |
| `Namespace` | string | Service namespace |
| `Operations` | `[HttpOperation]` | Service operations |
| `Auth` | string? | Authentication scheme |

---

### Predicates

| Predicate | Applies To | Condition |
|---|---|---|
| `isGet` | HttpOperation | `Verb == 'get'` |
| `isPut` | HttpOperation | `Verb == 'put'` |
| `isPost` | HttpOperation | `Verb == 'post'` |
| `isPatch` | HttpOperation | `Verb == 'patch'` |
| `isDelete` | HttpOperation | `Verb == 'delete'` |
