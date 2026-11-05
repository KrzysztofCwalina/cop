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
    public abstract IEnumerable<string> GetFieldNames(object obj);
    public abstract CopValue GetField(object obj, string name);
    public abstract string Display(object obj);
    public virtual string? TypeName => null;
}
