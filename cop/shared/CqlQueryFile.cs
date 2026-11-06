// Carries a generated CodeQL query's output file name and source content.

namespace Cop.Lang;

/// <summary>A single generated CodeQL query file.</summary>
public record CqlQueryFile(string FileName, string Content);
