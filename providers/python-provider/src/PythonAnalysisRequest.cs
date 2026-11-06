// Carries source identity and optional syntax coordinates for Python helper requests.

namespace Cop.Providers.SourceParsers;

internal sealed record PythonAnalysisRequest(string Path, string Source)
{
    internal string Mode { get; init; } = "source";
    internal int? Line { get; init; }
    internal int? ByteColumn { get; init; }
    internal int? EndLine { get; init; }
    internal int? EndByteColumn { get; init; }
}
