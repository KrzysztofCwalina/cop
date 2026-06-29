namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;
using Cop.Lang.Parser;

/// <summary>An inferred type: a type name plus whether it is a collection. <c>[Name]</c> when a collection.</summary>
public readonly record struct TypeInfo(string Name, bool IsCollection)
{
    /// <summary>Display form: <c>[Name]</c> for a collection, otherwise <c>Name</c>.</summary>
    public string Display => IsCollection ? $"[{Name}]" : Name;
}

/// <summary>A property of a type: its name, type (display form), and the type that declares it.</summary>
public sealed record PropertyInfo(string Name, string Type, string DeclaringType);

/// <summary>A declared predicate or function: parameter types and return type (display forms).</summary>
public sealed record CallableInfo(
    string Name,
    bool IsPredicate,
    IReadOnlyList<string?> ParamTypes,
    string? ReturnType,
    bool IsNarrowing);

/// <summary>
/// A read-only semantic view of a parsed cop program, backing editor features (hover, completion)
/// with the SAME type model and inference engine that <c>cop verify</c> uses (<see cref="TypeChecker"/>).
/// This is the single source of truth for "what type is this expression / what members does this type
/// have" — tooling consumes it instead of reimplementing the compiler.
/// </summary>
public sealed class SemanticModel
{
    private readonly TypeChecker _checker;
    private readonly Dictionary<string, List<string>> _namespaceFunctions = new(StringComparer.Ordinal);

    private SemanticModel(TypeChecker checker) => _checker = checker;

    /// <summary>Builds the semantic model from every module of a program (a cop-checks/ directory).</summary>
    public static SemanticModel Build(IEnumerable<ModuleNode> modules) => Build(modules, null);

    /// <summary>
    /// Builds the semantic model. <paramref name="namespaces"/> maps an imported package name to its
    /// modules so namespace-qualified completions (<c>csharp.parse</c>) can be offered.
    /// </summary>
    public static SemanticModel Build(
        IEnumerable<ModuleNode> modules,
        IReadOnlyDictionary<string, List<ModuleNode>>? namespaces)
    {
        var model = new SemanticModel(TypeChecker.ForModel(modules));
        if (namespaces is not null)
        {
            foreach (var (ns, mods) in namespaces)
            {
                var names = new List<string>();
                foreach (var m in mods)
                    foreach (var decl in m.Declarations)
                        if (decl is FunctionDecl { IsExported: true } fn && !names.Contains(fn.Name))
                            names.Add(fn.Name);
                if (names.Count > 0) model._namespaceFunctions[ns] = names;
            }
        }
        return model;
    }

    /// <summary>
    /// Infers the type of an expression given as source text (e.g. a dot/filter chain extracted at
    /// the cursor), resolved against the whole program. <paramref name="locals"/> supplies in-scope
    /// names such as the implicit <c>item</c> or a predicate parameter. Returns null when the type
    /// cannot be determined (which the editor renders as "unknown" rather than a wrong guess).
    /// </summary>
    public TypeInfo? InferExpressionType(string exprText, IReadOnlyDictionary<string, TypeInfo>? locals = null)
    {
        if (string.IsNullOrWhiteSpace(exprText)) return null;
        ModuleNode probe;
        try
        {
            // Reuse the REAL parser: wrap the chain as a let value so a single expression parses.
            probe = CopParser.Parse("let __semanticmodel_probe = " + exprText, "__semanticmodel_probe__");
        }
        catch
        {
            return null;
        }
        var let = probe.Declarations.OfType<LetDecl>().FirstOrDefault(d => d.Name == "__semanticmodel_probe");
        if (let?.Value is null) return null;
        return _checker.InferWithLocals(let.Value, locals);
    }

    /// <summary>True if <paramref name="name"/> is a top-level <c>let</c> binding in the program.</summary>
    public bool IsLet(string name) { _checker.TopLevelLet(name, out var found); return found; }

    /// <summary>The type of a top-level <c>let</c> (null if unknown or not a let).</summary>
    public TypeInfo? LetType(string name) => _checker.TopLevelLet(name, out _);

    /// <summary>All top-level <c>let</c> names.</summary>
    public IReadOnlyCollection<string> LetNames() => _checker.LetNames();

    /// <summary>True if a type with this name is declared (locally or by a provider/package).</summary>
    public bool IsKnownType(string name) => _checker.IsKnownType(name);

    /// <summary>True if this is an enum or flags type.</summary>
    public bool IsEnum(string name) => _checker.IsEnumName(name);

    /// <summary>All known type names.</summary>
    public IReadOnlyCollection<string> TypeNames() => _checker.KnownTypes();

    /// <summary>All properties of a type, including inherited ones.</summary>
    public IReadOnlyList<PropertyInfo> PropertiesOf(string typeName) => _checker.AllProperties(typeName);

    /// <summary>A single property of a type (walking base types), or null if absent.</summary>
    public PropertyInfo? PropertyOf(string typeName, string member) => _checker.FindProperty(typeName, member);

    /// <summary>Signature of a declared predicate or function, or null if not declared.</summary>
    public CallableInfo? Callable(string name) => _checker.Callable(name);

    /// <summary>Every declared predicate and function (for general/colon completion).</summary>
    public IReadOnlyList<CallableInfo> Callables()
    {
        var result = new List<CallableInfo>();
        foreach (var name in _checker.CallableNames())
        {
            var c = _checker.Callable(name);
            if (c is not null) result.Add(c);
        }
        return result;
    }

    /// <summary>Exported functions of an imported package namespace (e.g. <c>csharp</c>).</summary>
    public IReadOnlyList<CallableInfo> NamespaceFunctions(string ns)
    {
        if (!_namespaceFunctions.TryGetValue(ns, out var names)) return [];
        var result = new List<CallableInfo>();
        foreach (var name in names)
        {
            var c = _checker.Callable(name);
            if (c is not null) result.Add(c);
        }
        return result;
    }

    /// <summary>True if <paramref name="ns"/> is an imported package namespace with exports.</summary>
    public bool IsNamespace(string ns) => _namespaceFunctions.ContainsKey(ns);

    /// <summary>True if a predicate declared for <paramref name="paramType"/> applies to a value of
    /// <paramref name="elementType"/> (same type, or element is a subtype/conformer).</summary>
    public bool PredicateApplies(string? paramType, string? elementType)
    {
        if (paramType is null || elementType is null) return true; // unknown ⇒ offer it
        if (string.Equals(paramType, elementType, StringComparison.OrdinalIgnoreCase)) return true;
        return _checker.IsSubtypeOf(elementType, paramType);
    }

    // ── String-level type helpers (operate on display forms) ──────────────────

    public static bool IsCollectionType(string type) => type.StartsWith('[');

    public static string ElementType(string type) =>
        type.StartsWith('[') && type.EndsWith(']') ? type[1..^1] : type;

    public static bool IsStringType(string type) => StripNullable(type) == "string";

    public static bool IsNumericType(string type) => StripNullable(type) is "int" or "float";

    private static string StripNullable(string type) => type.EndsWith('?') ? type[..^1] : type;
}
