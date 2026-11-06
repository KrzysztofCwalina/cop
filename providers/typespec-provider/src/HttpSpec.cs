// Interprets TypeSpec HTTP decorators to produce service and operation metadata.

namespace Cop.Providers.TypeSpec;

/// <summary>
/// HTTP protocol graph types produced by the HTTP transformer.
/// Mirrors TypeSpec's @typespec/http layer.
/// </summary>
public class HttpSpec
{
    /// <summary>
    /// Gets or sets the service groupings derived from HTTP operations.
    /// </summary>
    public List<HttpService> Services { get; set; } = [];
    /// <summary>Gets or sets all transformed HTTP operations.</summary>
    public List<HttpOperation> Operations { get; set; } = [];
}
