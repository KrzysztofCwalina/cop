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
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}

