// Records the name and declared type of a function input.

namespace Cop.Lang;

/// <summary>Names a function parameter and its declared Cop type.</summary>
public record FunctionParameter(string Name, string TypeName);
