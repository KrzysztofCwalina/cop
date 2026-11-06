// Presents property type information resolved by the semantic model.

namespace Cop.Lang.Interpreter;

/// <summary>
/// A property's name, display-form type, and the name of its declaring type.
/// </summary>
public sealed record PropertyInfo(string Name, string Type, string DeclaringType);
