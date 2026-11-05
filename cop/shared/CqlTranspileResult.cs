// Carries generated CodeQL files and diagnostics from transpilation.

namespace Cop.Lang;

/// <summary>
/// Result of translating Cop source into CodeQL query files.
/// </summary>
public record CqlTranspileResult(List<CqlQueryFile> Files, List<string> Errors)
{
    /// <summary>Whether transpilation reported any errors.</summary>
    public bool HasErrors => Errors.Count > 0;
}
