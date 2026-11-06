// Classifies language-independent type declarations.

namespace Cop.Providers.SourceModel;

/// <summary>
/// The language-independent categories used for parsed type declarations.
/// </summary>
public enum TypeKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Alias
}
