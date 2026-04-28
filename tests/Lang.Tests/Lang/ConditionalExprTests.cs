using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class ConditionalExprTests
{
    // --- Parser tests ---

    [Test]
    public void Parse_SimpleTernary()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => Type.IsPublic ? 'public' | 'internal'", "test.cop");
        var body = file.Predicates[0].Body;
        Assert.That(body, Is.TypeOf<ConditionalExpr>());
        var cond = (ConditionalExpr)body;
        Assert.That(cond.Condition, Is.TypeOf<MemberAccessExpr>());
        Assert.That(cond.TrueExpr, Is.TypeOf<LiteralExpr>());
        Assert.That(cond.FalseExpr, Is.TypeOf<LiteralExpr>());
    }

    [Test]
    public void Parse_NestedTernaryInTrueBranch()
    {
        // a ? b ? c | d | e  →  a ? (b ? c | d) | e
        var file = ScriptParser.Parse(
            "predicate test(Type) => Type.IsAbstract ? Type.IsPublic ? 'abs-pub' | 'abs-priv' | 'concrete'", "test.cop");
        var body = (ConditionalExpr)file.Predicates[0].Body;
        Assert.That(body.TrueExpr, Is.TypeOf<ConditionalExpr>(), "True branch should be a nested ternary");
        Assert.That(body.FalseExpr, Is.TypeOf<LiteralExpr>());

        var inner = (ConditionalExpr)body.TrueExpr;
        Assert.That(((LiteralExpr)inner.TrueExpr).Value, Is.EqualTo("abs-pub"));
        Assert.That(((LiteralExpr)inner.FalseExpr).Value, Is.EqualTo("abs-priv"));
        Assert.That(((LiteralExpr)body.FalseExpr).Value, Is.EqualTo("concrete"));
    }

    [Test]
    public void Parse_NestedTernaryInFalseBranch()
    {
        // a ? b | c ? d | e  →  a ? b | (c ? d | e)  (right-associative)
        var file = ScriptParser.Parse(
            "predicate test(Type) => Type.IsAbstract ? 'abstract' | Type.IsSealed ? 'sealed' | 'open'", "test.cop");
        var body = (ConditionalExpr)file.Predicates[0].Body;
        Assert.That(body.TrueExpr, Is.TypeOf<LiteralExpr>());
        Assert.That(body.FalseExpr, Is.TypeOf<ConditionalExpr>(), "False branch should be a nested ternary");

        var inner = (ConditionalExpr)body.FalseExpr;
        Assert.That(((LiteralExpr)inner.TrueExpr).Value, Is.EqualTo("sealed"));
        Assert.That(((LiteralExpr)inner.FalseExpr).Value, Is.EqualTo("open"));
    }

    [Test]
    public void Parse_TernaryWithLogicalCondition()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => Type.IsPublic && Type.IsSealed ? 'ok' | 'bad'", "test.cop");
        var body = (ConditionalExpr)file.Predicates[0].Body;
        Assert.That(body.Condition, Is.TypeOf<BinaryExpr>());
        Assert.That(((BinaryExpr)body.Condition).Operator, Is.EqualTo("&&"));
    }

    [Test]
    public void Parse_TernaryWithOrCondition()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => Type.IsPublic || Type.IsProtected ? 'visible' | 'hidden'", "test.cop");
        var body = (ConditionalExpr)file.Predicates[0].Body;
        Assert.That(body.Condition, Is.TypeOf<BinaryExpr>());
        Assert.That(((BinaryExpr)body.Condition).Operator, Is.EqualTo("||"));
    }

    [Test]
    public void Parse_TernaryWithParenthesizedBitwiseOr()
    {
        // Bitwise | inside parens should still work
        var file = ScriptParser.Parse(
            "predicate test(Type) => Type.IsPublic ? (1 | 2) | 0", "test.cop");
        var body = (ConditionalExpr)file.Predicates[0].Body;
        Assert.That(body.TrueExpr, Is.TypeOf<BinaryExpr>());
        Assert.That(((BinaryExpr)body.TrueExpr).Operator, Is.EqualTo("|"));
        Assert.That(body.FalseExpr, Is.TypeOf<LiteralExpr>());
    }

    [Test]
    public void Parse_TernaryMissingFalseBranch_Throws()
    {
        Assert.That(() => ScriptParser.Parse(
            "predicate test(Type) => Type.IsPublic ? 'yes'", "test.cop"),
            Throws.TypeOf<ParseException>());
    }

    [Test]
    public void Parse_TernaryInLetExpression_ParsesAsFilterExpr()
    {
        // Ternary can appear inside predicate bodies, not at top-level let
        // (let declarations expect collection expressions)
        var file = ScriptParser.Parse(
            "predicate label(Type) => Type.IsPublic ? 'public' | 'internal'", "test.cop");
        Assert.That(file.Predicates[0].Body, Is.TypeOf<ConditionalExpr>());
    }

    // --- Evaluation tests ---

    [Test]
    public void Eval_BaselineNoTernary_FilterWorks()
    {
        // code.cop already defines: predicate isSealed(Type) => Type.Modifiers:isSet(Sealed)
        // Use it directly — no need to redefine.
        var file = ScriptParser.Parse(
            "foreach Types:csharp:isSealed => PRINT('{item.Name}')", "test.cop");
        var interpreter = TestInterpreter.Create();
        var docs = TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"));
        var result = interpreter.Run([TestInterpreter.CodePackage, file], docs);
        Assert.That(result.Outputs.Any(o => o.Message.Contains("GoodClient")), Is.True,
            $"Baseline: sealed type should pass isSealed. Got {result.Outputs.Count} outputs: [{string.Join(", ", result.Outputs.Select(o => o.Message))}]");
    }

    [Test]
    public void Eval_TernaryTrueCondition_FiltersByTrueBranch()
    {
        // code.cop defines: isSealed, isAbstract, isPublic predicates via Modifiers:isSet(...)
        // true ? isSealed | false  →  always evaluates isSealed
        var file = ScriptParser.Parse(@"
predicate alwaysCheckSealed(Type) => true ? isSealed | false
foreach Types:csharp:alwaysCheckSealed => PRINT('{item.Name}')
", "test.cop");

        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(
            [TestInterpreter.CodePackage, file],
            TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"))).Outputs;

        // GoodClient is sealed, so the true branch (isSealed) returns true
        Assert.That(outputs.Any(o => o.Message.Contains("GoodClient")), Is.True,
            "Sealed type should pass when true branch returns isSealed");
    }

    [Test]
    public void Eval_TernaryFalseCondition_FiltersByFalseBranch()
    {
        // false ? true | isPublic  →  always evaluates isPublic
        var file = ScriptParser.Parse(@"
predicate checkPublicViaFalse(Type) => false ? false | isPublic
foreach Types:csharp:checkPublicViaFalse => PRINT('{item.Name}')
", "test.cop");

        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(
            [TestInterpreter.CodePackage, file],
            TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"))).Outputs;

        Assert.That(outputs.Any(o => o.Message.Contains("GoodClient")), Is.True,
            "Public type should pass when false branch returns isPublic");
    }

    [Test]
    public void Eval_NestedTernary_EvaluatesCorrectly()
    {
        // isAbstract ? false | isSealed ? true | false
        // For GoodClient (sealed, not abstract): outer=false → inner: sealed=true → true
        var file = ScriptParser.Parse(@"
predicate sealedNotAbstract(Type) => isAbstract ? false | isSealed ? true | false
foreach Types:csharp:sealedNotAbstract => PRINT('{item.Name}')
", "test.cop");

        var interpreter = TestInterpreter.Create();
        var outputs = interpreter.Run(
            [TestInterpreter.CodePackage, file],
            TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"))).Outputs;

        Assert.That(outputs.Any(o => o.Message.Contains("GoodClient")), Is.True,
            "Sealed non-abstract type should pass nested ternary filter");
    }

    private static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Samples", fileName);
}
