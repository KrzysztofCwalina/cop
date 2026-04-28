namespace Cop.Lang;

/// <summary>
/// A named property with a value. In the cop list model, every object
/// is conceptually a [Property] — a list of named values.
/// Property access (obj.Name) is syntactic sugar for filtering
/// the property list by name and extracting the value.
/// </summary>
public record Property(string Name, object? Value);
