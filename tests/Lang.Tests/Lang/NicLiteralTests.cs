using Cop.Lang;
using Cop.Providers;
using Cop.Providers.SourceModel;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class NicLiteralTests
{
    private static TypeDeclaration MakeType(string name = "Foo") =>
        new(name, TypeKind.Class, Modifier.Public, [], [], [], [], [], [], 1);

    private static TypeRegistry CreateTestRegistry()
    {
        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        return registry;
    }

    // --- Parser tests ---

    [Test]
    public void Parse_NicLiteral_InExpression()
    {
        var file = ScriptParser.Parse(
            "predicate test(Type) => nic", "test.cop");
        var body = file.Predicates[0].Body;
        Assert.That(body, Is.TypeOf<NicExpr>());
    }

    [Test]
    public void Parse_NicLiteral_InObjectField()
    {
        var file = ScriptParser.Parse(
            "let config = { Name: 'hello', Value: nic }", "test.cop");
        var expr = (ObjectLiteralExpr)file.LetDeclarations[0].ValueExpression!;
        Assert.That(expr.Fields["Value"], Is.TypeOf<NicExpr>());
    }

    [Test]
    public void Parse_NicLiteral_InLetBinding()
    {
        var file = ScriptParser.Parse(
            "let x = nic", "test.cop");
        var expr = file.LetDeclarations[0].ValueExpression;
        Assert.That(expr, Is.TypeOf<NicExpr>());
    }

    [Test]
    public void Parse_NicAsObjectFieldName()
    {
        var file = ScriptParser.Parse(
            "let config = { nic: 42 }", "test.cop");
        var expr = (ObjectLiteralExpr)file.LetDeclarations[0].ValueExpression!;
        Assert.That(expr.Fields.ContainsKey("nic"), Is.True);
    }

    // --- Evaluation tests ---

    [Test]
    public void Eval_NicLiteral_IsFalsy()
    {
        var source = "predicate test(Type) => nic";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType(), "Type");
        Assert.That(result, Is.False);
    }

    [Test]
    public void Eval_NicLiteral_ReturnsNull()
    {
        var source = "predicate test(Type) => nic";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var result = evaluator.EvaluateField(file.Predicates[0].Body, MakeType(), "Type");
        Assert.That(result, Is.Null);
    }

    [Test]
    public void Eval_ObjectWithNicField_HasNullValue()
    {
        var source = "predicate test(Type) => MyType { Name: 'hello', Extra: nic }";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var result = evaluator.EvaluateField(file.Predicates[0].Body, MakeType(), "Type");
        Assert.That(result, Is.TypeOf<ScriptObject>());
        var obj = (ScriptObject)result!;
        Assert.That(obj.GetField("Name"), Is.EqualTo("hello"));
        Assert.That(obj.GetField("Extra"), Is.Null);
    }

    [Test]
    public void Eval_NicInConditional()
    {
        var source = "predicate test(Type) => false ? 'value' | nic";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var result = evaluator.EvaluateField(file.Predicates[0].Body, MakeType(), "Type");
        Assert.That(result, Is.Null);
    }

    // --- JSON serialization test ---

    [Test]
    public void ScriptObject_ToJson_NullField_SerializesAsNull()
    {
        var fields = new Dictionary<string, object?>
        {
            ["name"] = "hello",
            ["value"] = null
        };
        var obj = new ScriptObject("Test", fields);
        var json = obj.ToJson();
        Assert.That(json, Does.Contain("\"value\": null"));
    }

    // --- QueryFingerprint test ---

    [Test]
    public void QueryFingerprint_NicExpr_SerializedAsNic()
    {
        var filters = new List<Expression> { new NicExpr() };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Does.Contain("nic"));
    }

    // --- Multi-line object literal test ---

    [Test]
    public void Parse_ObjectLiteral_NewlineSeparatedFields()
    {
        var source = "let obj = {\n    y = 5\n    x = 'hi'\n}";
        var file = ScriptParser.Parse(source, "test.cop");
        var expr = (ObjectLiteralExpr)file.LetDeclarations[0].ValueExpression!;
        Assert.That(expr.Fields, Has.Count.EqualTo(2));
        Assert.That(expr.Fields.ContainsKey("y"), Is.True);
        Assert.That(expr.Fields.ContainsKey("x"), Is.True);
    }

    [Test]
    public void Parse_ObjectLiteral_FieldWithPredicateCallValue()
    {
        var source = "predicate test(Type) => {\n    y = Type.Name:startsWith('A')\n    x = 'hi'\n}";
        var file = ScriptParser.Parse(source, "test.cop");
        var body = (ObjectLiteralExpr)file.Predicates[0].Body;
        Assert.That(body.Fields, Has.Count.EqualTo(2));
        Assert.That(body.Fields["y"], Is.TypeOf<PredicateCallExpr>());
        Assert.That(body.Fields["x"], Is.TypeOf<LiteralExpr>());
    }
}
