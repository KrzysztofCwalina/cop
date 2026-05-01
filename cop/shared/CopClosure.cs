namespace Cop.Lang;

/// <summary>
/// A closure representing a partially-applied function.
/// Created when a function is called with fewer arguments than it requires.
/// Invoking the closure with the remaining arguments completes the call.
/// </summary>
public class CopClosure
{
    public FunctionDefinition Function { get; }
    public List<object?> BoundArgs { get; }

    public CopClosure(FunctionDefinition function, List<object?> boundArgs)
    {
        Function = function;
        BoundArgs = boundArgs;
    }

    /// <summary>
    /// Number of remaining arguments needed to fully apply the function.
    /// </summary>
    public int RemainingArgs => Function.Parameters.Count - BoundArgs.Count;

    public override string ToString() =>
        $"{Function.Name}({string.Join(", ", BoundArgs.Select(a => a?.ToString() ?? "null"))}, ...)";
}
