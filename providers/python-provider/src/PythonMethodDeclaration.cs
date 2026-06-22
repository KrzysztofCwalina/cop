namespace Cop.Providers.SourceModel;

/// <summary>
/// A Python-specific <see cref="MethodDeclaration"/> carrying Python-only method facts.
/// Only the Python provider emits these, and they round-trip through the source cache.
/// </summary>
public sealed record PythonMethodDeclaration : MethodDeclaration
{
    public PythonMethodDeclaration(MethodDeclaration source, bool isGenerator = false)
        : base(source)
    {
        IsGenerator = isGenerator;
    }

    /// <summary>True for methods decorated with <c>@staticmethod</c>.</summary>
    public bool IsStaticmethod => HasDecorator("staticmethod");

    /// <summary>True for methods decorated with <c>@classmethod</c>.</summary>
    public bool IsClassmethod => HasDecorator("classmethod");

    /// <summary>True for methods decorated with <c>@property</c>.</summary>
    public bool IsProperty => HasDecorator("property");

    /// <summary>True when the method body contains a <c>yield</c> statement.</summary>
    public bool IsGenerator { get; init; }

    /// <summary>True for methods decorated with <c>@abstractmethod</c>.</summary>
    public bool IsAbstractMethod => HasDecorator("abstractmethod");

    /// <summary>True for Python dunder methods such as <c>__str__</c>.</summary>
    public bool IsDunder => Name.Length > 4 && Name.StartsWith("__") && Name.EndsWith("__");

    public override string? LanguageTag => "python";

    public override IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags =>
    [
        new("IsGenerator", IsGenerator),
    ];

    public static void RegisterCacheFactory() =>
        MethodTypeRegistry.Register("python", (baseDecl, flags) => new PythonMethodDeclaration(
            baseDecl,
            isGenerator: flags.TryGetValue("IsGenerator", out var g) ? g : ContainsYield(baseDecl)));

    private bool HasDecorator(string name) => Decorators.Exists(d => IsDecorator(d, name));

    private static bool IsDecorator(string decorator, string name) =>
        decorator == name || decorator.EndsWith("." + name, StringComparison.Ordinal);

    private static bool ContainsYield(MethodDeclaration method) =>
        method.Statements.Exists(s => s.Kind == "yield");
}

public static class PythonMethodDeclarationExtensions
{
    public static PythonMethodDeclaration AsPython(this MethodDeclaration source, bool isGenerator = false)
        => new(source, isGenerator);
}
