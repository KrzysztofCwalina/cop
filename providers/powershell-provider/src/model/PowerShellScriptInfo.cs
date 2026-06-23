namespace Cop.Providers.PowerShell;

/// <summary>
/// Summary information for one parsed PowerShell script.
/// </summary>
public record PowerShellScriptInfo(bool UsesStrictMode, int Line = 1)
{
    public SourceModel.SourceFile? File { get; init; }
    public string Source => $"{File?.Path}:{Line}";
}
