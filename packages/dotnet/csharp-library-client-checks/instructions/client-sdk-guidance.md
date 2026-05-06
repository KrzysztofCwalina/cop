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
