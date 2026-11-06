// Represents the HTTP response to send back to the client.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Cop.Core;
using Cop.Lang;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cop.Providers.Http;

/// <summary>
/// Represents the HTTP response to send back to the client.
/// </summary>
public class HttpResponseItem
{
    /// <summary>Gets the HTTP status code returned to the client.</summary>
    public int StatusCode { get; init; } = 200;
    /// <summary>Gets the response payload text.</summary>
    public string Body { get; init; } = string.Empty;
    /// <summary>Gets the response payload's media type.</summary>
    public string ContentType { get; init; } = "application/json";
}
