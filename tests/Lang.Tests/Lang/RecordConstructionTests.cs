using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class RecordConstructionTests
{
    // --- Parser tests ---

    [Test]
    public void Parse_TypedRecord_WithColonSyntax()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => MyType { Name: 'hello', Age: 42 }", "test.cop");
        var body = file.Predicates[0].Body;
        Assert.That(body, Is.TypeOf<ObjectLiteralExpr>());
        var obj = (ObjectLiteralExpr)body;
        Assert.That(obj.TypeName, Is.EqualTo("MyType"));
        Assert.That(obj.Fields, Has.Count.EqualTo(2));
        Assert.That(obj.Fields.ContainsKey("Name"), Is.True);
        Assert.That(obj.Fields.ContainsKey("Age"), Is.True);
    }

    [Test]
    public void Parse_TypedRecord_WithEqualsSyntax()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => MyType { Name = 'hello', Age = 42 }", "test.cop");
        var body = (ObjectLiteralExpr)file.Predicates[0].Body;
        Assert.That(body.TypeName, Is.EqualTo("MyType"));
        Assert.That(body.Fields, Has.Count.EqualTo(2));
    }

    [Test]
    public void Parse_UntypedObject_StillWorks()
    {
        var source = "let config = { Debug = true, Verbose = false }";
        var file = ScriptParser.Parse(source, "test.cop");
        var expr = (ObjectLiteralExpr)file.LetDeclarations[0].ValueExpression!;
        Assert.That(expr.TypeName, Is.Null);
        Assert.That(expr.Fields, Has.Count.EqualTo(2));
    }

    [Test]
    public void Parse_NestedRecordConstruction()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => Outer { Inner: Inner { X: 1 }, Name: 'test' }", "test.cop");
        var body = (ObjectLiteralExpr)file.Predicates[0].Body;
        Assert.That(body.TypeName, Is.EqualTo("Outer"));
        Assert.That(body.Fields["Inner"], Is.TypeOf<ObjectLiteralExpr>());
        var inner = (ObjectLiteralExpr)body.Fields["Inner"];
        Assert.That(inner.TypeName, Is.EqualTo("Inner"));
    }

    [Test]
    public void Parse_RecordInTernary()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => isPublic ? Result { Status: 'ok' } | Result { Status: 'fail' }", "test.cop");
        var body = (ConditionalExpr)file.Predicates[0].Body;
        Assert.That(body.TrueExpr, Is.TypeOf<ObjectLiteralExpr>());
        Assert.That(body.FalseExpr, Is.TypeOf<ObjectLiteralExpr>());
        Assert.That(((ObjectLiteralExpr)body.TrueExpr).TypeName, Is.EqualTo("Result"));
    }

    [Test]
    public void Parse_DuplicateField_Throws()
    {
        Assert.That(() => ScriptParser.Parse(
            "predicate test(Type) => MyType { X: 1, X: 2 }", "test.cop"),
            Throws.TypeOf<ParseException>().With.Message.Contains("Duplicate field"));
    }

    [Test]
    public void Parse_EmptyRecord()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => Empty { }", "test.cop");
        var body = (ObjectLiteralExpr)file.Predicates[0].Body;
        Assert.That(body.TypeName, Is.EqualTo("Empty"));
        Assert.That(body.Fields, Is.Empty);
    }

    // --- Evaluation tests ---

    [Test]
    public void Eval_TypedRecord_FieldAccess()
    {
        // Construct a record and access its field in the same expression
        var file = ScriptParser.Parse(@"
predicate nameMatch(Type) => MyRecord { Name: Type.Name, Value: 42 }.Name == Type.Name
foreach Types:csharp:nameMatch => '{item.Name}'
", "test.cop");
        var interpreter = TestInterpreter.Create();
        var docs = TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"));
        var result = interpreter.Run([TestInterpreter.CodePackage, file], docs);
        Assert.That(result.Outputs.Any(o => o.Message.Contains("GoodClient")), Is.True,
            "Record field access should return the value set during construction");
    }

    [Test]
    public void Eval_RecordInTernary()
    {
        // Use record construction in ternary branches
        var file = ScriptParser.Parse(@"
predicate checkResult(Type) => (isSealed ? Result { Ok: true } | Result { Ok: false }).Ok
foreach Types:csharp:checkResult => '{item.Name}'
", "test.cop");
        var interpreter = TestInterpreter.Create();
        var docs = TestInterpreter.ParseSourceFiles(SamplePath("GoodClient.cs"));
        var result = interpreter.Run([TestInterpreter.CodePackage, file], docs);
        // GoodClient is sealed, so ternary takes true branch, Ok=true → passes filter
        Assert.That(result.Outputs.Any(o => o.Message.Contains("GoodClient")), Is.True);
    }

    private static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Samples", fileName);
}
