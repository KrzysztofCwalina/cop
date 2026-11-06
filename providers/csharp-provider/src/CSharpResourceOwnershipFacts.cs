// Recognizes return/member escape sites, not the receiver's eventual disposal behavior.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cop.Providers.SourceParsers;

internal static class CSharpResourceOwnershipFacts
{
    internal static bool IsTransferred(BaseObjectCreationExpressionSyntax creation, SemanticModel model)
    {
        ExpressionSyntax value = Unwrap(creation);
        if (value.Parent is ReturnStatementSyntax
            || value.Parent is ArrowExpressionClauseSyntax
                && model.GetEnclosingSymbol(creation.SpanStart) is IMethodSymbol { ReturnsVoid: false }
            || value.Parent is LambdaExpressionSyntax lambda
                && model.GetSymbolInfo(lambda).Symbol is IMethodSymbol { ReturnsVoid: false })
        {
            return true;
        }

        if (value.Parent is AssignmentExpressionSyntax directAssignment
                && ReferenceEquals(directAssignment.Right, value)
                && directAssignment.Kind() is SyntaxKind.SimpleAssignmentExpression
                    or SyntaxKind.CoalesceAssignmentExpression
                && IsMemberTarget(directAssignment.Left, model)
            || value.Parent is EqualsValueClauseSyntax initializer
                && model.GetDeclaredSymbol(initializer.Parent!) is IFieldSymbol or IPropertySymbol)
        {
            return true;
        }

        if (value.Parent is not EqualsValueClauseSyntax
            { Parent: VariableDeclaratorSyntax variable }
            || model.GetDeclaredSymbol(variable) is not ILocalSymbol local)
        {
            return false;
        }

        SyntaxNode? scope = creation.Ancestors().FirstOrDefault(IsCallable);
        if (scope is null)
        {
            return false;
        }

        HashSet<SyntaxNode> transfers = [];
        foreach (SyntaxNode transfer in scope.DescendantNodes(
                     node => ReferenceEquals(node, scope) || !IsCallable(node))
                     .Where(node => node is ReturnStatementSyntax or AssignmentExpressionSyntax))
        {
            if (transfer.SpanStart < creation.SpanStart
                || HasInterveningWrite(scope, creation, transfer, local, model))
            {
                continue;
            }

            if (transfer is AssignmentExpressionSyntax assignment
                && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                && IsMemberTarget(assignment.Left, model)
                && IsLocal(Strip(assignment.Right), local, model))
            {
                transfers.Add(transfer);
                continue;
            }

            if (transfer is not ReturnStatementSyntax returned)
            {
                continue;
            }

            ExpressionSyntax? expression = returned.Expression is { } result ? Strip(result) : null;
            if (IsLocal(expression, local, model))
            {
                transfers.Add(transfer);
                continue;
            }

            if (expression is BaseObjectCreationExpressionSyntax container
                && container.ArgumentList is { } arguments
                && arguments.Arguments.Any(argument =>
                    argument.RefKindKeyword.RawKind == 0
                    && IsLocal(Strip(argument.Expression), local, model)))
            {
                transfers.Add(transfer);
            }
        }

        if (variable.Parent?.Parent is not LocalDeclarationStatementSyntax declaration
            || declaration.Parent is not BlockSyntax block)
        {
            return false;
        }

        int index = block.Statements.IndexOf(declaration);
        return Sequence(block.Statements.Skip(index + 1), transfers) == TransferPaths.Transferred;
    }

    [Flags]
    private enum TransferPaths
    {
        Pending = 1,
        Transferred = 2,
        UntransferredExit = 4
    }

    private static TransferPaths Sequence(
        IEnumerable<StatementSyntax> statements, HashSet<SyntaxNode> transfers)
    {
        TransferPaths paths = TransferPaths.Pending;
        foreach (StatementSyntax statement in statements)
        {
            if (!paths.HasFlag(TransferPaths.Pending))
            {
                break;
            }

            paths = (paths & ~TransferPaths.Pending) | Statement(statement, transfers);
        }
        return paths;
    }

    private static TransferPaths Statement(
        StatementSyntax statement, HashSet<SyntaxNode> transfers)
    {
        TransferPaths paths = statement switch
        {
            ReturnStatementSyntax returned => transfers.Contains(returned)
                ? TransferPaths.Transferred : TransferPaths.UntransferredExit,
            ExpressionStatementSyntax expression when transfers.Contains(Strip(expression.Expression))
                => TransferPaths.Transferred,
            BlockSyntax block => Sequence(block.Statements, transfers),
            IfStatementSyntax conditional => Conditional(conditional, transfers),
            UsingStatementSyntax resource => Statement(resource.Statement, transfers),
            LockStatementSyntax locked => Statement(locked.Statement, transfers),
            LocalFunctionStatementSyntax => TransferPaths.Pending,
            ThrowStatementSyntax or GotoStatementSyntax
                or ContinueStatementSyntax or BreakStatementSyntax or YieldStatementSyntax
                or TryStatementSyntax or SwitchStatementSyntax or LabeledStatementSyntax
                or WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax
                or CommonForEachStatementSyntax => TransferPaths.UntransferredExit,
            _ => TransferPaths.Pending
        };
        if (statement is not LocalFunctionStatementSyntax
            && statement.DescendantNodes(node =>
                    node is not AnonymousFunctionExpressionSyntax
                        and not LocalFunctionStatementSyntax)
                .OfType<ThrowExpressionSyntax>().Any())
        {
            paths |= TransferPaths.UntransferredExit;
        }
        return paths;
    }

    private static TransferPaths Conditional(
        IfStatementSyntax conditional, HashSet<SyntaxNode> transfers)
    {
        ExpressionSyntax condition = Strip(conditional.Condition);
        if (condition.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            return Statement(conditional.Statement, transfers);
        }
        if (condition.IsKind(SyntaxKind.FalseLiteralExpression))
        {
            return conditional.Else is null ? TransferPaths.Pending
                : Statement(conditional.Else.Statement, transfers);
        }

        return Statement(conditional.Statement, transfers)
            | (conditional.Else is null ? TransferPaths.Pending
                : Statement(conditional.Else.Statement, transfers));
    }

    private static bool IsCallable(SyntaxNode node) =>
        node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
            or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax;

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression.Parent is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized;
        }

        return expression;
    }

    private static ExpressionSyntax Strip(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool IsLocal(
        ExpressionSyntax? expression, ILocalSymbol local, SemanticModel model) =>
        expression is IdentifierNameSyntax
        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(expression).Symbol, local);

    private static bool IsMemberTarget(ExpressionSyntax expression, SemanticModel model) =>
        model.GetSymbolInfo(expression).Symbol is IFieldSymbol
            or IPropertySymbol { IsIndexer: false };

    private static bool HasInterveningWrite(SyntaxNode scope, SyntaxNode creation,
        SyntaxNode transfer, ILocalSymbol local, SemanticModel model)
    {
        foreach (SyntaxNode node in scope.DescendantNodes().Where(node =>
                     node.SpanStart > creation.SpanStart && node.SpanStart < transfer.SpanStart))
        {
            if (node is AssignmentExpressionSyntax assignment
                    && WritesLocal(assignment.Left, local, model)
                || node is ArgumentSyntax argument
                    && argument.RefKindKeyword.RawKind != 0
                    && IsLocal(Strip(argument.Expression), local, model)
                || node is PrefixUnaryExpressionSyntax prefix
                    && prefix.Kind() is SyntaxKind.PreIncrementExpression
                        or SyntaxKind.PreDecrementExpression
                    && IsLocal(Strip(prefix.Operand), local, model)
                || node is PostfixUnaryExpressionSyntax postfix
                    && postfix.Kind() is SyntaxKind.PostIncrementExpression
                        or SyntaxKind.PostDecrementExpression
                    && IsLocal(Strip(postfix.Operand), local, model))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WritesLocal(
        ExpressionSyntax target, ILocalSymbol local, SemanticModel model)
    {
        target = Strip(target);
        return IsLocal(target, local, model)
            || target is TupleExpressionSyntax tuple
                && tuple.Arguments.Any(argument => WritesLocal(argument.Expression, local, model));
    }
}
