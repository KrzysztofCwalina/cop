namespace Cop.Providers.Bash;

/// <summary>
/// Summary information for one parsed Bash/Shell script.
/// </summary>
public record ShellScriptInfo(bool HasStrictMode, int Line = 1)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}

