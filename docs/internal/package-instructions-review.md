# Package Embedded Instructions — Review

_Generated 2026-06-24 from `packages/**/instructions/*.md`._

These are the natural-language **instructions** embedded in cop packages. On restore they are
placed into `<target>/.github/instructions/{package}.instructions.md` (prefixed with an
`applyTo` front-matter) for coding agents to consume.

**4 of 54 packages** embed instructions. All four live under `packages/checks/` and target C# (`applyTo: **/*.cs`).

## Summary

| # | Package | Title | applyTo | Instruction file(s) |
|---|---|---|---|---|
| 1 | `csharp-checks` | C# Checks | `**/*.cs` | `coding-rules.md` |
| 2 | `csharp-library-checks` | C# Library and Client Library Development | `**/*.cs` | `library-design.md`<br>`client-sdk-guidance.md` |
| 3 | `csharp-library-azure-checks` | C# Azure Client Library Development | `**/*.cs` | `azure-client-guidance.md` |
| 4 | `csharp-nunit-checks` | NUnit Testing | `**/*.cs` | `nunit-guidance.md` |

The remaining **50 packages have no `instructions/` content** (groups: `core`, `formats`, `languages`, `tools`, plus the non-C# and snippet/azure check packages).

---

## 1. `csharp-checks` — coding-rules.md

> **Package:** C# Checks (`csharp-checks`) &middot; **applyTo:** `**/*.cs`  
> **Source:** `packages/checks/csharp-checks/instructions/coding-rules.md`

# C# Coding Standards and Best Practices

## Naming Conventions

### Types and Methods
- Use **PascalCase** for all public types, classes, interfaces, methods, and properties
- Examples: `public class UserService`, `public string GetUserName()`, `public interface IRepository`
- Maintain consistency across public APIs; it aids discoverability and IDE autocomplete

### Local Variables and Parameters
- Use **camelCase** for local variables and method parameters
- Example: `var userName = GetName(userId);`
- Enhances readability and distinguishes scope immediately

### Private Fields
- Use **_camelCase** with leading underscore for private instance fields
- Example: `private string _userName;`
- Explicitly declares field as private; improves code clarity

### Constants
- Use **UPPER_SNAKE_CASE** for compile-time constants
- Example: `private const int DEFAULT_TIMEOUT = 5000;`

---

## Nullable Reference Types

- **Always enable** nullable reference types in your `.csproj`:
  ```xml
  <PropertyGroup>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  ```
- Annotate return types and parameters explicitly: `public string? GetValue()` vs `public string GetValue()`
- Non-nullable types indicate values that are guaranteed not null; nullable types permit null
- **Avoid the null-forgiving operator (`!`)** except in rare cases where you're certain of non-nullability
- Using the null-forgiving operator too freely defeats the purpose of nullable reference types

---

## Async/Await Patterns

- Use **async all the way**: if a method calls an async operation, make it async
- Avoid `Task.Wait()` or `Task.Result` in application code; they can cause deadlocks
- Add `ConfigureAwait(false)` in library code to avoid capturing synchronization context:
  ```csharp
  public async Task<User> GetUserAsync(int id)
  {
      var response = await _httpClient.GetAsync($"/api/users/{id}").ConfigureAwait(false);
      return await response.Content.ReadAsAsync<User>().ConfigureAwait(false);
  }
  ```
- Always accept `CancellationToken` parameters in long-running async methods for proper cancellation support
- Example: `public async Task<List<Item>> FetchItemsAsync(CancellationToken ct = default)`

---

## LINQ Usage

- **Prefer method syntax** over query syntax for consistency
  - Bad: `var users = from u in context.Users where u.IsActive select u;`
  - Good: `var users = context.Users.Where(u => u.IsActive);`
- Avoid excessive method chaining on a single line; break into multiple lines for readability
- **Materialize collections** when needed for multiple iterations:
  ```csharp
  var activeUsers = users.Where(u => u.IsActive).ToList();  // Materialize once
  var count = activeUsers.Count;
  var first = activeUsers.FirstOrDefault();
  ```
- Use `ToList()` or `ToArray()` explicitly when you need to consume the entire sequence

---

## Error Handling

- Use exceptions for **exceptional cases**, not control flow
  - Bad: `try { return int.Parse(input); } catch { return 0; }`
  - Good: `if (int.TryParse(input, out var result)) { return result; }`
- Use **specific catch blocks** instead of generic `catch (Exception)`
  ```csharp
  try { /* code */ }
  catch (ArgumentNullException ex) { /* handle specific error */ }
  catch (InvalidOperationException ex) { /* handle specific error */ }
  catch (Exception ex) { /* log or rethrow */ }
  ```
- **Never use empty catch blocks**; always log or rethrow
- Use `throw;` to preserve stack trace when rethrowing

---

## Modern C# Features

- Use **file-scoped namespaces** (C# 10+) instead of traditional braces:
  ```csharp
  namespace MyApp.Services;
  
  public class UserService { }  // No nested braces needed
  ```
- Use **expression-bodied members** for simple one-liners:
  ```csharp
  public string GetFullName() => $"{FirstName} {LastName}";
  public bool IsActive => Status == UserStatus.Active;
  ```
- Use **target-typed `new` expressions** (C# 9+) to reduce verbosity:
  ```csharp
  User user = new() { Name = "John", Age = 30 };  // Type inferred from context
  ```
- Use **records** (C# 9+) for immutable data types:
  ```csharp
  public record UserDto(int Id, string Name, string Email);
  ```

---

## .NET 10 Target Framework

- Target **.NET 10** for all new projects unless otherwise specified
- Leverage latest language features and performance improvements
- Keep dependencies updated; use `dotnet package update --check-outdated` regularly
- Ensure your code compiles without warnings

---

## Code Organization

- One public type per file (with rare exceptions for nested types)
- Place using statements at the top; order: system namespaces first, then third-party, then local
- Use regions sparingly; well-organized methods eliminate the need for regions
- Keep methods focused and under 30 lines; extract complex logic into helper methods

---

## StyleCop Conventions

### Spacing

- One space after keywords (`if`, `for`, `while`, `switch`, `return`)
- One space before and after binary operators (`=`, `+`, `==`, `&&`)
- No space between method name and opening parenthesis: `GetItems()`
- No space inside parentheses: `GetItems(id)` not `GetItems( id )`
- One space after commas in argument lists: `Method(a, b, c)`
- Single space after `//` in single-line comments: `// This is correct`
- No tabs — use spaces for indentation (4 spaces per level)
- No trailing whitespace at end of lines

### Member Ordering

Within a type, order members by category and then by access level:

1. Constants and static readonly fields
2. Fields
3. Constructors
4. Properties
5. Methods
6. Nested types

Within each category, order by access: `public`, `internal`, `protected`, `private`.
Static members before instance members within the same access level.

### Access Modifiers

- Always declare access modifiers explicitly — do not rely on defaults
- Bad: `class UserService { }` — Good: `public class UserService { }`
- Bad: `string Name { get; }` — Good: `public string Name { get; }` or `private string Name { get; }`

### Brace Style

- Use Allman-style braces (opening brace on its own line):

```csharp
public void Process()
{
    if (condition)
    {
        DoWork();
    }
}
```

- Always use braces for `if`, `else`, `for`, `foreach`, `while`, `do` — even for single-line bodies
- No blank line after opening brace
- No blank line before closing brace
- Blank line after closing brace (before next element)

### Blank Lines

- One blank line between type members (methods, properties, constructors)
- No multiple consecutive blank lines
- No blank lines at the start or end of a file
- No blank line between a documentation comment and the element it documents

### Documentation

- All public types and members should have XML documentation comments
- Use `<summary>` for a brief description of the element
- Document parameters with `<param name="...">` tags
- Document return values with `<returns>` tags
- Documentation text should end with a period

```csharp
/// <summary>
/// Gets the user with the specified identifier.
/// </summary>
/// <param name="userId">The unique identifier of the user.</param>
/// <returns>The user, or <c>null</c> if not found.</returns>
public User? GetUser(string userId)
```

---

## 2. `csharp-library-checks` — library-design.md

> **Package:** C# Library and Client Library Development (`csharp-library-checks`) &middot; **applyTo:** `**/*.cs`  
> **Source:** `packages/checks/csharp-library-checks/instructions/library-design.md`

# C# Library Design Guidance

## API Surface Design

**Minimize Public Surface**
- Expose only types and members that form your intended public contract
- Use `internal` by default; make public only what's essential
- Avoid exposing implementation details, collection internals, or framework dependencies
- Consider using the `[EditorBrowsable(EditorBrowsableState.Never)]` attribute for members that must be public but shouldn't appear in IntelliSense

**Seal Classes by Default**
- Mark classes `sealed` unless inheritance is an explicit design goal
- Sealed classes are more maintainable—you control all subclass behavior
- If you anticipate subclassing, document the protected contract clearly
- Use abstract base classes when defining extension points

**Prefer Interfaces**
- Design abstractions around interfaces, not base classes
- Interfaces enable composition, multiple inheritance of type, and better testability
- Keep interfaces focused and cohesive (e.g., `IEnumerable`, `IDisposable`)

## Versioning

**Semantic Versioning**
- Follow SemVer: MAJOR.MINOR.PATCH (e.g., 2.1.3)
- MAJOR: Breaking changes
- MINOR: New backward-compatible features
- PATCH: Bug fixes

**Assembly Versioning**
- Set `AssemblyVersion` only on major version changes to avoid binding redirects
- Set `AssemblyFileVersion` and `InformationalVersion` to full SemVer
- Example: AssemblyVersion=2.0.0.0, FileVersion=2.1.3.0, InformationalVersion=2.1.3

**Backward Compatibility**
- Never remove public members—deprecate them with `[Obsolete]` instead
- Never change method signatures in breaking ways
- Never change the meaning of parameters or return values
- Use overloads to add optional parameters rather than breaking existing ones

## Packaging

**NuGet Conventions**
- Use `<PackageId>` matching the namespace root (e.g., Company.Library)
- Include meaningful `<Description>` and `<Authors>` metadata
- Add `<RepositoryUrl>` pointing to source control
- Set `<PackageProjectUrl>` to documentation
- Use `<LicenseExpression>` (e.g., "Apache-2.0", "MIT")

**Package Metadata**
- Include `<Version>` matching your versioning scheme
- Document breaking changes in `<PackageReleaseNotes>`
- Add relevant `<PackageTags>` for discoverability
- Set `<Nullable>enable</Nullable>` for modern null-safety

**README**
- Create a concise README.md in the package root
- Include basic usage example, installation instructions, and license
- Link to full documentation for complex libraries

## Dependency Management

**Minimize Dependencies**
- Reduce external dependencies—each adds maintenance burden
- Audit transitive dependencies; watch for version conflicts
- Prefer `Target Frameworks` like `net6.0` and `netstandard2.1` over multiple frameworks

**Use Abstractions**
- Depend on interfaces, not concrete implementations
- Allow consumers to substitute their own implementations
- Example: accept `ILogger` rather than `ConcreteLogger`

**Avoid Diamond Dependencies**
- If two of your dependencies depend on conflicting versions of a third library, your package becomes problematic
- Document minimum version requirements clearly
- Test against realistic dependency trees

## XML Documentation

**All Public Members**
- Every public type, method, property, and event must have XML documentation
- Use `<summary>`, `<param>`, `<returns>`, `<exception>` tags
- Keep summaries concise; use `<remarks>` for detailed explanation

**Examples and Remarks**
- Include `<example>` for complex APIs or common use cases
- Document exceptions in `<exception>` tags so consumers know what to handle
- Use `<see cref="..."/>` and `<seealso cref="..."/>` for cross-references

Example:
```csharp
/// <summary>
/// Computes the hash of the provided data.
/// </summary>
/// <param name="data">The input bytes to hash.</param>
/// <returns>A 32-character hexadecimal string.</returns>
/// <exception cref="ArgumentNullException">Thrown if data is null.</exception>
public string ComputeHash(byte[] data)
{
    // ...
}
```

## Obsoleting APIs

**ObsoleteAttribute Usage**
- Apply `[Obsolete("message", false)]` to deprecate APIs; don't remove them immediately
- Set the second parameter to `true` only when removing in a major version
- Provide clear migration guidance in the obsolete message

Example:
```csharp
[Obsolete("Use ComputeHashAsync instead for better performance.", false)]
public string ComputeHash(byte[] data)
{
    // ...
}
```

**Migration Paths**
- Document the replacement API clearly
- If possible, provide helper methods to ease migration
- Give at least one full minor version (2-4 weeks) before removing deprecated APIs

---

## 3. `csharp-library-checks` — client-sdk-guidance.md

> **Package:** C# Library and Client Library Development (`csharp-library-checks`) &middot; **applyTo:** `**/*.cs`  
> **Source:** `packages/checks/csharp-library-checks/instructions/client-sdk-guidance.md`

# Client Library Guidance

## Client Constructor Patterns

Client libraries follow a standardized constructor pattern:

```csharp
public class ServiceClient
{
    public ServiceClient(Uri endpoint)
        : this(endpoint, new ServiceClientOptions()) { }

    public ServiceClient(Uri endpoint, ServiceClientOptions options)
    {
        Argument.AssertNotNull(endpoint, nameof(endpoint));
        options ??= new ServiceClientOptions();

        _endpoint = endpoint;
        _pipeline = CreatePipeline(options);
    }
}
```

Key patterns:
- **Endpoint + Options** constructor pattern
- Endpoint validation (must not be null)
- Options provide configuration without explosion of constructors
- Credential support is added via additional constructor overloads when needed (e.g., API key, connection string)

## Retry Policies

Implement exponential backoff with jitter and configurable retry attempts:

```csharp
// Default: 3 retries, exponential backoff (1s, 2s, 4s)
public int MaxRetries { get; set; } = 3;
public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(1);
public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(60);
```

Features:
- Idempotent requests retry on transient failures (408, 429, 500, 503)
- Non-idempotent requests don't retry on server errors
- Exponential backoff with maximum delay cap
- Configurable via ServiceClientOptions

## Pagination

Implement asynchronous pagination with continuation tokens:

```csharp
public async IAsyncEnumerable<BinaryData> GetItemsAsync(
    string continuationToken = null,
    CancellationToken cancellationToken = default)
{
    while (true)
    {
        var response = await GetItemsPageAsync(
            continuationToken, cancellationToken).ConfigureAwait(false);

        foreach (var item in ParseItems(response))
            yield return item;

        continuationToken = GetContinuationToken(response);
        if (continuationToken == null) break;
    }
}
```

- Use `IAsyncEnumerable<T>` for implicit pagination
- Return continuation tokens for manual pagination control
- Propagate cancellation tokens through pagination loops

## Long-Running Operations (LRO)

LROs follow polling-based patterns:

```csharp
public async Task<Operation<T>> StartLongRunningOperationAsync(
    CancellationToken cancellationToken = default)
{
    var response = await StartOperationAsync(cancellationToken)
        .ConfigureAwait(false);
    return new Operation<T>(_client, response);
}

public class Operation<T>
{
    public async ValueTask<OperationStatus> UpdateStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await PollOperationAsync(OperationId, cancellationToken)
            .ConfigureAwait(false);
        return ParseStatus(response);
    }

    public async ValueTask<T> WaitForCompletionAsync(
        CancellationToken cancellationToken = default)
    {
        while (!HasCompleted)
            await Task.Delay(PollingInterval, cancellationToken);
        return await GetResultAsync(cancellationToken);
    }
}
```

Features:
- Immediate return of Operation<T> object
- Non-blocking polling with configurable intervals
- `WaitForCompletion()` for synchronous consumption
- Result access after completion

## Client Options Pattern

Centralize all configuration in a ServiceClientOptions class:

```csharp
public class ServiceClientOptions
{
    // Retry configuration
    public int MaxRetries { get; set; } = 3;
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(60);

    // Diagnostics
    public bool IsLoggingEnabled { get; set; } = true;

    // Service-specific options
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
```

Options classes should:
- Provide sensible defaults
- Be extensible for service-specific configuration
- Include diagnostics and retry settings
- Include transport/pipeline configuration as needed

## Response Types

Clients should wrap raw responses in a typed result:

```csharp
public async Task<ClientResult<Item>> GetItemAsync(string id,
    CancellationToken cancellationToken = default)
{
    var response = await GetItemRawAsync(id, cancellationToken)
        .ConfigureAwait(false);

    Item item = JsonSerializer.Deserialize<Item>(response.Content);
    return ClientResult.FromValue(item, response);
}
```

- `ClientResult<T>` provides typed values with access to raw response
- Status codes and headers accessible via the raw response
- Consistent return type across all client methods

## Cancellation Token Propagation

Always propagate cancellation tokens through async chains:

```csharp
public async Task<Result> DoWorkAsync(CancellationToken cancellationToken = default)
{
    // Pass token to all async operations
    await _httpClient.GetAsync(url, cancellationToken)
        .ConfigureAwait(false);

    await Task.Delay(1000, cancellationToken)
        .ConfigureAwait(false);
}
```

## Naming Conventions

Follow standard .NET client library naming:
- **Client class**: `{ServiceName}Client` (e.g., `BlobClient`, `ChatClient`)
- **Options class**: `{ServiceName}ClientOptions` (e.g., `BlobClientOptions`)
- **Async methods**: Suffix with `Async` (e.g., `CreateAsync()`, `DeleteAsync()`)
- **Sync methods**: No suffix (e.g., `Create()`, `Delete()`)

These patterns ensure consistency across client libraries and improve discoverability for developers.

---

## 4. `csharp-library-azure-checks` — azure-client-guidance.md

> **Package:** C# Azure Client Library Development (`csharp-library-azure-checks`) &middot; **applyTo:** `**/*.cs`  
> **Source:** `packages/checks/csharp-library-azure-checks/instructions/azure-client-guidance.md`

# Azure Client Library Guidance

This package extends the generic `csharp-library` patterns with Azure-specific requirements.

## Pipeline Framework Choice

Azure client libraries can use either pipeline framework:

- **Azure.Core** — the established Azure SDK pipeline with `HttpPipeline`, `ClientOptions`, and `Response<T>`
- **System.ClientModel** — the newer, framework-agnostic pipeline with `ClientPipeline`, `ClientOptions`, and `ClientResult<T>`

Both are supported. Choose based on your library's needs:

| Concern | Azure.Core | System.ClientModel |
|---------|-----------|-------------------|
| Pipeline type | `HttpPipeline` | `ClientPipeline` |
| Options base | `Azure.Core.ClientOptions` | `System.ClientModel.Primitives.ClientPipelineOptions` |
| Response type | `Response<T>` | `ClientResult<T>` |
| Credential | `TokenCredential` (Azure.Core) | `ApiKeyCredential` or `TokenCredential` (Azure.Core) |
| Best for | Libraries tightly integrated with Azure ecosystem | Libraries that may also target non-Azure services |

## TokenCredential Requirement

All Azure client libraries **must** accept `TokenCredential` for authentication. This is a hard requirement — API key-only clients are not permitted for Azure services.

### Azure.Core Pattern

```csharp
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;

public class ServiceClient
{
    private readonly HttpPipeline _pipeline;
    private readonly Uri _endpoint;

    public ServiceClient(Uri endpoint, TokenCredential credential)
        : this(endpoint, credential, new ServiceClientOptions()) { }

    public ServiceClient(Uri endpoint, TokenCredential credential,
        ServiceClientOptions options)
    {
        Argument.AssertNotNull(endpoint, nameof(endpoint));
        Argument.AssertNotNull(credential, nameof(credential));
        options ??= new ServiceClientOptions();

        _endpoint = endpoint;
        _pipeline = HttpPipelineBuilder.Build(options,
            new BearerTokenAuthenticationPolicy(credential, options.Scopes));
    }
}

public class ServiceClientOptions : ClientOptions
{
    internal string[] Scopes { get; set; } = new[] { "https://service.azure.com/.default" };
}
```

### System.ClientModel Pattern

```csharp
using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Core;

public class ServiceClient
{
    private readonly ClientPipeline _pipeline;
    private readonly Uri _endpoint;

    public ServiceClient(Uri endpoint, TokenCredential credential)
        : this(endpoint, credential, new ServiceClientOptions()) { }

    public ServiceClient(Uri endpoint, TokenCredential credential,
        ServiceClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);
        options ??= new ServiceClientOptions();

        _endpoint = endpoint;
        _pipeline = ClientPipeline.Create(options,
            perCallPolicies: ReadOnlySpan<PipelinePolicy>.Empty,
            perTryPolicies: new[] { new BearerTokenAuthenticationPolicy(credential, options.Scopes) },
            beforeTransportPolicies: ReadOnlySpan<PipelinePolicy>.Empty);
    }
}

public class ServiceClientOptions : ClientPipelineOptions
{
    internal string[] Scopes { get; set; } = new[] { "https://service.azure.com/.default" };
}
```

## Constructor Requirements

Azure clients must provide these constructor overloads:

```csharp
// Required: endpoint + credential
public ServiceClient(Uri endpoint, TokenCredential credential);

// Required: endpoint + credential + options
public ServiceClient(Uri endpoint, TokenCredential credential, ServiceClientOptions options);

// Optional: connection string (if the service supports it)
public ServiceClient(string connectionString);
```

**Rules:**
- `TokenCredential` must always be supported
- API key constructors are optional and supplementary — they do not replace `TokenCredential`
- The `credential` parameter must never be nullable
- The default scope must target the Azure service's `.default` resource

## Response Types

### With Azure.Core

```csharp
public async Task<Response<Item>> GetItemAsync(string id,
    CancellationToken cancellationToken = default)
{
    using var message = _pipeline.CreateMessage();
    // ... build request ...
    await _pipeline.SendAsync(message, cancellationToken).ConfigureAwait(false);

    var response = message.Response;
    if (response.IsError)
        throw new RequestFailedException(response);

    Item item = Item.FromResponse(response);
    return Response.FromValue(item, response);
}
```

### With System.ClientModel

```csharp
public async Task<ClientResult<Item>> GetItemAsync(string id,
    CancellationToken cancellationToken = default)
{
    using var message = _pipeline.CreateMessage();
    // ... build request ...
    PipelineResponse response = await _pipeline.ProcessMessageAsync(
        message, null, cancellationToken).ConfigureAwait(false);

    Item item = Item.FromResponse(response);
    return ClientResult.FromValue(item, response);
}
```

## Diagnostics and Logging

Azure clients must support Azure SDK diagnostics:

```csharp
// Azure.Core
public class ServiceClientOptions : ClientOptions
{
    public ServiceClientOptions()
    {
        Diagnostics.ApplicationId = "service-client";
    }
}
```

- Enable distributed tracing via `DiagnosticScope`
- Log request/response headers (excluding sensitive values)
- Use `EventSource` for detailed SDK logging

## Naming Conventions

Azure client libraries follow Azure SDK naming:
- **Client class**: `{ServiceName}Client` (e.g., `BlobClient`, `KeyVaultClient`, `OpenAIClient`)
- **Options class**: `{ServiceName}ClientOptions`
- **NuGet package**: `Azure.{ServiceArea}.{ServiceName}` (e.g., `Azure.Storage.Blobs`)
- **Namespace**: matches the NuGet package name

---

## 5. `csharp-nunit-checks` — nunit-guidance.md

> **Package:** NUnit Testing (`csharp-nunit-checks`) &middot; **applyTo:** `**/*.cs`  
> **Source:** `packages/checks/csharp-nunit-checks/instructions/nunit-guidance.md`

# NUnit Testing Guidance

NUnit-specific patterns and best practices for unit testing in C#.

## Assert.That Constraint Model

NUnit's constraint model provides expressive, fluent assertions:

```csharp
// Prefer: Constraint model (fluent and readable)
Assert.That(result, Is.EqualTo(42));
Assert.That(collection, Has.Count.EqualTo(3));
Assert.That(value, Is.GreaterThan(0).And.LessThan(100));
Assert.That(text, Does.Contain("expected"));
Assert.That(items, Is.Empty);

// Avoid: Classic assertions (less readable)
Assert.AreEqual(42, result);
Assert.IsTrue(collection.Count == 3);
Assert.IsNotNull(value);
```

**Advantages of Assert.That**:
- Clear intent with method names (Is, Has, Does, Throws)
- Chainable constraints for complex conditions
- Better error messages on failure
- Easier to read like specifications

## Test Lifecycle Attributes

NUnit provides attributes for test setup and teardown at different scopes:

```csharp
[TestFixture]
public class CalculatorTests
{
    private Calculator _calculator;
    private static DatabaseConnection _sharedConnection;
    
    // Runs once before any tests in this fixture
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _sharedConnection = new DatabaseConnection("test");
    }
    
    // Runs before each test
    [SetUp]
    public void SetUp()
    {
        _calculator = new Calculator();
    }
    
    // Runs after each test
    [TearDown]
    public void TearDown()
    {
        _calculator?.Dispose();
    }
    
    // Runs once after all tests in this fixture
    [OneTimeTearDown]
    public static void OneTimeTearDown()
    {
        _sharedConnection?.Close();
    }
    
    [Test]
    public void Add_TwoNumbers_ReturnsSum()
    {
        var result = _calculator.Add(2, 3);
        Assert.That(result, Is.EqualTo(5));
    }
}
```

**Use [OneTimeSetUp] for**:
- Expensive setup (database connection, file I/O)
- Shared read-only resources
- Static configuration

**Use [SetUp] for**:
- Fresh instance initialization
- Per-test state reset
- Dependency injection

## Parameterized Tests

Run the same test with multiple inputs:

```csharp
[TestFixture]
public class CalculatorTests
{
    // [TestCase] for inline parameters
    [TestCase(2, 3, 5)]
    [TestCase(0, 0, 0)]
    [TestCase(-1, 1, 0)]
    [TestCase(int.MaxValue, 0, int.MaxValue)]
    public void Add_WithVariousInputs_ReturnsCorrectSum(int a, int b, int expected)
    {
        var calc = new Calculator();
        Assert.That(calc.Add(a, b), Is.EqualTo(expected));
    }
    
    // [Values] for single parameter variations
    [Test]
    public void IsPositive_WithMultipleValues_ReturnsCorrectly(
        [Values(-1, 0, 1, 100)] int value)
    {
        Assert.That(Calculator.IsPositive(value), Is.EqualTo(value > 0));
    }
    
    // [TestCaseSource] for complex test data
    [TestCaseSource(nameof(DivisionTestCases))]
    public void Divide_WithTestData_ReturnsExpectedResult(double numerator, double denominator, double expected)
    {
        var calc = new Calculator();
        Assert.That(calc.Divide(numerator, denominator), Is.EqualTo(expected));
    }
    
    private static IEnumerable<TestCaseData> DivisionTestCases()
    {
        yield return new TestCaseData(10, 2, 5).SetName("Divide_10by2_Returns5");
        yield return new TestCaseData(1, 3, 0.333).SetName("Divide_1by3_Returns0.333");
        yield return new TestCaseData(0, 5, 0).SetName("Divide_0by5_Returns0");
    }
}
```

## Category Conventions

Use [Category] attributes for test organization and filtering:

```csharp
[TestFixture]
public class UserServiceTests
{
    [Test]
    [Category("Unit")]
    public void CreateUser_ValidInput_ReturnsUserId()
    {
        // Fast unit test
    }
    
    [Test]
    [Category("Integration")]
    [Category("Database")]
    public void CreateUser_SavesToDatabase_PersistsCorrectly()
    {
        // Slower integration test
    }
    
    [Test]
    [Category("Slow")]
    public void BulkCreateUsers_1000Users_CompletesInReasonableTime()
    {
        // Performance/stress test
    }
}
```

**Standard categories**:
- `Unit`: Fast, isolated tests
- `Integration`: Tests with external dependencies
- `Database`: Tests requiring database
- `Network`: Tests requiring network calls
- `Slow`: Tests that take significant time
- `Smoke`: Quick sanity checks

Run by category: `dotnet test --filter "Category=Unit"` or `dotnet test --filter "Category!=Slow"`

## Async Test Patterns

Test async code properly using async Task:

```csharp
[TestFixture]
public class AsyncServiceTests
{
    private IAsyncService _service;
    
    [SetUp]
    public void SetUp()
    {
        _service = new AsyncService();
    }
    
    // Proper async test: return Task
    [Test]
    public async Task FetchData_ValidId_ReturnsData()
    {
        var result = await _service.FetchDataAsync(123);
        Assert.That(result, Is.Not.Null);
    }
    
    // Test timeout and cancellation
    [Test]
    public void FetchData_Timeout_ThrowsTimeoutException()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.That(
            async () => await _service.SlowFetchAsync(cts.Token),
            Throws.TypeOf<OperationCanceledException>()
        );
    }
    
    // Test exception from async method
    [Test]
    public void InvalidOperation_ThrowsException()
    {
        Assert.That(
            async () => await _service.FailAsync(),
            Throws.TypeOf<InvalidOperationException>()
        );
    }
}
```

## TestContext Usage for Logging

Use TestContext for runtime information and logging:

```csharp
[TestFixture]
public class DiagnosticTests
{
    [Test]
    public void TestWithLogging()
    {
        TestContext.WriteLine($"Test started at {DateTime.Now}");
        TestContext.WriteLine($"Test name: {TestContext.CurrentContext.Test.Name}");
        
        // Your test code
        var result = SomeOperation();
        
        TestContext.WriteLine($"Result: {result}");
        Assert.That(result, Is.GreaterThan(0));
        
        TestContext.WriteLine("Test completed successfully");
    }
    
    [Test]
    public void TestWithProgress()
    {
        for (int i = 0; i < 100; i++)
        {
            TestContext.Write($"Processing item {i}...");
            ProcessItem(i);
            TestContext.WriteLine(" done");
        }
    }
}
```

Output appears in test results and can help debug test failures.

## AutoFixture and Test Data Generation

Use AutoFixture for automatic test data generation:

```csharp
[TestFixture]
public class OrderServiceTests
{
    private readonly Fixture _fixture = new Fixture();
    
    [Test]
    public void CreateOrder_ValidOrder_ReturnsOrderId()
    {
        // Auto-generate test data
        var order = _fixture.Create<Order>();
        var service = new OrderService();
        
        var orderId = service.CreateOrder(order);
        Assert.That(orderId, Is.GreaterThan(0));
    }
    
    [Test]
    public void CreateOrder_MultipleOrders_AllGetUniqueIds()
    {
        var service = new OrderService();
        var orders = _fixture.CreateMany<Order>(10).ToList();
        
        var orderIds = orders.Select(o => service.CreateOrder(o)).ToList();
        
        Assert.That(orderIds, Is.Unique);
        Assert.That(orderIds, Has.All.GreaterThan(0));
    }
    
    [Test]
    public void ProcessOrder_CustomData_HandlesCorrectly()
    {
        // Customize generated data
        _fixture.Customize<Order>(c => c
            .With(x => x.Status, OrderStatus.Pending)
            .With(x => x.Total, 99.99m)
        );
        
        var order = _fixture.Create<Order>();
        Assert.That(order.Status, Is.EqualTo(OrderStatus.Pending));
        Assert.That(order.Total, Is.EqualTo(99.99m));
    }
}
```

**AutoFixture benefits**:
- Reduces boilerplate test data setup
- Generates realistic anonymous data
- Customizable for specific scenarios
- Encourages focusing on behavior, not data

---

