// Selects interpreter-backed Python analysis or an explicitly source-only parse.

namespace Cop.Providers.SourceParsers;

/// <summary>Chooses how Python syntax and binding facts are obtained.</summary>
public enum PythonAnalysisBackend
{
    /// <summary>
    /// Use an interpreter when available and warn before source-only fallback.
    /// </summary>
    PreferNative,
    /// <summary>
    /// Use only the source parser without probing or launching an interpreter.
    /// </summary>
    SyntaxOnly
}
