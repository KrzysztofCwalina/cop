# Azure Client Library Guidance

This package extends the generic `csharp-library-client` patterns with Azure-specific requirements.

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
