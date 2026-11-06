// Adds C#-specific method facts to the shared source model.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A C#-specific <see cref="MethodDeclaration"/> carrying language-specific facts that have
/// no place in the language-agnostic common model (e.g. extension methods, partial methods,
/// unsafe / expression-bodied / generic methods).
///
/// Only the C# provider emits these. The runtime maps this CLR type to the cop type
/// <c>CSharpMethod</c> (declared in the csharp package), so checks that narrow with
/// <c>:asCSharp</c> can read these fields while the common <c>Method</c> stays clean.
/// </summary>
public sealed record CSharpMethodDeclaration : MethodDeclaration
{
    internal int SyntaxStart { get; set; } = -1;
    /// <summary>
    /// Copies a shared method before C#-specific facts are initialized.
    /// </summary>
    public CSharpMethodDeclaration(MethodDeclaration source)
        : base(source)
    {
    }

    /// <summary>True for C# extension members or methods whose first parameter has the <c>this</c> modifier.</summary>
    public bool IsExtension { get; init; }

    /// <summary>True for <c>partial</c> method declarations.</summary>
    public bool IsPartial { get; init; }

    /// <summary>True for <c>unsafe</c> method declarations.</summary>
    public bool IsUnsafe { get; init; }

    /// <summary>True for <c>extern</c> method declarations (e.g. P/Invoke).</summary>
    public bool IsExtern { get; init; }

    /// <summary>True for expression-bodied methods (<c>=&gt; expr;</c>).</summary>
    public bool IsExpressionBodied { get; init; }

    /// <summary>True when the method declares generic type parameters.</summary>
    public bool IsGeneric { get; init; }
}
