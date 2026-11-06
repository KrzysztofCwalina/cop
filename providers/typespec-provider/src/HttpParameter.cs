// Describes how an operation parameter is carried in an HTTP request.

namespace Cop.Providers.TypeSpec;

/// <summary>Describes how an operation parameter is carried in an HTTP request.</summary>
public class HttpParameter
{
    /// <summary>Gets or sets the HTTP parameter's name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the parameter's TypeSpec type expression.</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets its location: path, query, header, cookie, or body.
    /// </summary>
    public string In { get; set; } = "query"; // path, query, header, cookie, body
    /// <summary>Gets or sets whether the parameter may be omitted.</summary>
    public bool IsOptional { get; set; }
    /// <summary>Gets or sets the parameter serialization style, if specified.</summary>
    public string? Style { get; set; }
}
