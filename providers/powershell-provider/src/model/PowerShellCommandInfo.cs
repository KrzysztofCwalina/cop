// Models a PowerShell command invocation and its source location.

namespace Cop.Providers.PowerShell;

/// <summary>
/// A PowerShell command discovered in a script.
/// </summary>
public record PowerShellCommandInfo(string Name, string Text, int Line)
{
    /// <summary>Gets the PowerShell source file containing this command.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
