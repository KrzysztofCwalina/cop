// Groups the declarations and commands parsed from a Cop source file.

namespace Cop.Lang;

/// <summary>
/// Collects the declarations, imports, and commands belonging to one source file.
/// </summary>
public record ScriptFile(
    string FilePath,
    List<string> Imports,
    List<TypeDefinition> TypeDefinitions,
    List<CollectionDeclaration> CollectionDeclarations,
    List<LetDeclaration> LetDeclarations,
    List<PredicateDefinition> Predicates,
    List<FunctionDefinition> Functions,
    List<CommandBlock> Commands,
    List<string>? FeedPaths = null,
    List<FlagsDefinition>? FlagsDefinitions = null,
    List<EnumDefinition>? EnumDefinitions = null,
    List<TypeImportDeclaration>? TypeImports = null);
