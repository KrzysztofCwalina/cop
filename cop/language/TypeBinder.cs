namespace Cop.Lang;

/// <summary>
/// Static type validation pass for cop script files using 'import' (strict mode).
/// Validates that predicate parameter types exist, property access chains resolve
/// through the type schema, and PRINT placeholders reference valid properties.
/// </summary>
public class TypeBinder
{
    private readonly TypeRegistry _registry;
    private readonly List<string> _errors = [];

    public TypeBinder(TypeRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// Validates a script file against the type registry.
    /// Only runs for files that have imports (strict mode).
    /// Returns a list of binding errors (empty if valid).
    /// </summary>
    public List<string> Bind(ScriptFile file)
    {
        _errors.Clear();

        if (file.Imports.Count == 0)
            return []; // Legacy mode — no type validation

        // Validate predicate parameter types exist
        foreach (var pred in file.Predicates)
        {
            var paramType = pred.ParameterType;

            // Predicate paramType can be a collection reference like "[Statement]"
            // or a simple type name like "Type" or "Method"
            if (!_registry.HasType(paramType) && !IsCollectionReference(paramType))
            {
                _errors.Add($"{file.FilePath}({pred.Line}): type '{paramType}' is not defined");
                continue;
            }

            // Validate the body expression against the parameter type
            ValidateExpression(pred.Body, paramType, file.FilePath, pred.Line);
        }

        // Validate let declarations (before command blocks, so let-derived names can be resolved)
        foreach (var let in file.LetDeclarations)
        {
            // Value bindings (let Name = [...]) don't need collection-based validation
            if (let.IsValueBinding)
            {
                ValidateExpression(let.ValueExpression!, "Unknown", file.FilePath, let.Line);
                continue;
            }

            var baseItemType = ResolveCollectionItemType(let.BaseCollection, file);
            if (baseItemType is null)
            {
                _errors.Add($"{file.FilePath}({let.Line}): unknown base collection '{let.BaseCollection}'");
                continue;
            }

            foreach (var filter in let.Filters)
                ValidateExpression(filter, baseItemType, file.FilePath, let.Line);
        }

        // Validate command blocks
        foreach (var cmd in file.Commands)
        {
            // Bare PRINT("message") — no collection to validate
            if (cmd.Collection is null)
            {
                ValidateTemplate(cmd.MessageTemplate, null, file.FilePath, cmd.Line);
                continue;
            }

            // Validate collection exists (built-in or derived)
            var collectionItemType = ResolveCollectionItemType(cmd.Collection, file);
            if (collectionItemType is null)
            {
                _errors.Add($"{file.FilePath}({cmd.Line}): unknown collection '{cmd.Collection}'");
                continue;
            }

            // Validate inline filters
            foreach (var filter in cmd.Filters)
                ValidateExpression(filter, collectionItemType, file.FilePath, cmd.Line);

            // Resolve the final item type after filters (may differ from base type after map functions)
            var finalItemType = ResolveItemTypeAfterFilters(collectionItemType, cmd.Filters, file);

            // Validate PRINT message template placeholders
            ValidateTemplate(cmd.MessageTemplate, finalItemType, file.FilePath, cmd.Line);
        }

        return [.. _errors];
    }

    private void ValidateExpression(Expression expr, string contextType, string filePath, int line)
    {
        switch (expr)
        {
            case MemberAccessExpr ma:
                ValidateMemberAccess(ma, contextType, filePath, line);
                break;

            case PredicateCallExpr mc:
                ValidateExpression(mc.Target, contextType, filePath, line);
                foreach (var arg in mc.Args)
                    ValidateExpression(arg, contextType, filePath, line);
                break;

            case BinaryExpr bin:
                ValidateExpression(bin.Left, contextType, filePath, line);
                ValidateExpression(bin.Right, contextType, filePath, line);
                break;

            case UnaryExpr un:
                ValidateExpression(un.Operand, contextType, filePath, line);
                break;

            case FunctionCallExpr fc:
                foreach (var arg in fc.Args)
                    ValidateExpression(arg, contextType, filePath, line);
                break;

            case ListLiteralExpr list:
                foreach (var elem in list.Elements)
                    ValidateExpression(elem, contextType, filePath, line);
                break;

            case IdentifierExpr:
            case LiteralExpr:
                // These are always valid
                break;
        }
    }

    private void ValidateMemberAccess(MemberAccessExpr ma, string contextType, string filePath, int line)
    {
        // Resolve the target to a type name
        var targetType = InferExpressionType(ma.Target, contextType);
        if (targetType is null)
        {
            // Can't infer type — skip validation (don't report false positives)
            return;
        }

        var typeDesc = _registry.GetType(targetType);
        if (typeDesc is null) return; // Unknown type — skip

        var prop = typeDesc.GetProperty(ma.Member);
        if (prop is null)
        {
            _errors.Add($"{filePath}({line}): property '{ma.Member}' is not defined on type '{targetType}'");
        }
    }

    /// <summary>
    /// Infers the cop type name of an expression within a given context type.
    /// Returns null if the type cannot be determined.
    /// </summary>
    private string? InferExpressionType(Expression expr, string contextType)
    {
        return expr switch
        {
            IdentifierExpr id => ResolveIdentifierType(id.Name, contextType),
            MemberAccessExpr ma => InferMemberAccessType(ma, contextType),
            LiteralExpr lit => lit.Value switch
            {
                string => "string",
                int => "int",
                double => "number",
                bool => "bool",
                _ => null
            },
            _ => null
        };
    }

    private string? ResolveIdentifierType(string name, string contextType)
    {
        // If the identifier matches the context type name, it IS the context type
        if (_registry.HasType(name))
            return name;

        return contextType;
    }

    private string? InferMemberAccessType(MemberAccessExpr ma, string contextType)
    {
        var targetType = InferExpressionType(ma.Target, contextType);
        if (targetType is null) return null;

        var typeDesc = _registry.GetType(targetType);
        var prop = typeDesc?.GetProperty(ma.Member);
        if (prop is null) return null;

        // Return the property's declared type
        return prop.IsCollection ? null : prop.TypeName;
    }

    private string? ResolveCollectionItemType(string collection, ScriptFile file)
    {
        // Check registry for built-in collections
        var itemType = _registry.GetCollectionItemType(collection);
        if (itemType is not null) return itemType;

        // Check let declarations
        var letDecl = file.LetDeclarations.FirstOrDefault(l => l.Name == collection);
        if (letDecl is not null)
        {
            if (letDecl.IsCollectionUnion)
            {
                var firstElem = ((ListLiteralExpr)letDecl.ValueExpression!).Elements[0];
                return ResolveCollectionItemType(((IdentifierExpr)firstElem).Name, file);
            }
            if (letDecl.IsValueBinding) return null; // Value bindings are not collections
            return ResolveCollectionItemType(letDecl.BaseCollection, file);
        }

        // Check predicate-derived collections
        var pred = file.Predicates.FirstOrDefault(p => p.Name == collection);
        if (pred is not null)
            return pred.ParameterType;

        return null;
    }

    private void ValidateTemplate(string template, string? itemType, string filePath, int line)
    {
        var segments = TemplateParser.Parse(template);
        foreach (var segment in segments)
        {
            if (segment is not ExpressionSegment expr || expr.PropertyPath.Length < 2)
                continue;

            var typeName = expr.PropertyPath[0];

            // Resolve 'item' to the collection's final item type
            if (typeName == "item" && itemType is not null)
                typeName = itemType;

            if (!_registry.HasType(typeName))
                continue;

            var typeDesc = _registry.GetType(typeName)!;
            for (int i = 1; i < expr.PropertyPath.Length; i++)
            {
                var prop = typeDesc.GetProperty(expr.PropertyPath[i]);
                if (prop is null)
                {
                    var placeholder = string.Join('.', expr.PropertyPath);
                    _errors.Add($"{filePath}({line}): template placeholder '{placeholder}' — property '{expr.PropertyPath[i]}' is not defined on type '{typeDesc.Name}'");
                    break;
                }

                // Follow the property's type for the next segment
                if (i < expr.PropertyPath.Length - 1 && !prop.IsCollection)
                {
                    typeDesc = _registry.GetType(prop.TypeName)!;
                    if (typeDesc is null) break;
                }
            }
        }
    }

    /// <summary>
    /// Resolves the final item type after applying filters. Map functions may change the type.
    /// </summary>
    private string? ResolveItemTypeAfterFilters(string baseItemType, List<Expression> filters, ScriptFile file)
    {
        // For now, return the base item type.
        // Map functions that change the type (like toError → Violation) would need function return type tracking.
        return baseItemType;
    }

    /// <summary>
    /// Checks if a parameter type is a collection reference like "[Statement]".
    /// Some predicates declare their parameter as a collection type.
    /// </summary>
    private bool IsCollectionReference(string paramType)
    {
        // Predicate paramType could be a bare type name already resolved by the parser
        // The parser strips brackets, so "Statement" from "[Statement]" is just "Statement"
        return _registry.HasCollection(paramType) || _registry.HasType(paramType);
    }
}
