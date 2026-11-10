// Identifies the declarations that count as individual checks in execution summaries.

using Cop.Lang.Ast;
using Cop.Lang.Interpreter;

namespace Cop.Providers;

/// <summary>
/// Finds the leaf checks of a program so the run summary can count them without authors
/// annotating anything. A leaf check is a top-level <c>let</c> whose type is <c>[Violation]</c>,
/// or an exported function declared to return <c>[Violation]</c>, unless its value only combines
/// other checks with <c>+</c> (a suite) or just refers to one (an alias).
/// </summary>
internal sealed class LeafCheckCatalog
{
    private const string ViolationType = "Violation";

    private readonly Dictionary<Declaration, string> _checks = new(ReferenceEqualityComparer.Instance);

    private LeafCheckCatalog() { }

    /// <summary>Returns the check identity of <paramref name="declaration"/>, or null when it is not a leaf check.</summary>
    public string? NameOf(Declaration declaration) => _checks.GetValueOrDefault(declaration);

    /// <summary>Classifies the leaf checks declared by <paramref name="modules"/>.</summary>
    public static LeafCheckCatalog Build(IReadOnlyList<(ModuleNode Module, string FilePath)> modules)
    {
        LeafCheckCatalog catalog = new LeafCheckCatalog();
        if (!modules.Any(m => m.Module.Declarations.Any(d => d is TypeDecl { Name: ViolationType })))
        {
            return catalog;
        }

        SemanticModel model = SemanticModel.Build(modules.Select(m => m.Module));
        HashSet<string> violationMaps = ViolationMaps(modules);
        HashSet<string> checkFunctions = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> checkLets = new HashSet<string>(StringComparer.Ordinal);
        List<(Declaration Declaration, Expression? Value, string FilePath)> candidates = [];
        foreach ((ModuleNode module, string filePath) in modules)
        {
            foreach (Declaration declaration in module.Declarations)
            {
                if (declaration is FunctionDecl { IsExported: true, IsPredicate: false } function
                    && IsViolationList(function.ReturnType?.Name, function.ReturnType?.IsCollection == true))
                {
                    checkFunctions.Add(function.Name);
                    candidates.Add((function, (function.Body as ExpressionBody)?.Expr, filePath));
                }
                else if (declaration is LetDecl let)
                {
                    TypeInfo? type = let.TypeAnnotation is { } annotation
                        ? new TypeInfo(annotation.Name, annotation.IsCollection)
                        : model.InferExpressionType(let.Value);
                    if ((type is { } t && IsViolationList(t.Name, t.IsCollection)) || MapsToViolations(let.Value, violationMaps))
                    {
                        checkLets.Add(let.Name);
                        candidates.Add((let, let.Value, filePath));
                    }
                }
            }
        }

        foreach ((Declaration declaration, Expression? value, string filePath) in candidates)
        {
            if (value is null || !CombinesChecks(value, checkFunctions, checkLets))
            {
                string name = declaration is FunctionDecl f ? f.Name : ((LetDecl)declaration).Name;
                catalog._checks[declaration] = $"{filePath}:{declaration.Line}:{name}";
            }
        }

        return catalog;
    }

    // Overloaded mappers such as toError(item, message) defeat single-signature type inference,
    // so collect the functions whose every overload produces one Violation per item.
    private static HashSet<string> ViolationMaps(IReadOnlyList<(ModuleNode Module, string FilePath)> modules)
    {
        Dictionary<string, bool> all = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (FunctionDecl function in modules.SelectMany(m => m.Module.Declarations).OfType<FunctionDecl>())
        {
            bool producesViolation = !function.IsPredicate && (function.ReturnType is { } returnType
                ? returnType.Name == ViolationType && !returnType.IsCollection
                : function.Body is ExpressionBody { Expr: ObjectExpr { TypeHint: ViolationType } });
            all[function.Name] = all.GetValueOrDefault(function.Name, true) && producesViolation;
        }

        return all.Where(kv => kv.Value).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static bool MapsToViolations(Expression? value, HashSet<string> violationMaps) =>
        value is FilterExpr filter && filter.Predicate switch
        {
            CallExpr { Callee: IdentifierExpr callee } => violationMaps.Contains(callee.Name),
            IdentifierExpr identifier => violationMaps.Contains(identifier.Name),
            _ => false
        };

    private static bool IsViolationList(string? typeName, bool isCollection) =>
        isCollection && string.Equals(typeName, ViolationType, StringComparison.Ordinal);

    private static bool CombinesChecks(Expression expression, HashSet<string> checkFunctions, HashSet<string> checkLets) =>
        expression switch
        {
            BinaryExpr { Op: BinaryOp.Add } add =>
                CombinesChecks(add.Left, checkFunctions, checkLets) && CombinesChecks(add.Right, checkFunctions, checkLets),
            IdentifierExpr identifier => checkLets.Contains(identifier.Name),
            MemberExpr member => checkLets.Contains(member.Member),
            CallExpr { Callee: IdentifierExpr callee } => checkFunctions.Contains(callee.Name),
            CallExpr { Callee: MemberExpr callee } => checkFunctions.Contains(callee.Member),
            _ => false
        };
}
