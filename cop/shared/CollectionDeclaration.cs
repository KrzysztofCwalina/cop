namespace Cop.Lang;

/// <summary>
/// A collection declaration: collection Types : [Type]
/// </summary>
public record CollectionDeclaration(
    string Name,
    string ItemType,
    int Line,
    bool IsExported = false);