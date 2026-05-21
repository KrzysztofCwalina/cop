namespace Cop.Lang;

public record ScriptFile(
    string FilePath,
    List<string> Imports,
    List<TypeDefinition> TypeDefinitions,
    List<CollectionDeclaration> CollectionDeclarations,
    List<LetDeclaration> LetDeclarations,
    List<PredicateDefinition> Predicates,
    List<FunctionDefinition> Functions,
    List<CommandBlock> Commands,
    List<RunInvocation>? RunInvocations = null,
    List<string>? FeedPaths = null,
    List<FlagsDefinition>? FlagsDefinitions = null,
    List<EnumDefinition>? EnumDefinitions = null,
    List<TypeImportDeclaration>? TypeImports = null);
