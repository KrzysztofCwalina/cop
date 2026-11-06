// Groups related HTTP operations under a service identity.

namespace Cop.Providers.TypeSpec;

/// <summary>Groups related HTTP operations under a service identity.</summary>
public class HttpService
{
    /// <summary>Gets or sets the service's display name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the scope used to group the service's operations.</summary>
    public string Namespace { get; set; } = string.Empty;
    /// <summary>Gets or sets the HTTP operations belonging to the service.</summary>
    public List<HttpOperation> Operations { get; set; } = [];
    /// <summary>
    /// Gets or sets the service authentication description, if available.
    /// </summary>
    public string? Auth { get; set; }
}
