namespace Cop.Providers.SourceModel;

public record StatementInfo(
    string Kind,
    List<string> Keywords,
    string? TypeName,
    string? MemberName,
    List<string> Arguments,
    int Line,
    bool IsInMethod)
{
    public SourceFile? File { get; init; }
    public bool HasRethrow { get; init; }
    public bool IsErrorHandler { get; init; }
    public bool IsGenericErrorHandler { get; init; }
    public string Source => $"{File?.Path}:{MemberName}";
}
