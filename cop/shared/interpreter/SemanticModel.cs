// Exposes symbol, type, and syntax relationships for language-tooling consumers.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;
using Cop.Lang.Parser;

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
        SemanticModel model = new SemanticModel(TypeChecker.ForModel(modules));
        if (namespaces is not null)
        {
            foreach ((string? ns, List<ModuleNode>? mods) in namespaces)
            {
                List<string> names = new List<string>();
                foreach (ModuleNode m in mods)
                {
                    foreach (Declaration decl in m.Declarations)
                    {
                        if (decl is FunctionDecl { IsExported: true } fn && !names.Contains(fn.Name))
                        {
                            names.Add(fn.Name);
                        }
                    }
                }

                if (names.Count > 0)
                {
                    model._namespaceFunctions[ns] = names;
                }
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
        if (string.IsNullOrWhiteSpace(exprText))
        {
            return null;
        }

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
        LetDecl? let = probe.Declarations.OfType<LetDecl>().FirstOrDefault(d => d.Name == "__semanticmodel_probe");
        if (let?.Value is null)
        {
            return null;
        }

        return _checker.InferWithLocals(let.Value, locals);
    }

    /// <summary>Infers a parsed expression without reparsing source text.</summary>
    public TypeInfo? InferExpressionType(Expression expression, IReadOnlyDictionary<string, TypeInfo>? locals = null)
        => _checker.InferWithLocals(expression, locals);

    /// <summary>True if <paramref name="name"/> is a top-level <c>let</c> binding in the program.</summary>
    public bool IsLet(string name) { _checker.TopLevelLet(name, out bool found); return found; }

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

    /// <summary>Every overload of every declared predicate and function (for general/colon completion).</summary>
    public IReadOnlyList<CallableInfo> Callables()
    {
        List<CallableInfo> result = new List<CallableInfo>();
        foreach (string name in _checker.CallableNames())
        {
            result.AddRange(_checker.AllCallables(name));
        }

        return result;
    }

    /// <summary>Exported functions of an imported package namespace (e.g. <c>csharp</c>).</summary>
    public IReadOnlyList<CallableInfo> NamespaceFunctions(string ns)
    {
        if (!_namespaceFunctions.TryGetValue(ns, out List<string>? names))
        {
            return [];
        }

        List<CallableInfo> result = new List<CallableInfo>();
        foreach (string name in names)
        {
            CallableInfo? c = _checker.Callable(name);
            if (c is not null)
            {
                result.Add(c);
            }
        }
        return result;
    }

    /// <summary>True if <paramref name="ns"/> is an imported package namespace with exports.</summary>
    public bool IsNamespace(string ns) => _namespaceFunctions.ContainsKey(ns);

    /// <summary>True if a predicate declared for <paramref name="paramType"/> applies to a value of
    /// <paramref name="elementType"/> (same type, or element is a subtype/conformer).</summary>
    public bool PredicateApplies(string? paramType, string? elementType)
    {
        if (paramType is null || elementType is null)
        {
            return true; // unknown ⇒ offer it
        }

        if (string.Equals(paramType, elementType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _checker.IsSubtypeOf(elementType, paramType);
    }

    // ── String-level type helpers (operate on display forms) ──────────────────

    /// <summary>
    /// Whether a display-form type name starts with a collection marker.
    /// </summary>
    public static bool IsCollectionType(string type) => type.StartsWith('[');

    /// <summary>
    /// Unwraps a bracketed collection type, leaving other type names unchanged.
    /// </summary>
    public static string ElementType(string type) =>
        type.StartsWith('[') && type.EndsWith(']') ? type[1..^1] : type;

    /// <summary>
    /// Whether a type name denotes string, including its nullable form.
    /// </summary>
    public static bool IsStringType(string type) => StripNullable(type) == "string";

    /// <summary>
    /// Whether a type name denotes int or float, including nullable forms.
    /// </summary>
    public static bool IsNumericType(string type) => StripNullable(type) is "int" or "float";

    private static string StripNullable(string type) => type.EndsWith('?') ? type[..^1] : type;
}
