// Describes a declared callable's parameter types and narrowing behavior.

namespace Cop.Lang.Interpreter;

/// <summary>
/// A predicate or function's display-form parameter and return types.
/// </summary>
public sealed record CallableInfo(
    string Name,
    bool IsPredicate,
    IReadOnlyList<string?> ParamTypes,
    string? ReturnType,
    bool IsNarrowing);
