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
    public CSharpMethodDeclaration(
        MethodDeclaration source,
        bool isExtension = false,
        bool isPartial = false,
        bool isUnsafe = false,
        bool isExtern = false,
        bool isExpressionBodied = false,
        bool isGeneric = false)
        : base(source)
    {
        IsExtension = isExtension;
        IsPartial = isPartial;
        IsUnsafe = isUnsafe;
        IsExtern = isExtern;
        IsExpressionBodied = isExpressionBodied;
        IsGeneric = isGeneric;
    }

    /// <summary>True for C# extension methods (first parameter has the <c>this</c> modifier).</summary>
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

public static class CSharpMethodDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="MethodDeclaration"/> as a C#-specific one.</summary>
    public static CSharpMethodDeclaration AsCSharp(
        this MethodDeclaration source,
        bool isExtension = false,
        bool isPartial = false,
        bool isUnsafe = false,
        bool isExtern = false,
        bool isExpressionBodied = false,
        bool isGeneric = false)
        => new(source, isExtension, isPartial, isUnsafe, isExtern, isExpressionBodied, isGeneric);
}
