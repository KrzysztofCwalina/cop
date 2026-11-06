// Identifies catch filters whose accepted values must belong to specific exception types.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cop.Providers.SourceParsers;

internal static class CSharpCatchFilterFacts
{
    internal static bool IsTypeRestricted(CatchClauseSyntax clause, SemanticModel model)
    {
        if (clause.Declaration is not { Identifier.RawKind: not 0 } declaration
            || clause.Filter is null
            || model.GetDeclaredSymbol(declaration) is not ILocalSymbol caught)
        {
            return false;
        }

        return RestrictsExpression(clause.Filter.FilterExpression, caught, model);
    }

    private static bool RestrictsExpression(
        ExpressionSyntax expression, ILocalSymbol caught, SemanticModel model)
    {
        if (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            return RestrictsExpression(parenthesized.Expression, caught, model);
        }

        if (expression is IsPatternExpressionSyntax test && IsCaught(test.Expression, caught, model))
        {
            return RestrictsPattern(test.Pattern, model);
        }

        if (expression is not BinaryExpressionSyntax binary)
        {
            return false;
        }

        if (binary.IsKind(SyntaxKind.IsExpression)
            && binary.Right is TypeSyntax type
            && IsCaught(binary.Left, caught, model))
        {
            return IsSpecificException(type, model);
        }

        if (model.GetTypeInfo(binary).Type?.SpecialType != SpecialType.System_Boolean
            || model.GetSymbolInfo(binary).Symbol is IMethodSymbol
                { MethodKind: MethodKind.UserDefinedOperator })
        {
            return false;
        }

        return binary.Kind() switch
        {
            SyntaxKind.LogicalAndExpression =>
                RestrictsExpression(binary.Left, caught, model)
                || RestrictsExpression(binary.Right, caught, model),
            SyntaxKind.LogicalOrExpression =>
                RestrictsExpression(binary.Left, caught, model)
                && RestrictsExpression(binary.Right, caught, model),
            _ => false
        };
    }

    private static bool IsCaught(
        ExpressionSyntax expression, ILocalSymbol caught, SemanticModel model)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression is IdentifierNameSyntax
            && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(expression).Symbol, caught);
    }

    private static bool RestrictsPattern(PatternSyntax pattern, SemanticModel model) =>
        pattern switch
        {
            ParenthesizedPatternSyntax parenthesized =>
                RestrictsPattern(parenthesized.Pattern, model),
            TypePatternSyntax typed => IsSpecificException(typed.Type, model),
            DeclarationPatternSyntax declared => IsSpecificException(declared.Type, model),
            RecursivePatternSyntax { Type: { } type } => IsSpecificException(type, model),
            // Bare names in compound patterns retain constant syntax after type binding.
            ConstantPatternSyntax constant when model.GetOperation(constant)
                is ITypePatternOperation { MatchedType: INamedTypeSymbol type } =>
                IsSpecificException(type, model),
            BinaryPatternSyntax binary when binary.IsKind(SyntaxKind.AndPattern) =>
                RestrictsPattern(binary.Left, model) || RestrictsPattern(binary.Right, model),
            BinaryPatternSyntax binary when binary.IsKind(SyntaxKind.OrPattern) =>
                RestrictsPattern(binary.Left, model) && RestrictsPattern(binary.Right, model),
            _ => false
        };

    private static bool IsSpecificException(TypeSyntax syntax, SemanticModel model)
    {
        return model.GetTypeInfo(syntax).Type is INamedTypeSymbol type
            && IsSpecificException(type, model);
    }

    private static bool IsSpecificException(INamedTypeSymbol type, SemanticModel model)
    {
        if (type.TypeKind == TypeKind.Error)
        {
            return false;
        }

        INamedTypeSymbol? exception = model.Compilation.GetTypeByMetadataName("System.Exception");
        INamedTypeSymbol? systemException =
            model.Compilation.GetTypeByMetadataName("System.SystemException");
        if (exception is null
            || SymbolEqualityComparer.Default.Equals(type, exception)
            || SymbolEqualityComparer.Default.Equals(type, systemException))
        {
            return false;
        }

        for (INamedTypeSymbol? current = type.BaseType; current is not null;
             current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, exception))
            {
                return true;
            }
        }

        return false;
    }
}
