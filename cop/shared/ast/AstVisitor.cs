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
    /// <summary>
    /// Visits <see cref="ModuleNode"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitModule(ModuleNode node) => DefaultVisit(node);

    // Declarations
    /// <summary>
    /// Visits <see cref="ImportDecl"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitImportDecl(ImportDecl node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="TypeDecl"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitTypeDecl(TypeDecl node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="EnumDecl"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitEnumDecl(EnumDecl node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="FlagsDecl"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitFlagsDecl(FlagsDecl node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="FunctionDecl"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitFunctionDecl(FunctionDecl node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="LetDecl"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitLetDecl(LetDecl node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="CommandDecl"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitCommandDecl(CommandDecl node) => DefaultVisit(node);

    // Statements
    /// <summary>
    /// Visits <see cref="LetStatement"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitLetStatement(LetStatement node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="ForEachStatement"/> through the default handler unless
    /// overridden.
    /// </summary>
    public virtual T VisitForEachStatement(ForEachStatement node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="ExpressionStatement"/> through the default handler unless
    /// overridden.
    /// </summary>
    public virtual T VisitExpressionStatement(ExpressionStatement node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="PipelineStatement"/> through the default handler unless
    /// overridden.
    /// </summary>
    public virtual T VisitPipelineStatement(PipelineStatement node) => DefaultVisit(node);

    // Expressions
    /// <summary>
    /// Visits <see cref="IdentifierExpr"/> through the default handler unless
    /// overridden.
    /// </summary>
    public virtual T VisitIdentifierExpr(IdentifierExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="LiteralExpr"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitLiteralExpr(LiteralExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="BinaryExpr"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitBinaryExpr(BinaryExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="UnaryExpr"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitUnaryExpr(UnaryExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="CallExpr"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitCallExpr(CallExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="MemberExpr"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitMemberExpr(MemberExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="IndexExpr"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitIndexExpr(IndexExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="LambdaExpr"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitLambdaExpr(LambdaExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="ConditionalExpr"/> through the default handler unless
    /// overridden.
    /// </summary>
    public virtual T VisitConditionalExpr(ConditionalExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="MatchExpr"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitMatchExpr(MatchExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="ListExpr"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitListExpr(ListExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="ObjectExpr"/> through the default handler unless overridden.
    /// </summary>
    public virtual T VisitObjectExpr(ObjectExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="InterpolatedStringExpr"/> through the default handler unless
    /// overridden.
    /// </summary>
    public virtual T VisitInterpolatedStringExpr(InterpolatedStringExpr node) => DefaultVisit(node);
    /// <summary>
    /// Visits <see cref="FilterExpr"/> through the default handler unless overridden.
    /// </summary>
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
