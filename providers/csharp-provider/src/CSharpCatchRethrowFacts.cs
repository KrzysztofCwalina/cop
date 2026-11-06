// Proves explicit structured rethrow paths, not exception effects of calls or cleanup.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cop.Providers.SourceParsers;

internal static class CSharpCatchRethrowFacts
{
    [Flags]
    private enum Exits
    {
        None = 0,
        Next = 1,
        Original = 2,
        Other = 4,
        Break = 8
    }

    internal static bool AlwaysRethrows(CatchClauseSyntax clause) =>
        Sequence(clause.Block.Statements) == Exits.Original;

    private static Exits Sequence(IEnumerable<StatementSyntax> statements)
    {
        Exits exits = Exits.Next;
        foreach (StatementSyntax statement in statements)
        {
            if (!exits.HasFlag(Exits.Next))
            {
                break;
            }

            exits = (exits & ~Exits.Next) | Statement(statement);
        }

        return exits;
    }

    private static Exits Statement(StatementSyntax statement)
    {
        Exits exits = statement switch
        {
            BlockSyntax block => Sequence(block.Statements),
            ThrowStatementSyntax { Expression: null } => Exits.Original,
            ThrowStatementSyntax or ReturnStatementSyntax or GotoStatementSyntax
                or ContinueStatementSyntax or YieldStatementSyntax => Exits.Other,
            BreakStatementSyntax => Exits.Break,
            IfStatementSyntax conditional => Conditional(conditional),
            SwitchStatementSyntax selection => Switch(selection),
            TryStatementSyntax guarded => Try(guarded),
            UsingStatementSyntax resource => Statement(resource.Statement),
            LockStatementSyntax locked => Statement(locked.Statement),
            CheckedStatementSyntax checkedBlock => Sequence(checkedBlock.Block.Statements),
            UnsafeStatementSyntax unsafeBlock => Sequence(unsafeBlock.Block.Statements),
            FixedStatementSyntax pinned => Statement(pinned.Statement),
            LabeledStatementSyntax labeled => Statement(labeled.Statement),
            LocalFunctionStatementSyntax => Exits.Next,
            WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax
                or CommonForEachStatementSyntax => Exits.Next | Exits.Other,
            _ => Exits.Next
        };
        // A throw expression is a replacement exception, including in conditions or
        // initializers. Function bodies execute separately and do not prove this catch.
        if (statement is not LocalFunctionStatementSyntax
            && statement.DescendantNodes(node =>
                    node is not AnonymousFunctionExpressionSyntax
                        and not LocalFunctionStatementSyntax)
                .OfType<ThrowExpressionSyntax>().Any())
        {
            exits |= Exits.Other;
        }

        return exits;
    }

    private static Exits Conditional(IfStatementSyntax conditional)
    {
        ExpressionSyntax condition = conditional.Condition;
        while (condition is ParenthesizedExpressionSyntax parenthesized)
        {
            condition = parenthesized.Expression;
        }

        if (condition.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            return Statement(conditional.Statement);
        }

        if (condition.IsKind(SyntaxKind.FalseLiteralExpression))
        {
            return conditional.Else is null ? Exits.Next : Statement(conditional.Else.Statement);
        }

        return Statement(conditional.Statement)
            | (conditional.Else is null ? Exits.Next : Statement(conditional.Else.Statement));
    }

    private static Exits Switch(SwitchStatementSyntax selection)
    {
        Exits exits = selection.Sections.Any(section =>
            section.Labels.Any(label => label is DefaultSwitchLabelSyntax))
            ? Exits.None : Exits.Next;
        foreach (SwitchSectionSyntax section in selection.Sections)
        {
            Exits branch = Sequence(section.Statements);
            exits |= (branch & ~Exits.Break)
                | (branch.HasFlag(Exits.Break) ? Exits.Next : Exits.None);
        }

        return exits;
    }

    private static Exits Try(TryStatementSyntax guarded)
    {
        // Nested handlers can change the active exception. Do not infer identity or
        // exceptional reachability across them without a full exception-flow model.
        if (guarded.Catches.Count != 0)
        {
            return Exits.Next | Exits.Other;
        }

        Exits body = Sequence(guarded.Block.Statements);
        if (guarded.Finally is null)
        {
            return body;
        }

        Exits cleanup = Sequence(guarded.Finally.Block.Statements);
        if ((body & Exits.Other) != 0 || (cleanup & ~Exits.Next) != 0)
        {
            return Exits.Other;
        }

        return body;
    }
}
