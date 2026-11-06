// Models an OpenAPI operation and its source provenance.

namespace Cop.Providers.OpenApi;

/// <summary>
/// An OpenAPI operation declared under a path item.
/// </summary>
public record OpenApiOperationInfo(
    string Method,
    string Path,
    string OperationId,
    bool HasSummary,
    bool HasResponses,
    int Line)
{
    /// <summary>Gets the specification source file declaring this operation.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
