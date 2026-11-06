// Describes the HTTP binding of a TypeSpec operation.

namespace Cop.Providers.TypeSpec;

/// <summary>Describes the HTTP binding of a TypeSpec operation.</summary>
public class HttpOperation
{
    /// <summary>Gets or sets the source operation's name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the HTTP request method.</summary>
    public string Verb { get; set; } = string.Empty;
    /// <summary>Gets or sets the operation's resolved route path.</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the URI template including route parameter placeholders.
    /// </summary>
    public string UriTemplate { get; set; } = string.Empty;
    /// <summary>Gets or sets the operation's HTTP parameter bindings.</summary>
    public List<HttpParameter> Parameters { get; set; } = [];
    /// <summary>Gets or sets the operation's declared HTTP responses.</summary>
    public List<HttpResponse> Responses { get; set; } = [];
    /// <summary>Gets or sets the source interface name, if any.</summary>
    public string? Interface { get; set; }
    /// <summary>Gets or sets decorators retained from the source operation.</summary>
    public List<TspDecorator> Decorators { get; set; } = [];
}
