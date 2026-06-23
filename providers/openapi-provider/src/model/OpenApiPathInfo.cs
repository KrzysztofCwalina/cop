namespace Cop.Providers.OpenApi;

/// <summary>
/// An OpenAPI path item declared under the top-level paths mapping.
/// </summary>
public record OpenApiPathInfo(string Path, int Line)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}

