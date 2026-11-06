// Stores a TypeSpec token's category, text, and source position.

using System.Text;

namespace Cop.Providers.TypeSpec;

/// <summary>Stores a TypeSpec token's category, text, and source position.</summary>
public record TspToken(TspTokenKind Kind, string Value, int Line, int Column);
