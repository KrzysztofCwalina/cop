// Tracks explicit cancellation paths and bound token provenance, not exception effects.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cop.Providers.SourceParsers;

internal sealed class CSharpCancellationFacts
{
    private readonly CatchClauseSyntax _clause;
    private readonly SemanticModel _model;
    private readonly SyntaxNode _scope;
    private readonly Dictionary<ISymbol, bool> _stable = new(SymbolEqualityComparer.Default);

    private CSharpCancellationFacts(CatchClauseSyntax clause, SemanticModel model)
    {
        _clause = clause;
        _model = model;
        _scope = clause.Ancestors().FirstOrDefault(node =>
            node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
                or AnonymousFunctionExpressionSyntax) ?? clause.SyntaxTree.GetRoot();
    }

    internal static (bool IsHandler, bool PreservesCaller) Analyze(
        CatchClauseSyntax clause, SemanticModel model)
    {
        CSharpCancellationFacts facts = new(clause, model);
        bool isHandler = clause.Declaration is not null
            && IsCancellation(model.GetTypeInfo(clause.Declaration.Type).Type);
        return (isHandler, isHandler && facts.PreservesCaller());
    }

    private bool PreservesCaller()
    {
        if (CSharpCatchRethrowFacts.AlwaysRethrows(_clause)
            || _clause.Filter is not null
                && Condition(_clause.Filter.FilterExpression, null) == false)
        {
            return true;
        }

        if (_clause.Parent is not TryStatementSyntax guarded)
        {
            return false;
        }

        HashSet<IParameterSymbol> callers = new(SymbolEqualityComparer.Default);
        foreach (ArgumentSyntax argument in Nodes(guarded.Block).OfType<ArgumentSyntax>())
        {
            if (_model.GetOperation(argument) is IArgumentOperation operation
                && IsToken(operation.Parameter?.Type))
            {
                callers.UnionWith(Token(operation.Value).Callers);
            }
        }

        foreach (InvocationExpressionSyntax invocation in
                 Nodes(guarded.Block).OfType<InvocationExpressionSyntax>())
        {
            if (_model.GetOperation(invocation) is IInvocationOperation operation
                && IsToken(operation.TargetMethod.ContainingType)
                && operation.TargetMethod.Name == "ThrowIfCancellationRequested")
            {
                callers.UnionWith(Token(operation.Instance).Callers);
            }
        }

        if (callers.Count == 0)
        {
            Paths original = Sequence(_clause.Block.Statements, null,
                [new(SymbolEqualityComparer.Default)], _clause);
            return !original.Unsafe && original.Next.Count == 0
                || HasOnlyInternalAwaits(guarded.Block);
        }

        return callers.All(caller => Preserves(caller, guarded));
    }

    private bool Preserves(IParameterSymbol caller, TryStatementSyntax guarded)
    {
        if (_clause.Filter is not null
            && Condition(_clause.Filter.FilterExpression, caller) == false)
        {
            return true;
        }

        Paths paths = Sequence(_clause.Block.Statements, caller,
            [new(SymbolEqualityComparer.Default)], _clause);
        if (!paths.Unsafe && paths.Next.Count != 0 && guarded.Finally is null
            && guarded.Parent is BlockSyntax enclosing)
        {
            int index = enclosing.Statements.IndexOf(guarded);
            // Only a direct continuation is proven: intervening returns/branches are not
            // assumed to await a canceled completion source.
            paths = Sequence(enclosing.Statements.Skip(index + 1).Take(1), caller,
                paths.Next, _clause);
        }

        return !paths.Unsafe && paths.Next.Count == 0;
    }

    private Paths Sequence(IEnumerable<StatementSyntax> statements, IParameterSymbol? caller,
        List<HashSet<ILocalSymbol>> next, CatchClauseSyntax activeCatch)
    {
        Paths result = new(next, false);
        foreach (StatementSyntax statement in statements)
        {
            if (result.Next.Count == 0)
            {
                break;
            }

            Paths step = new([], false);
            foreach (HashSet<ILocalSymbol> state in result.Next)
            {
                Paths branch = Statement(statement, caller, state, activeCatch);
                step.Next.AddRange(branch.Next);
                step.Unsafe |= branch.Unsafe;
            }

            step.Unsafe |= result.Unsafe;
            result = step;
        }

        return result;
    }

    private Paths Statement(StatementSyntax statement, IParameterSymbol? caller,
        HashSet<ILocalSymbol> state, CatchClauseSyntax activeCatch)
    {
        if (statement is not LocalFunctionStatementSyntax
            && Nodes(statement).OfType<ThrowExpressionSyntax>().Any())
        {
            return new([state], true);
        }

        switch (statement)
        {
            case BlockSyntax block:
                return Sequence(block.Statements, caller, [state], activeCatch);
            case ThrowStatementSyntax thrown:
                return new([], !IsCancellationThrow(thrown, caller, activeCatch));
            case ReturnStatementSyntax returned:
                return new([], !ConsumesCancellation(returned.Expression, state));
            case ExpressionStatementSyntax expression:
                if (_model.GetOperation(expression.Expression) is IInvocationOperation invocation)
                {
                    if (invocation.TargetMethod.Name == "ThrowIfCancellationRequested"
                        && IsToken(invocation.TargetMethod.ContainingType)
                        && IsCaller(invocation.Instance, caller))
                    {
                        return new([], false);
                    }

                    if (CanceledCompletion(invocation, caller) is ILocalSymbol source)
                    {
                        state.Add(source);
                    }
                }

                return ConsumesCancellation(expression.Expression, state)
                    ? new([], false) : new([state], false);
            case IfStatementSyntax conditional:
                bool? condition = Condition(conditional.Condition, caller);
                Paths positive = condition == false ? new([], false)
                    : Statement(conditional.Statement, caller, Copy(state), activeCatch);
                Paths negative = condition == true ? new([], false)
                    : conditional.Else is null ? new([Copy(state)], false)
                    : Statement(conditional.Else.Statement, caller, Copy(state), activeCatch);
                positive.Next.AddRange(negative.Next);
                positive.Unsafe |= negative.Unsafe;
                return positive;
            case LockStatementSyntax locked:
                return Statement(locked.Statement, caller, state, activeCatch);
            case UsingStatementSyntax resource:
                return Statement(resource.Statement, caller, state, activeCatch);
            case CheckedStatementSyntax checkedBlock:
                return Sequence(checkedBlock.Block.Statements, caller, [state], activeCatch);
            case TryStatementSyntax { Finally: null } guarded:
                Paths guardedPaths = Sequence(guarded.Block.Statements, caller,
                    [Copy(state)], activeCatch);
                foreach (CatchClauseSyntax handler in guarded.Catches)
                {
                    Paths handlerPaths = Sequence(handler.Block.Statements, caller,
                        [Copy(state)], handler);
                    guardedPaths.Next.AddRange(handlerPaths.Next);
                    guardedPaths.Unsafe |= handlerPaths.Unsafe;
                }

                return guardedPaths;
            case LocalFunctionStatementSyntax:
            case EmptyStatementSyntax:
                return new([state], false);
            case LocalDeclarationStatementSyntax declaration
                when !Nodes(declaration).OfType<ThrowExpressionSyntax>().Any()
                    && !Nodes(declaration).OfType<AwaitExpressionSyntax>().Any():
                return new([state], false);
            default:
                return new([state], true);
        }
    }

    private bool IsCancellationThrow(ThrowStatementSyntax thrown, IParameterSymbol? caller,
        CatchClauseSyntax activeCatch)
    {
        if (thrown.Expression is null)
        {
            return activeCatch.Declaration is not null
                && IsCancellation(_model.GetTypeInfo(activeCatch.Declaration.Type).Type);
        }

        IOperation? operation = Unwrap(_model.GetOperation(thrown.Expression));
        if (operation is ILocalReferenceOperation caught && IsStable(caught.Local)
            && (IsCaughtCancellation(caught.Local, _clause)
                || IsCaughtCancellation(caught.Local, activeCatch)))
        {
            return true;
        }

        return operation is IObjectCreationOperation creation
            && (IsFrameworkType(creation.Type, "System", "OperationCanceledException")
                || IsFrameworkType(creation.Type, "System.Threading.Tasks",
                    "TaskCanceledException"))
            && creation.Arguments.Any(argument =>
                IsToken(argument.Parameter?.Type) && caller is not null
                // Linked sources preserve each known caller lineage even when another
                // input (such as an instance lifetime token) has unknown provenance.
                && Token(argument.Value).Callers.Contains(caller));
    }

    private bool IsCaughtCancellation(ILocalSymbol local, CatchClauseSyntax clause) =>
        clause.Declaration is not null
        && IsCancellation(_model.GetTypeInfo(clause.Declaration.Type).Type)
        && SymbolEqualityComparer.Default.Equals(local,
            _model.GetDeclaredSymbol(clause.Declaration));

    private bool ConsumesCancellation(ExpressionSyntax? expression,
        HashSet<ILocalSymbol> canceled)
    {
        return expression is not null
            && Unwrap(_model.GetOperation(expression)) is IAwaitOperation awaited
            && CompletionTask(awaited.Operation) is ILocalSymbol source
            && canceled.Contains(source);
    }

    private ILocalSymbol? CompletionTask(IOperation? operation, int depth = 0)
    {
        if (depth > 12)
        {
            return null;
        }

        operation = Awaitable(operation);
        if (operation is IPropertyReferenceOperation property
            && property.Property.Name == "Task"
            && IsCompletionSource(property.Property.ContainingType)
            && Unwrap(property.Instance) is ILocalReferenceOperation local)
        {
            return local.Local;
        }

        if (operation is ILocalReferenceOperation alias && IsStable(alias.Local))
        {
            return CompletionTask(Initializer(alias.Local), depth + 1);
        }

        return null;
    }

    private ILocalSymbol? CanceledCompletion(IInvocationOperation operation,
        IParameterSymbol? caller)
    {
        if (!IsCompletionSource(operation.TargetMethod.ContainingType)
            || operation.TargetMethod.Name is not ("SetCanceled" or "TrySetCanceled")
            || Unwrap(operation.Instance) is not ILocalReferenceOperation local
            || !IsFreshCompletion(local.Local, caller)
            || !operation.Arguments.Any(argument => IsToken(argument.Parameter?.Type)
                && (IsCaller(argument.Value, caller) || IsCaughtToken(argument.Value))))
        {
            return null;
        }

        return local.Local;
    }

    private bool IsCaughtToken(IOperation? operation) =>
        Unwrap(operation) is IPropertyReferenceOperation property
        && property.Property.Name == "CancellationToken"
        && IsFrameworkType(property.Property.ContainingType,
            "System", "OperationCanceledException")
        && Unwrap(property.Instance) is ILocalReferenceOperation local
        && _clause.Declaration is not null
        && SymbolEqualityComparer.Default.Equals(local.Local,
            _model.GetDeclaredSymbol(_clause.Declaration));

    private bool IsFreshCompletion(ILocalSymbol source, IParameterSymbol? caller)
    {
        if (!IsStable(source) || Unwrap(Initializer(source)) is not IObjectCreationOperation created
            || !IsCompletionSource(created.Type))
        {
            return false;
        }

        foreach (IdentifierNameSyntax name in _scope.DescendantNodes()
                     .OfType<IdentifierNameSyntax>())
        {
            if (!SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(name).Symbol, source))
            {
                continue;
            }

            if (name.Parent is not MemberAccessExpressionSyntax member
                || !ReferenceEquals(member.Expression, name))
            {
                return false;
            }

            ISymbol? symbol = _model.GetSymbolInfo(member).Symbol;
            if (symbol is IPropertySymbol { Name: "Task" } property
                && IsCompletionSource(property.ContainingType))
            {
                continue;
            }

            if (symbol is not IMethodSymbol method
                || !IsCompletionSource(method.ContainingType)
                || method.Name is not ("SetCanceled" or "TrySetCanceled")
                || member.Parent is not InvocationExpressionSyntax invocation
                || !_clause.Block.Span.Contains(invocation.Span)
                || _model.GetOperation(invocation) is not IInvocationOperation cancellation
                || !cancellation.Arguments.Any(argument => IsToken(argument.Parameter?.Type)
                    && (IsCaller(argument.Value, caller) || IsCaughtToken(argument.Value))))
            {
                return false;
            }
        }

        return true;
    }

    private bool HasOnlyInternalAwaits(BlockSyntax block)
    {
        if (Nodes(block).Any(node => node is ThrowStatementSyntax or ThrowExpressionSyntax))
        {
            return false;
        }

        AwaitExpressionSyntax[] awaits = Nodes(block).OfType<AwaitExpressionSyntax>().ToArray();
        return awaits.Length != 0 && awaits.All(awaited =>
        {
            if (Awaitable(_model.GetOperation(awaited.Expression))
                is not IInvocationOperation invocation)
            {
                return false;
            }

            IArgumentOperation[] tokens = invocation.Arguments
                .Where(argument => IsToken(argument.Parameter?.Type)).ToArray();
            return tokens.Length != 0 && tokens.All(argument => Token(argument.Value).Internal);
        });
    }

    private bool? Condition(ExpressionSyntax expression, IParameterSymbol? caller) =>
        Boolean(_model.GetOperation(expression), caller);

    private bool? Boolean(IOperation? operation, IParameterSymbol? caller)
    {
        operation = Unwrap(operation);
        if (operation?.ConstantValue is { HasValue: true, Value: bool constant })
        {
            return constant;
        }

        if (operation is IPropertyReferenceOperation property
            && property.Property.Name == "IsCancellationRequested"
            && IsToken(property.Property.ContainingType) && IsCaller(property.Instance, caller))
        {
            return true;
        }

        if (operation is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } unary
            && unary.OperatorMethod is null)
        {
            return Boolean(unary.Operand, caller) is bool value ? !value : null;
        }

        if (operation is IBinaryOperation binary && binary.OperatorMethod is null)
        {
            bool? left = Boolean(binary.LeftOperand, caller);
            bool? right = Boolean(binary.RightOperand, caller);
            return binary.OperatorKind switch
            {
                BinaryOperatorKind.ConditionalAnd => left == false || right == false
                    ? false : left == true && right == true ? true : null,
                BinaryOperatorKind.ConditionalOr => left == true || right == true
                    ? true : left == false && right == false ? false : null,
                BinaryOperatorKind.Equals when left.HasValue && right.HasValue => left == right,
                BinaryOperatorKind.NotEquals when left.HasValue && right.HasValue => left != right,
                _ => null
            };
        }

        return null;
    }

    private bool IsCaller(IOperation? operation, IParameterSymbol? caller)
    {
        if (caller is null)
        {
            return false;
        }

        TokenOrigins origins = Token(operation);
        return !origins.Unknown && !origins.Internal && origins.Callers.Count == 1
            && origins.Callers.Contains(caller);
    }

    private TokenOrigins Token(IOperation? operation, int depth = 0)
    {
        if (depth > 12)
        {
            return new([], false, true);
        }

        operation = Unwrap(operation);
        if (operation is IParameterReferenceOperation parameter && IsToken(parameter.Type)
            && IsStable(parameter.Parameter))
        {
            return new(new([parameter.Parameter], SymbolEqualityComparer.Default), false, false);
        }

        if (operation is ILocalReferenceOperation local && IsStable(local.Local))
        {
            return Token(Initializer(local.Local), depth + 1);
        }

        if (operation is IPropertyReferenceOperation property
            && property.Property.Name == "Token" && IsTokenSource(property.Property.ContainingType))
        {
            return Token(property.Instance, depth + 1);
        }

        if (operation is IObjectCreationOperation creation && IsTokenSource(creation.Type))
        {
            return new([], true, false);
        }

        if (operation is IInvocationOperation invocation
            && IsTokenSource(invocation.TargetMethod.ContainingType)
            && invocation.TargetMethod.Name == "CreateLinkedTokenSource")
        {
            TokenOrigins[] origins = invocation.Arguments
                .Select(argument => Token(argument.Value, depth + 1)).ToArray();
            HashSet<IParameterSymbol> callers = new(SymbolEqualityComparer.Default);
            foreach (TokenOrigins origin in origins)
            {
                callers.UnionWith(origin.Callers);
            }

            return new(callers, origins.Length != 0 && origins.All(origin => origin.Internal),
                origins.Any(origin => origin.Unknown));
        }

        return new([], false, true);
    }

    private IOperation? Initializer(ISymbol symbol) =>
        symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
            is VariableDeclaratorSyntax { Initializer: not null } variable
                ? _model.GetOperation(variable.Initializer.Value) : null;

    private bool IsStable(ISymbol symbol)
    {
        if (_stable.TryGetValue(symbol, out bool stable))
        {
            return stable;
        }

        stable = !_scope.DescendantNodes().OfType<IdentifierNameSyntax>().Any(name =>
            SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(name).Symbol, symbol)
            && name.Ancestors().TakeWhile(node => node is not StatementSyntax).Any(node =>
                node is AssignmentExpressionSyntax assignment
                    && assignment.Left.Span.Contains(name.Span)
                || node is ArgumentSyntax argument
                    && argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword
                        or SyntaxKind.OutKeyword));
        _stable[symbol] = stable;
        return stable;
    }

    private static IOperation? Awaitable(IOperation? operation)
    {
        operation = Unwrap(operation);
        while (operation is IInvocationOperation invocation
               && invocation.TargetMethod.Name is "ConfigureAwait" or "AsTask"
               && IsTaskType(invocation.TargetMethod.ContainingType))
        {
            operation = Unwrap(invocation.Instance);
        }

        return operation;
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation or IParenthesizedOperation)
        {
            if (operation is IConversionOperation { OperatorMethod: not null })
            {
                return operation;
            }

            operation = operation is IConversionOperation conversion
                ? conversion.Operand : ((IParenthesizedOperation)operation).Operand;
        }

        return operation;
    }

    private static IEnumerable<SyntaxNode> Nodes(SyntaxNode root) =>
        root.DescendantNodes(node => node == root || node is not
            (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

    private static bool IsCancellation(ITypeSymbol? type)
    {
        for (ITypeSymbol? current = type; current is INamedTypeSymbol named; current = named.BaseType)
        {
            if (IsFrameworkType(named, "System", "OperationCanceledException"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsToken(ITypeSymbol? type) =>
        IsFrameworkType(type, "System.Threading", "CancellationToken");

    private static bool IsTokenSource(ITypeSymbol? type) =>
        IsFrameworkType(type, "System.Threading", "CancellationTokenSource");

    private static bool IsCompletionSource(ITypeSymbol? type) =>
        IsFrameworkType(type, "System.Threading.Tasks", "TaskCompletionSource`1")
        || IsFrameworkType(type, "System.Threading.Tasks", "TaskCompletionSource");

    private static bool IsTaskType(ITypeSymbol? type) =>
        IsFrameworkType(type, "System.Threading.Tasks", "Task")
        || IsFrameworkType(type, "System.Threading.Tasks", "Task`1")
        || IsFrameworkType(type, "System.Threading.Tasks", "ValueTask")
        || IsFrameworkType(type, "System.Threading.Tasks", "ValueTask`1");

    private static bool IsFrameworkType(ITypeSymbol? type, string ns, string name) =>
        type is INamedTypeSymbol named && named.MetadataName == name
        && named.ContainingNamespace.ToDisplayString() == ns
        && !named.Locations.Any(location => location.IsInSource);

    private static HashSet<ILocalSymbol> Copy(HashSet<ILocalSymbol> state) =>
        new(state, SymbolEqualityComparer.Default);

    private sealed record TokenOrigins(
        HashSet<IParameterSymbol> Callers, bool Internal, bool Unknown);

    private sealed class Paths(List<HashSet<ILocalSymbol>> next, bool isUnsafe)
    {
        internal List<HashSet<ILocalSymbol>> Next { get; } = next;
        internal bool Unsafe { get; set; } = isUnsafe;
    }
}
