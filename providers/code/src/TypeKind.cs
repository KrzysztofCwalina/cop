// Classifies language-independent type declarations.

namespace Cop.Providers.SourceModel;

public enum TypeKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Alias
}
