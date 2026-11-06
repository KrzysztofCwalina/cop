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
    /// <summary>
    /// The deferred collection from which this property is projected.
    /// </summary>
    public CopQueryable Source { get; }
    /// <summary>
    /// The element member selected by the deferred projection.
    /// </summary>
    public string PropertyName { get; }

    public CopQueryableProperty(CopQueryable source, string propertyName)
    {
        Source = source;
        PropertyName = propertyName;
    }

    /// <inheritdoc/>
    public override string Display() => $"<queryable-property {Source.ProviderName}.{PropertyName}>";
    /// <inheritdoc/>
    public override string ToString() => Display();
}
