// Models PowerShell script-wide settings and source provenance.

namespace Cop.Providers.PowerShell;

/// <summary>
/// Summary information for one parsed PowerShell script.
/// </summary>
public record PowerShellScriptInfo(bool UsesStrictMode, int Line = 1)
{
    /// <summary>Gets the parsed source file for this PowerShell script.</summary>
    public SourceModel.SourceFile? File { get; init; }
    /// <summary>Gets the file path and line used to locate diagnostics.</summary>
    public string Source => $"{File?.Path}:{Line}";
}
