// Describes a named HTTP response header and its value type.

namespace Cop.Providers.TypeSpec;

/// <summary>Describes a named HTTP response header and its value type.</summary>
public class HttpHeader
{
    /// <summary>Gets or sets the header's HTTP field name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the header value's TypeSpec type expression.</summary>
    public string Type { get; set; } = string.Empty;
}
