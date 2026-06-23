namespace Cop.Providers.Bash;

/// <summary>
/// A simple shell command discovered in a Bash/Shell script.
/// </summary>
public record ShellCommandInfo(string Name, string Text, int Line)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}

