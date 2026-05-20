namespace Cop.Lang.Ast;

/// <summary>
/// Base class for AST visitors implementing the Visitor pattern.
/// Each Visit method returns a value of type T, enabling both
/// void traversals (T = object?) and transformations (T = AstNode).
/// </summary>
public abstract class AstVisitor<T>
{
    // Module
    public virtual T VisitModule(ModuleNode node) => DefaultVisit(node);

    // Declarations
    public virtual T VisitImportDecl(ImportDecl node) => DefaultVisit(node);
    public virtual T VisitTypeDecl(TypeDecl node) => DefaultVisit(node);
    public virtual T VisitEnumDecl(EnumDecl node) => DefaultVisit(node);
    public virtual T VisitFlagsDecl(FlagsDecl node) => DefaultVisit(node);
    public virtual T VisitFunctionDecl(FunctionDecl node) => DefaultVisit(node);
    public virtual T VisitLetDecl(LetDecl node) => DefaultVisit(node);
    public virtual T VisitCommandDecl(CommandDecl node) => DefaultVisit(node);

    // Statements
    public virtual T VisitLetStatement(LetStatement node) => DefaultVisit(node);
    public virtual T VisitForEachStatement(ForEachStatement node) => DefaultVisit(node);
    public virtual T VisitExpressionStatement(ExpressionStatement node) => DefaultVisit(node);
    public virtual T VisitPipelineStatement(PipelineStatement node) => DefaultVisit(node);

    // Expressions
    public virtual T VisitIdentifierExpr(IdentifierExpr node) => DefaultVisit(node);
    public virtual T VisitLiteralExpr(LiteralExpr node) => DefaultVisit(node);
    public virtual T VisitBinaryExpr(BinaryExpr node) => DefaultVisit(node);
    public virtual T VisitUnaryExpr(UnaryExpr node) => DefaultVisit(node);
    public virtual T VisitCallExpr(CallExpr node) => DefaultVisit(node);
    public virtual T VisitMemberExpr(MemberExpr node) => DefaultVisit(node);
    public virtual T VisitIndexExpr(IndexExpr node) => DefaultVisit(node);
    public virtual T VisitLambdaExpr(LambdaExpr node) => DefaultVisit(node);
    public virtual T VisitConditionalExpr(ConditionalExpr node) => DefaultVisit(node);
    public virtual T VisitMatchExpr(MatchExpr node) => DefaultVisit(node);
    public virtual T VisitListExpr(ListExpr node) => DefaultVisit(node);
    public virtual T VisitObjectExpr(ObjectExpr node) => DefaultVisit(node);
    public virtual T VisitInterpolatedStringExpr(InterpolatedStringExpr node) => DefaultVisit(node);
    public virtual T VisitFilterExpr(FilterExpr node) => DefaultVisit(node);

    /// <summary>
    /// Dispatch to the appropriate Visit method based on node type.
    /// </summary>
    public T Visit(AstNode node) => node switch
    {
        ModuleNode n => VisitModule(n),
        ImportDecl n => VisitImportDecl(n),
        TypeDecl n => VisitTypeDecl(n),
        EnumDecl n => VisitEnumDecl(n),
        FlagsDecl n => VisitFlagsDecl(n),
        FunctionDecl n => VisitFunctionDecl(n),
        LetDecl n => VisitLetDecl(n),
        CommandDecl n => VisitCommandDecl(n),
        LetStatement n => VisitLetStatement(n),
        ForEachStatement n => VisitForEachStatement(n),
        ExpressionStatement n => VisitExpressionStatement(n),
        PipelineStatement n => VisitPipelineStatement(n),
        IdentifierExpr n => VisitIdentifierExpr(n),
        LiteralExpr n => VisitLiteralExpr(n),
        BinaryExpr n => VisitBinaryExpr(n),
        UnaryExpr n => VisitUnaryExpr(n),
        CallExpr n => VisitCallExpr(n),
        MemberExpr n => VisitMemberExpr(n),
        IndexExpr n => VisitIndexExpr(n),
        LambdaExpr n => VisitLambdaExpr(n),
        ConditionalExpr n => VisitConditionalExpr(n),
        MatchExpr n => VisitMatchExpr(n),
        ListExpr n => VisitListExpr(n),
        ObjectExpr n => VisitObjectExpr(n),
        InterpolatedStringExpr n => VisitInterpolatedStringExpr(n),
        FilterExpr n => VisitFilterExpr(n),
        _ => DefaultVisit(node)
    };

    /// <summary>
    /// Default handler for unoverridden visit methods.
    /// Override to throw or return a sentinel value.
    /// </summary>
    protected abstract T DefaultVisit(AstNode node);
}

/// <summary>
/// Convenience base for visitors that walk the tree without producing a value.
/// </summary>
public abstract class AstWalker : AstVisitor<object?>
{
    protected override object? DefaultVisit(AstNode node) => null;

    public override object? VisitModule(ModuleNode node)
    {
        foreach (var decl in node.Declarations)
            Visit(decl);
        return null;
    }

    public override object? VisitTypeDecl(TypeDecl node)
    {
        foreach (var prop in node.Properties)
            Visit(prop);
        return null;
    }

    public override object? VisitFunctionDecl(FunctionDecl node)
    {
        if (node.Guard is not null) Visit(node.Guard);
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
        foreach (var stmt in node.Body)
            Visit(stmt);
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
        foreach (var stmt in node.Body)
            Visit(stmt);
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
        foreach (var stage in node.Stages)
            Visit(stage.Expr);
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
        foreach (var arg in node.Args)
            Visit(arg);
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
        foreach (var arm in node.Arms)
            Visit(arm.Body);
        return null;
    }

    public override object? VisitListExpr(ListExpr node)
    {
        foreach (var elem in node.Elements)
            Visit(elem);
        return null;
    }

    public override object? VisitObjectExpr(ObjectExpr node)
    {
        foreach (var field in node.Fields)
            Visit(field.Value);
        return null;
    }

    public override object? VisitInterpolatedStringExpr(InterpolatedStringExpr node)
    {
        foreach (var part in node.Parts)
        {
            if (part is ExpressionPart ep)
                Visit(ep.Expr);
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
                foreach (var mapping in mb.Mappings)
                    Visit(mapping.Value);
                break;
        }
    }
}
