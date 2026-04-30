# Static Analysis Checks

## code-analysis

- **callsSyncWhenAsyncExists** — Flags sync method calls when an async variant exists on any type in the codebase

## arch-layering

- **notInLayer** — Detects projects not assigned to any defined architectural layer

## python

- **print-calls** — Avoid `print()`; use logging instead
- **bare-except-clauses** — Do not use bare `except`; catch a specific exception type
- **silenced-exceptions** — Do not silence `Exception`; reraise or catch a specific type
- **no-six-import** — Do not import the `six` package; Python 2 is no longer supported
- **no-legacy-typing** — Do not use type comments; use Python 3.9+ type annotations
- **no-eval** — Do not use `eval()`; it is a security risk

## python-library

- **untyped-public-params** — Public methods should have type hints on all parameters
- **missing-return-types** — Public methods should declare a return type

## python-library-client

- **async-client-bad-name** — Async clients should not include `Async` in the class name
- **client-missing-kwargs** — Client constructor must accept `**kwargs`
- **no-connection-string-in-ctor** — Use a class method factory for connection strings, not a constructor parameter
- **no-static-methods** — Client types should not use `@staticmethod`; prefer module-level functions
- **too-many-positional-params** — Client methods should not have more than 5 positional parameters
- **async-method-missing-kwargs** — Async client methods must accept `**kwargs`
- **lro-naming** — LRO methods must be prefixed with `begin_`
- **paging-naming** — Paging methods must be prefixed with `list_`
- **name-too-long** — Type names should be under 40 characters

## python-library-client-azure

- **client-needs-credential** — Azure clients must accept a `credential` parameter
- **client-needs-api-version** — Azure clients must accept an `api_version` keyword argument
- **method-snake-case** — Method names must use `snake_case`
- **type-pascal-case** — Type names must use PascalCase
- **delete-returns-none** — Delete operations should return `None` or `LROPoller[None]`
- **no-raise-with-traceback** — Use `raise ... from` instead of `raise_with_traceback`
- **no-legacy-typing** — Do not use type comments; use Python 3.9+ type annotations
- **no-asyncio-sleep** — Use `azure.core` transport sleep instead of `asyncio.sleep`
- **no-log-exceptions-above-debug** — Do not log exceptions at levels above debug (may reveal sensitive info)
- **missing-tracing-decorator** — Public service methods must have `@distributed_trace` decorator

## javascript

- **console-calls** — Avoid `console` output in production code
- **alert-calls** — Do not use `alert()`
- **eval-calls** — Do not use `eval()`; it is a security risk
- **debugger-statements** — Remove `debugger` statements
- **var-declarations** — Use `const` or `let` instead of `var`
- **swallowed-exceptions** — Catch blocks should rethrow or handle errors explicitly

## javascript-library

- **no-default-exports** — Do not use default exports — use named exports only
- **no-external-promises** — Use native Promise — do not import from bluebird or other polyfill libraries
- **no-const-enums** — Do not use `const enum` — use regular enum instead
- **no-window-reference** — Do not use `window` — use `self` for cross-platform compatibility

## javascript-library-client

- **standardized-verbs** — Client methods must not use banned verb prefixes (erase, fetch, getAll, make, pop, push, insertOrUpdate, updateOrInsert)
- **support-cancellation** — Async client methods must support cancellation via AbortSignalLike or an options parameter
- **subclient-naming** — Methods returning a subclient must be named `get*Client`
- **lro-begin-prefix** — Methods returning a Poller must be prefixed with `begin`
- **pagination-list-return** — `list*` methods should return `PagedAsyncIterableIterator`

## javascript-library-client-azure

- **client-needs-credential** — Azure client constructors must accept a `TokenCredential` parameter
- **copyright-header** — Source files must have a copyright header comment on the first line
- **error-handling** — Only throw `Error`, `TypeError`, `RangeError`, or `RestError`
- **no-console-logging** — Do not use `console` for logging — use `@azure/logger` instead

## csharp

- **var-declarations** — Disallow implicit typing with `var`; use explicit types
- **dynamic-declarations** — Disallow `dynamic` typing
- **thread-sleep-calls** — Use `Task.Delay` instead of blocking `Thread.Sleep`
- **console-calls** — Avoid `Console` output in library code
- **base-exception-catches** — Catch specific exception types instead of bare `Exception`
- **swallowed-exceptions** — Do not swallow base exceptions; always rethrow
- **configure-await-true-calls** — `ConfigureAwait(true)` is the default and should not be used explicitly
- **sync-over-async-calls** — Do not block on async code with `GetAwaiter().GetResult()`
- **bare-task-completion-sources** — `TaskCompletionSource` should use `RunContinuationsAsynchronously`
- **sync-wait-in-async** — Do not use sync-blocking calls (`.Wait()`, `.GetResult()`) inside async methods
- **uri-tostring** — Use `Uri.AbsoluteUri` instead of `Uri.ToString()`
- **type-name-is-keyword** — Type names should not match C# reserved keywords
- **uninstantiated-internal** — Internal non-abstract classes should be instantiated
- **undisposed-new** — Dispose objects before losing scope; use a `using` statement
- **readme-install-package** — Use `dotnet add package` instead of `Install-Package` in README
- **cpm-compliance** — `PackageReference` should not specify Version (use Central Package Management)

## csharp-style

- **interface-prefix** — Interface names must begin with `I` followed by an uppercase letter (SA1302)
- **type-name-casing** — Type names must begin with an uppercase letter (SA1300)
- **method-name-casing** — Public method names must begin with an uppercase letter (SA1300)
- **single-type-per-file** — A file may only contain a single type (SA1402)
- **comment-spacing** — Single-line comment must begin with a space after `//` (SA1005)
- **no-tabs** — Code must not contain tabs (SA1027)
- **no-trailing-whitespace** — Code must not contain trailing whitespace (SA1028)
- **no-empty-statements** — Code must not contain empty statements (SA1106)
- **no-empty-comments** — Comments must contain text (SA1120)
- **use-string-empty** — Use `string.Empty` instead of `""` (SA1122)
- **modifier-order** — Declaration keywords must follow order: access modifier first (SA1206)
- **file-header-required** — File must begin with a header comment (SA1633)
- **public-documented** — Public types should have XML documentation (SA1600)
- **public-method-documented** — Public methods should have XML documentation (SA1600)
- **no-public-fields** — Fields should be private; use a property instead (SA1401)
- **private-field-naming** — Private fields should start with underscore (`_`)
- **static-field-naming** — Private static fields should start with `s_`
- **const-naming** — Constants should be PascalCase
- **braces-on-own-line** — Opening brace should be on its own line, Allman style (SA1500)
- **required-braces** — Braces must not be omitted from control flow statements (SA1503)
- **public-property-documented** — Public properties should have XML documentation (SA1600)

## csharp-library

- **public-async-bool-params** — Public methods should not have a `bool` parameter named `async`
- **async-missing-bool-param** — Non-public async methods should accept a `bool async` parameter
- **awaits-using-default** — Library code must use `ConfigureAwait(false)` on all await expressions
- **unconditional-await-in-dual-mode** — Await must be guarded by `if (async)` in dual-mode methods
- **unconditional-sync-in-dual-mode** — `EnsureCompleted` must be guarded by `if (!async)` in dual-mode methods
- **wrong-async-arg-value** — Passing `async: false` inside an async guard is incorrect
- **wrong-sync-arg-value** — Passing `async: true` inside a sync guard is incorrect
- **async-param-misuse** — Do not pass the `async` parameter as an argument; use if/else branching

## csharp-library-client

- **client-needs-options-ctor** — Client types must have a constructor that accepts an Options parameter
- **client-sealed-or-abstract** — Client types must be sealed or abstract
- **async-needs-cancellation-token** — Async methods on client types must accept a `CancellationToken`
- **client-methods-virtual** — Public instance methods on client types should be virtual for mocking
- **async-needs-sync-counterpart** — Every public async method on a client must have a matching sync method

## csharp-library-client-azure

- **client-needs-token-credential** — Azure clients must accept `TokenCredential` for authentication
- **client-needs-mocking-ctor** — Client types must provide a protected parameterless constructor for mocking
- **options-inherits-base** — Options types must inherit from `ClientOptions` or `ClientPipelineOptions`
- **client-needs-options-ctor** — Client types should have a constructor that accepts `ClientOptions`
- **client-needs-simple-ctor** — Client types should have a constructor without options for simple scenarios
- **no-request-content-in-convenience** — Convenience methods should not take `RequestContent` parameters
- **options-single-ctor-param** — `ClientOptions` constructors should only accept an optional `ServiceVersion`
- **no-collection-suffix** — Model types should not use `Collection` suffix
- **no-request-suffix** — Model types should not use `Request` suffix
- **no-parameter-suffix** — Model types should not use `Parameter(s)` suffix
- **no-option-suffix** — Model types should not use `Option(s)` suffix (unless `ClientOptions`)
- **no-resource-suffix** — Model types should not use `Resource` suffix
- **options-first-param-service-version** — `ClientOptions` constructor first parameter should be `ServiceVersion`
- **options-needs-service-version-enum** — `ClientOptions` types should contain a nested `ServiceVersion` enum
- **service-version-naming** — `ServiceVersion` enum members should follow `V{major}_{minor}` pattern
- **service-version-default-value** — `ServiceVersion` parameter should default to the latest version
- **service-method-needs-cancellation** — Public virtual service methods must accept `CancellationToken` or `RequestContext`
- **no-banned-internal-types** — Public API should not expose types from banned internal namespaces
- **no-raw-http-return-types** — Client methods should not return raw HTTP types
- **no-pipeline-types-in-api** — Public API should not expose `Azure.Core.Pipeline` internal types
- **model-reader-writer-context** — `ModelReaderWriter.Read` should pass `ModelReaderWriterOptions` context
- **protocol-method-return-type** — Protocol methods (with `RequestContext`) must return allowed types only
- **no-ambiguous-overloads** — Avoid ambiguous overloads between protocol and convenience methods
- **internals-visible-to** — `InternalsVisibleTo` targets non-test assembly; internal APIs become public surface
- **internals-visible-to-non-test** — `InternalsVisibleTo` should only target test assemblies
- **settings-ctor-isolation** — Constructor should not combine `ClientSettings` with other parameters
- **duplicate-bcl-type-name** — Model type name duplicates a common .NET BCL type name
- **model-needs-factory** — Output model types should have a corresponding `ModelFactory` method
- **use-ensure-completed** — Use `EnsureCompleted()` instead of `GetAwaiter().GetResult()` in sync methods
- **no-public-async-in-sync** — Do not call `*Async` methods from sync context
- **no-single-word-type-name** — Avoid single-word public type names; use a more descriptive multi-word name
- **cancellation-token-propagation** — Async calls should propagate `CancellationToken` or `RequestContext`

## csharp-snippets

- **snippet-missing-docs** — C# snippet region has no corresponding markdown code fence
- **snippet-orphaned-ref** — Markdown fence references a snippet not found in C# source
- **snippet-duplicate-name** — Same snippet name defined in multiple C# locations
- **snippet-stale-content** — Markdown snippet content is out of sync with C# source

## fdg (Framework Design Guidelines)

### Naming

- **exception-suffix** — Types inheriting from `Exception` must end with `Exception`
- **attribute-suffix** — Types inheriting from `Attribute` must end with `Attribute`
- **eventargs-suffix** — Types inheriting from `EventArgs` must end with `EventArgs`
- **stream-suffix** — Types inheriting from `Stream` must end with `Stream`
- **no-hungarian-prefix** — Classes should not use Hungarian notation (`C` prefix)
- **param-camel-case** — Parameters must use camelCase
- **bool-property-naming** — Boolean properties should use affirmative naming (Is/Has/Can/Should)
- **namespace-segments** — Namespace should have at least two segments (Company.Product)

### Type Design

- **enum-no-suffix** — Enum types should not have `Enum` or `Flags` suffix
- **flags-plural-name** — `[Flags]` enums should use plural names
- **abstract-protected-ctor** — Abstract classes should have protected constructors, not public
- **static-no-instance** — Static classes should not have instance members
- **no-marker-interfaces** — Interfaces with no members should use an attribute instead
- **should-be-static** — Classes with only static members should be marked static
- **struct-size** — Structs should be small (4 or fewer fields)

### Member Design

- **max-parameters** — Public methods should not have more than 5 parameters
- **no-write-only-properties** — Properties should not be write-only; add a getter
- **extension-class-naming** — Extension method classes should end with `Extensions`
- **ctor-parameter-count** — Constructors with more than 5 parameters should consider an options type
- **avoid-bool-params** — Public methods with `bool` parameters should consider an enum or separate overloads
- **no-array-properties** — Properties returning arrays should use `IReadOnlyList<T>` instead

### Exceptions

- **exception-naming** — Custom exceptions must end in `Exception`
- **exception-standard-ctors** — Custom exceptions should have 3 standard constructors
- **no-banned-throws** — Do not throw `System.Exception`, `NullReferenceException`, or other reserved types
- **try-pattern-returns-bool** — `Try*` methods must return `bool`
- **no-swallow-cancellation** — Do not swallow `OperationCanceledException`; always rethrow

