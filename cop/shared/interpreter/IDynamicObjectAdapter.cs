// Abstract base for adapting provider objects (DataObject, etc.) to the evaluator.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Abstract base for adapting provider objects (DataObject, etc.) to the evaluator.
/// Implemented by the runtime bridge — keeps provider knowledge out of core.
/// </summary>
public abstract class IDynamicObjectAdapter
{
    /// <summary>
    /// Lists the member names exposed by a wrapped object.
    /// </summary>
    public abstract IEnumerable<string> GetFieldNames(object obj);
    /// <summary>
    /// Reads a wrapped member and converts it to a language value.
    /// </summary>
    public abstract CopValue GetField(object obj, string name);

    /// <summary>Reads a member, awaiting provider preparation only when required.</summary>
    public virtual ValueTask<CopValue> GetFieldAsync(object obj, string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetField(obj, name));
    }
    /// <summary>
    /// Formats a wrapped object for language output and interpolation.
    /// </summary>
    public abstract string Display(object obj);

    /// <summary>Formats an object after any demanded asynchronous member preparation.</summary>
    public virtual ValueTask<string> DisplayAsync(object obj,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Display(obj));
    }
    /// <summary>
    /// The optional Cop type name exposed for wrapped objects.
    /// </summary>
    public virtual string? TypeName => null;
}
