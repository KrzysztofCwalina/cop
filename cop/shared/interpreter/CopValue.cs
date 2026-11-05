// Base class for all runtime values in the Cop language.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Base class for all runtime values in the Cop language.
/// The evaluator ALWAYS returns a CopValue (never C# null).
/// Use CopNull.Instance for the language's null value.
/// </summary>
public abstract class CopValue
{
    /// <summary>
    /// Truthiness: all values are truthy except CopNull and CopBool(false).
    /// </summary>
    public virtual bool IsTruthy => true;

    /// <summary>
    /// Display representation for interpolation / print.
    /// </summary>
    public abstract string Display();
}
