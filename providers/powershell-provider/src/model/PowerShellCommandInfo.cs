namespace Cop.Providers.PowerShell;

/// <summary>
/// A PowerShell command discovered in a script.
/// </summary>
public record PowerShellCommandInfo(string Name, string Text, int Line)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}
