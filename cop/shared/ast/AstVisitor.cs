// Base class for AST visitors implementing the Visitor pattern.

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
