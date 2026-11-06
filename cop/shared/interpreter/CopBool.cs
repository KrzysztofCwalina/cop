// An interpreter boolean value backed by shared true and false instances.

using Cop.Core;
using Cop.Lang.Ast;
using System.Text.Json.Serialization;

namespace Cop.Lang.Interpreter;

/// <summary>
/// An interpreter boolean value backed by shared true and false instances.
/// </summary>
public sealed class CopBool : CopValue
{
    public static readonly CopBool True = new(true);
    public static readonly CopBool False = new(false);

    /// <summary>
    /// The CLR boolean represented by this language value.
    /// </summary>
    [JsonPropertyName("Value")]
    public bool IsTrue { get; }
    private CopBool(bool value) => IsTrue = value;
    /// <summary>
    /// Returns the shared language value corresponding to a CLR boolean.
    /// </summary>
    public static CopBool Of(bool value) => value ? True : False;
    /// <inheritdoc/>
    public override bool IsTruthy => IsTrue;
    /// <inheritdoc/>
    public override string Display() => IsTrue ? "true" : "false";
    /// <inheritdoc/>
    public override string ToString() => Display();
}
