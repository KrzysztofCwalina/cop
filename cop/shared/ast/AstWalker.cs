// Convenience base for visitors that walk the tree without producing a value.

namespace Cop.Lang.Ast;

/// <summary>
/// Convenience base for visitors that walk the tree without producing a value.
/// </summary>
public abstract class AstWalker : AstVisitor<object?>
{
    protected override object? DefaultVisit(AstNode node) => null;

    public override object? VisitModule(ModuleNode node)
    {
        foreach (Declaration decl in node.Declarations)
        {
            Visit(decl);
        }

        return null;
    }

    public override object? VisitTypeDecl(TypeDecl node)
    {
        foreach (PropertyDecl prop in node.Properties)
        {
            Visit(prop);
        }

        return null;
    }

    public override object? VisitFunctionDecl(FunctionDecl node)
    {
        if (node.Guard is not null)
        {
            Visit(node.Guard);
        }

        VisitFunctionBody(node.Body);
        return null;
    }

    public override object? VisitLetDecl(LetDecl node)
    {
        Visit(node.Value);
        return null;
    }

    public override object? VisitCommandDecl(CommandDecl node)
    {
        foreach (Statement stmt in node.Body)
        {
            Visit(stmt);
        }

        return null;
    }

    public override object? VisitLetStatement(LetStatement node)
    {
        Visit(node.Value);
        return null;
    }

    public override object? VisitForEachStatement(ForEachStatement node)
    {
        Visit(node.Collection);
        foreach (Statement stmt in node.Body)
        {
            Visit(stmt);
        }

        return null;
    }

    public override object? VisitExpressionStatement(ExpressionStatement node)
    {
        Visit(node.Expr);
        return null;
    }

    public override object? VisitPipelineStatement(PipelineStatement node)
    {
        Visit(node.Source);
        foreach (PipelineStage stage in node.Stages)
        {
            Visit(stage.Expr);
        }

        return null;
    }

    public override object? VisitBinaryExpr(BinaryExpr node)
    {
        Visit(node.Left);
        Visit(node.Right);
        return null;
    }

    public override object? VisitUnaryExpr(UnaryExpr node)
    {
        Visit(node.Operand);
        return null;
    }

    public override object? VisitCallExpr(CallExpr node)
    {
        Visit(node.Callee);
        foreach (Expression arg in node.Args)
        {
            Visit(arg);
        }

        return null;
    }

    public override object? VisitMemberExpr(MemberExpr node)
    {
        Visit(node.Object);
        return null;
    }

    public override object? VisitIndexExpr(IndexExpr node)
    {
        Visit(node.Object);
        Visit(node.Index);
        return null;
    }

    public override object? VisitLambdaExpr(LambdaExpr node)
    {
        Visit(node.Body);
        return null;
    }

    public override object? VisitConditionalExpr(ConditionalExpr node)
    {
        Visit(node.Condition);
        Visit(node.Then);
        Visit(node.Else);
        return null;
    }

    public override object? VisitMatchExpr(MatchExpr node)
    {
        Visit(node.Discriminant);
        foreach (MatchArm arm in node.Arms)
        {
            Visit(arm.Body);
        }

        return null;
    }

    public override object? VisitListExpr(ListExpr node)
    {
        foreach (Expression elem in node.Elements)
        {
            Visit(elem);
        }

        return null;
    }

    public override object? VisitObjectExpr(ObjectExpr node)
    {
        foreach (FieldInit field in node.Fields)
        {
            Visit(field.Value);
        }

        return null;
    }

    public override object? VisitInterpolatedStringExpr(InterpolatedStringExpr node)
    {
        foreach (StringPart part in node.Parts)
        {
            if (part is ExpressionPart ep)
            {
                Visit(ep.Expr);
            }
        }
        return null;
    }

    public override object? VisitFilterExpr(FilterExpr node)
    {
        Visit(node.Collection);
        Visit(node.Predicate);
        return null;
    }

    protected void VisitFunctionBody(FunctionBody body)
    {
        switch (body)
        {
            case ExpressionBody eb:
                Visit(eb.Expr);
                break;
            case MappingBody mb:
                foreach (FieldMapping mapping in mb.Mappings)
                {
                    Visit(mapping.Value);
                }

                break;
        }
    }
}
