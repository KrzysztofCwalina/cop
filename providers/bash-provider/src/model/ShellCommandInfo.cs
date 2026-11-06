// Models a shell command and its source location.

namespace Cop.Providers.Bash;

/// <summary>
/// A simple shell command discovered in a Bash/Shell script.
/// </summary>
public record ShellCommandInfo(string Name, string Text, int Line)
{
    /// <summary>Gets the parsed source file containing this command.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
