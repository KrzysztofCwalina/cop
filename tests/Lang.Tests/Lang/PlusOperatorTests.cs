using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class PlusOperatorTests
{
    // --- Parser tests ---

    [Test]
    public void Parse_SimplePlus()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => 1 + 2", "test.cop");
        var body = file.Predicates[0].Body;
        Assert.That(body, Is.TypeOf<BinaryExpr>());
        var bin = (BinaryExpr)body;
        Assert.That(bin.Operator, Is.EqualTo("+"));
        Assert.That(((LiteralExpr)bin.Left).Value, Is.EqualTo(1));
        Assert.That(((LiteralExpr)bin.Right).Value, Is.EqualTo(2));
    }

    [Test]
    public void Parse_StringConcat()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => 'hello' + ' world'", "test.cop");
        var body = (BinaryExpr)file.Predicates[0].Body;
        Assert.That(body.Operator, Is.EqualTo("+"));
    }

    [Test]
    public void Parse_ListConcat()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => [1 2] + [3]", "test.cop");
        var body = (BinaryExpr)file.Predicates[0].Body;
        Assert.That(body.Operator, Is.EqualTo("+"));
        Assert.That(body.Left, Is.TypeOf<ListLiteralExpr>());
        Assert.That(body.Right, Is.TypeOf<ListLiteralExpr>());
    }

    [Test]
    public void Parse_ChainedPlus()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => 1 + 2 + 3", "test.cop");
        var body = (BinaryExpr)file.Predicates[0].Body;
        Assert.That(body.Operator, Is.EqualTo("+"));
        Assert.That(body.Left, Is.TypeOf<BinaryExpr>(), "Should be left-associative");
    }

    [Test]
    public void Parse_PlusAndMinus()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => 5 + 3 - 1", "test.cop");
        var body = (BinaryExpr)file.Predicates[0].Body;
        Assert.That(body.Operator, Is.EqualTo("-"));
        Assert.That(body.Left, Is.TypeOf<BinaryExpr>());
        Assert.That(((BinaryExpr)body.Left).Operator, Is.EqualTo("+"));
    }

    [Test]
    public void Parse_PlusWithComparison()
    {
        // + should bind tighter than ==
        var file = ScriptParser.Parse(
            "predicate test(Type) => 1 + 2 == 3", "test.cop");
        var body = (BinaryExpr)file.Predicates[0].Body;
        Assert.That(body.Operator, Is.EqualTo("=="));
        Assert.That(body.Left, Is.TypeOf<BinaryExpr>());
        Assert.That(((BinaryExpr)body.Left).Operator, Is.EqualTo("+"));
    }

    // --- Evaluation tests ---

    [Test]
    public void Eval_IntegerAddition()
    {
        // Use + in a predicate comparison: (1 + 2) == 3
        var file = ScriptParser.Parse(@"
predicate addCheck(Type) => (1 + 2) == 3
foreach Types:addCheck => '{item.Name}'
", "test.cop");
        var interpreter = TestInterpreter.Create();
        var docs = TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"));
        var result = interpreter.Run([TestInterpreter.CodePackage, file], docs);
        Assert.That(result.Outputs.Count, Is.GreaterThan(0), "1 + 2 should equal 3");
    }

    [Test]
    public void Eval_StringConcatenation()
    {
        var file = ScriptParser.Parse(@"
predicate concatCheck(Type) => ('hello' + ' world') == 'hello world'
foreach Types:concatCheck => '{item.Name}'
", "test.cop");
        var interpreter = TestInterpreter.Create();
        var docs = TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"));
        var result = interpreter.Run([TestInterpreter.CodePackage, file], docs);
        Assert.That(result.Outputs.Count, Is.GreaterThan(0), "String concat should work");
    }

    [Test]
    public void Eval_ListConcatenation()
    {
        var file = ScriptParser.Parse(@"
predicate listCheck(Type) => ([1 2] + [3 4]).Count == 4
foreach Types:listCheck => '{item.Name}'
", "test.cop");
        var interpreter = TestInterpreter.Create();
        var docs = TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"));
        var result = interpreter.Run([TestInterpreter.CodePackage, file], docs);
        Assert.That(result.Outputs.Count, Is.GreaterThan(0), "List concat should produce 4 items");
    }

    [Test]
    public void Eval_ListAppendElement()
    {
        var file = ScriptParser.Parse(@"
predicate appendCheck(Type) => ([1 2] + 3).Count == 3
foreach Types:appendCheck => '{item.Name}'
", "test.cop");
        var interpreter = TestInterpreter.Create();
        var docs = TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"));
        var result = interpreter.Run([TestInterpreter.CodePackage, file], docs);
        Assert.That(result.Outputs.Count, Is.GreaterThan(0), "List append should produce 3 items");
    }

    [Test]
    public void Eval_Subtraction()
    {
        var file = ScriptParser.Parse(@"
predicate subCheck(Type) => (10 - 3) == 7
foreach Types:subCheck => '{item.Name}'
", "test.cop");
        var interpreter = TestInterpreter.Create();
        var docs = TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"));
        var result = interpreter.Run([TestInterpreter.CodePackage, file], docs);
        Assert.That(result.Outputs.Count, Is.GreaterThan(0), "10 - 3 should equal 7");
    }

    [Test]
    public void Eval_ListPlusDoesNotMutate()
    {
        // Both expressions should yield different counts
        var file = ScriptParser.Parse(@"
predicate mutationCheck(Type) => [1 2].Count == 2 && ([1 2] + [3]).Count == 3
foreach Types:mutationCheck => '{item.Name}'
", "test.cop");
        var interpreter = TestInterpreter.Create();
        var docs = TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"));
        var result = interpreter.Run([TestInterpreter.CodePackage, file], docs);
        Assert.That(result.Outputs.Count, Is.GreaterThan(0), "List + should not mutate original");
    }

    private static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Samples", fileName);
}
