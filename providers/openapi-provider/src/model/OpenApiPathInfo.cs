// Models an OpenAPI route path and its source provenance.

namespace Cop.Providers.OpenApi;

/// <summary>
/// An OpenAPI path item declared under the top-level paths mapping.
/// </summary>
public record OpenApiPathInfo(string Path, int Line)
{
    /// <summary>Gets the specification source file declaring this path.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
