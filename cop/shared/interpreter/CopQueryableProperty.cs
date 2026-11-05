// Intermediate value representing a property access on a queryable collection.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Intermediate value representing a property access on a queryable collection.
/// Used for compound filter patterns like: queryable:PropertyName:stringOp(value).
/// The next filter in the chain provides the operation and value.
/// </summary>
public sealed class CopQueryableProperty : CopValue
{
    public CopQueryable Source { get; }
    public string PropertyName { get; }

    public CopQueryableProperty(CopQueryable source, string propertyName)
    {
        Source = source;
        PropertyName = propertyName;
    }

    public override string Display() => $"<queryable-property {Source.ProviderName}.{PropertyName}>";
    public override string ToString() => Display();
}
