// Represents parsed expression trees and decoded literals.

namespace Cop.Providers.SourceModel;

/// <summary>
/// Parsed expression facts, not source text. Unsupported syntax has kind "other";
/// string literals contain their decoded value, without quotes or escape syntax.
/// </summary>
public sealed record ExpressionInfo(string Kind)
{
    /// <summary>
    /// The declared or referenced name represented by this source element.
    /// </summary>
    public string? Name { get; init; }
    /// <summary>
    /// The decoded string-literal value, or null for other expressions.
    /// </summary>
    public string? StringValue { get; init; }
    /// <summary>
    /// The decoded Boolean-literal value, or null for other expressions.
    /// </summary>
    public bool? BooleanValue { get; init; }
    /// <summary>
    /// The target expression of member access or invocation, when present.
    /// </summary>
    public ExpressionInfo? Target { get; init; }
    /// <summary>
    /// The immediate child elements in source order.
    /// </summary>
    public List<ExpressionInfo> Children { get; init; } = [];
}
