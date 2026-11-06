// Connects HTTP request functions and streaming server endpoints to Cop pipelines.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Cop.Core;
using Cop.Lang;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cop.Providers.Http;

/// <summary>
/// Represents an incoming HTTP request in the streaming pipeline.
/// Carries a TaskCompletionSource to allow the sink to route the response back.
/// </summary>
public class HttpRequestItem
{
    /// <summary>Gets the incoming HTTP method.</summary>
    public string Method { get; init; } = string.Empty;
    /// <summary>Gets the request URI supplied by the client.</summary>
    public string Uri { get; init; } = "/";
    /// <summary>Gets the request payload bytes.</summary>
    public byte[] Body { get; init; } = [];
    /// <summary>Gets the request payload's media type.</summary>
    public string ContentType { get; init; } = string.Empty;
    internal TaskCompletionSource<HttpResponseItem> ResponseCompletion { get; init; } = null!;
    internal string? ReadError { get; init; }
}
