// Presents an inferred value type to semantic-model and editor consumers.

namespace Cop.Lang.Interpreter;

/// <summary>Identifies an inferred type and whether it represents a collection.</summary>
public readonly record struct TypeInfo(string Name, bool IsCollection)
{
    /// <summary>Display form: [Name] for a collection, otherwise Name.</summary>
    public string Display => IsCollection ? $"[{Name}]" : Name;
}
