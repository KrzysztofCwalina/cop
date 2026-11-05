// Models script-wide shell settings and source provenance.

namespace Cop.Providers.Bash;

/// <summary>
/// Summary information for one parsed Bash/Shell script.
/// </summary>
public record ShellScriptInfo(bool HasStrictMode, int Line = 1)
{
    /// <summary>Gets the parsed source file for this script.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
