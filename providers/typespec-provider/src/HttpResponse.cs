// Describes an operation's response status, body type, and headers.

namespace Cop.Providers.TypeSpec;

/// <summary>Describes an operation's response status, body type, and headers.</summary>
public class HttpResponse
{
    /// <summary>
    /// Gets or sets the response status code or status-code expression.
    /// </summary>
    public string StatusCode { get; set; } = "200";
    /// <summary>Gets or sets the response documentation, if available.</summary>
    public string? Description { get; set; }
    /// <summary>
    /// Gets or sets the response body's TypeSpec type expression, if any.
    /// </summary>
    public string? Body { get; set; }
    /// <summary>Gets or sets the declared response header bindings.</summary>
    public List<HttpHeader> Headers { get; set; } = [];
}
