using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class ColonPipeTests
{
    [Test]
    public void ColonPipe_UserFunction_ResolvesOverloadByTargetType()
    {
        var registry = new TypeRegistry();

        // Two overloads: ok(string) and ok(Request)
        var okString = new FunctionDefinition(
            "ok", "string", "Response", [],
            [], 1, false, BodyExpression: new LiteralExpr("ok-string"));
        var okRequest = new FunctionDefinition(
            "ok", "Request", "Response", [],
            [], 2, false, BodyExpression: new LiteralExpr("ok-request"));
        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["ok"] = [okString, okRequest]
        };

        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry, functions: functions);

        // "hello":ok → should resolve ok(string), not ok(Request)
        var expr = new CallExpr(
            new LiteralExpr("hello"),
            "ok",
            []);

        var result = evaluator.EvaluateField(expr, "dummy", "Request");
        Assert.That(result, Is.EqualTo("ok-string"));
    }

    [Test]
    public void ColonPipe_UserFunction_DataObjectResolvesOverload()
    {
        var registry = new TypeRegistry();
        registry.Register(new TypeDescriptor("Response"));

        var transformResponse = new FunctionDefinition(
            "transform", "Response", "string", [],
            [], 1, false, BodyExpression: new LiteralExpr("transformed"));
        var transformString = new FunctionDefinition(
            "transform", "string", "string", [],
            [], 2, false, BodyExpression: new LiteralExpr("string-path"));
        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["transform"] = [transformResponse, transformString]
        };

        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry, functions: functions);

        // DataObject("Response"):transform → should resolve transform(Response)
        var responseObj = new DataObject("Response");
        responseObj.Set("StatusCode", 200);

        var expr = new CallExpr(
            new LiteralExpr("placeholder"),  // We'll use the item itself
            "transform",
            []);

        // Evaluate with a Response DataObject as target
        var targetExpr = new IdentifierExpr("item");
        var pipeExpr = new CallExpr(targetExpr, "transform", []);

        var result = evaluator.EvaluateField(pipeExpr, responseObj, "Response");
        Assert.That(result, Is.EqualTo("transformed"));
    }

    [Test]
    public void ColonPipe_BuiltinText_ConvertsBytesToString()
    {
        var registry = new TypeRegistry();
        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry);

        // Create a DataObject with a Body field containing bytes
        var response = new DataObject("Response");
        response.Set("Body", System.Text.Encoding.UTF8.GetBytes("Hello, World!"));

        // response.Body:Text → should convert bytes to UTF-8 string
        var bodyAccess = new MemberAccessExpr(new IdentifierExpr("item"), "Body");
        var textPipe = new CallExpr(bodyAccess, "Text", []);

        var result = evaluator.EvaluateField(textPipe, response, "Response");
        Assert.That(result, Is.EqualTo("Hello, World!"));
    }

    [Test]
    public void ColonPipe_BuiltinText_ConvertsStringIdentity()
    {
        var registry = new TypeRegistry();
        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry);

        // "hello":Text → should return "hello" unchanged
        var expr = new CallExpr(
            new LiteralExpr("hello"),
            "Text",
            []);

        var result = evaluator.EvaluateField(expr, "dummy", "string");
        Assert.That(result, Is.EqualTo("hello"));
    }

    [Test]
    public void ColonPipe_NullTarget_ReturnsNull()
    {
        var registry = new TypeRegistry();

        var myFunc = new FunctionDefinition(
            "process", "string", "string", [],
            [], 1, false, BodyExpression: new LiteralExpr("processed"));
        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["process"] = [myFunc]
        };

        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry, functions: functions);

        // null:process → should return null, not apply function to outer item
        var expr = new CallExpr(
            new IdentifierExpr("null"),
            "process",
            []);

        var result = evaluator.EvaluateField(expr, "outer-item", "string");
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ColonPipe_ChainedFunctions()
    {
        var registry = new TypeRegistry();

        // toUpper(string) → returns "UPPER"
        var toUpper = new FunctionDefinition(
            "toUpper", "string", "string", [],
            [], 1, false, BodyExpression: new LiteralExpr("UPPER"));
        // wrap(string) → returns "wrapped"
        var wrap = new FunctionDefinition(
            "wrap", "string", "string", [],
            [], 2, false, BodyExpression: new LiteralExpr("wrapped"));
        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["toUpper"] = [toUpper],
            ["wrap"] = [wrap]
        };

        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry, functions: functions);

        // "hello":toUpper:wrap → chains: toUpper("hello") → "UPPER", then wrap("UPPER") → "wrapped"
        var firstPipe = new CallExpr(new LiteralExpr("hello"), "toUpper", []);
        var secondPipe = new CallExpr(firstPipe, "wrap", []);

        var result = evaluator.EvaluateField(secondPipe, "dummy", "string");
        Assert.That(result, Is.EqualTo("wrapped"));
    }

    [Test]
    public void ColonPipe_BuiltinText_NullReturnsNull()
    {
        var registry = new TypeRegistry();
        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry);

        // null:Text → should return null
        var expr = new CallExpr(
            new IdentifierExpr("null"),
            "Text",
            []);

        var result = evaluator.EvaluateField(expr, "dummy", "string");
        Assert.That(result, Is.Null);
    }
}
