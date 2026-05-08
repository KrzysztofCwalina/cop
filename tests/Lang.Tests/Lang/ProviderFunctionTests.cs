using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class ProviderFunctionTests
{
    [Test]
    public void RegisterProviderFunction_IsResolvable()
    {
        var registry = new TypeRegistry();
        Func<List<object?>, Task<object?>> func = args => Task.FromResult<object?>("hello");
        registry.RegisterProviderFunction("http", "Get", func);

        Assert.That(registry.IsProviderFunctionNamespace("http"), Is.True);
        Assert.That(registry.ResolveProviderFunction("http", "Get"), Is.Not.Null);
        Assert.That(registry.ResolveProviderFunction("http", "Unknown"), Is.Null);
        Assert.That(registry.IsProviderFunctionNamespace("unknown"), Is.False);
    }

    [Test]
    public void ProviderFunction_CaseInsensitiveName()
    {
        var registry = new TypeRegistry();
        Func<List<object?>, Task<object?>> func = args => Task.FromResult<object?>("ok");
        registry.RegisterProviderFunction("http", "Post", func);

        // Function names are case-insensitive
        Assert.That(registry.ResolveProviderFunction("http", "post"), Is.Not.Null);
        Assert.That(registry.ResolveProviderFunction("http", "POST"), Is.Not.Null);
    }

    [Test]
    public void EvalPredicateCall_DispatchesToProviderFunction()
    {
        var registry = new TypeRegistry();
        Func<List<object?>, Task<object?>> getFunc = args =>
        {
            var url = args[0]?.ToString() ?? "";
            var result = new DataObject("Response");
            result.Set("StatusCode", 200);
            result.Set("Body", $"response from {url}");
            return Task.FromResult<object?>(result);
        };
        registry.RegisterProviderFunction("http", "Get", getFunc);

        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry);

        // http.Get('https://example.com') → PredicateCallExpr(IdentifierExpr("http"), "Get", [url])
        var expr = new PredicateCallExpr(
            new IdentifierExpr("http"),
            "Get",
            [new LiteralExpr("https://example.com")]);

        var result = evaluator.EvaluateField(expr, "dummy", "string");

        Assert.That(result, Is.InstanceOf<DataObject>());
        var response = (DataObject)result!;
        Assert.That(response.GetField("StatusCode"), Is.EqualTo(200));
        Assert.That(response.GetField("Body"), Is.EqualTo("response from https://example.com"));
    }

    [Test]
    public void ProviderFunction_MemberAccessOnResult()
    {
        var registry = new TypeRegistry();
        Func<List<object?>, Task<object?>> postFunc = args =>
        {
            var result = new DataObject("Response");
            result.Set("StatusCode", 201);
            result.Set("Body", new byte[] { 0x48, 0x69 });
            result.Set("ContentType", "text/plain");
            return Task.FromResult<object?>(result);
        };
        registry.RegisterProviderFunction("http", "Post", postFunc);

        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry);

        // http.Post('url', 'body').StatusCode
        var callExpr = new PredicateCallExpr(
            new IdentifierExpr("http"),
            "Post",
            [new LiteralExpr("https://api.example.com"), new LiteralExpr("payload")]);
        var memberExpr = new MemberAccessExpr(callExpr, "StatusCode");

        var result = evaluator.EvaluateField(memberExpr, "dummy", "string");
        Assert.That(result, Is.EqualTo(201));
    }

    [Test]
    public void ProviderFunction_UnknownFunctionThrows()
    {
        var registry = new TypeRegistry();
        Func<List<object?>, Task<object?>> func = args => Task.FromResult<object?>("ok");
        registry.RegisterProviderFunction("http", "Get", func);

        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry);

        // http.Delete(...) should throw since Delete is not registered
        var expr = new PredicateCallExpr(
            new IdentifierExpr("http"),
            "Delete",
            [new LiteralExpr("https://example.com")]);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            evaluator.EvaluateField(expr, "dummy", "string"));
        Assert.That(ex!.Message, Does.Contain("Unknown function 'http.Delete'"));
    }

    [Test]
    public void ProviderFunction_NotShadowedByUserFunction()
    {
        var registry = new TypeRegistry();
        bool providerCalled = false;
        Func<List<object?>, Task<object?>> getFunc = args =>
        {
            providerCalled = true;
            return Task.FromResult<object?>("provider-result");
        };
        registry.RegisterProviderFunction("http", "Get", getFunc);

        // Register a user-defined function named "Get" (should NOT shadow http.Get)
        var userFunc = new FunctionDefinition(
            "Get", "string", "", [],
            [], 1, false, BodyExpression: new LiteralExpr("user-result"));
        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["Get"] = [userFunc]
        };

        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry, functions: functions);

        // http.Get('url') should call the provider, not the user function
        var expr = new PredicateCallExpr(
            new IdentifierExpr("http"),
            "Get",
            [new LiteralExpr("https://example.com")]);

        var result = evaluator.EvaluateField(expr, "dummy", "string");
        Assert.That(providerCalled, Is.True);
        Assert.That(result, Is.EqualTo("provider-result"));
    }

    [Test]
    public void ProviderFunction_ErrorValueReturned()
    {
        var registry = new TypeRegistry();
        Func<List<object?>, Task<object?>> failFunc = args =>
            Task.FromResult<object?>(new ErrorValue("connection refused"));
        registry.RegisterProviderFunction("http", "Get", failFunc);

        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry);

        var expr = new PredicateCallExpr(
            new IdentifierExpr("http"),
            "Get",
            [new LiteralExpr("https://bad-url.invalid")]);

        var result = evaluator.EvaluateField(expr, "dummy", "string");
        Assert.That(ErrorValue.IsError(result), Is.True);
    }

    [Test]
    public void ProviderFunction_HeadersPassedAsDataObject()
    {
        var registry = new TypeRegistry();
        DataObject? receivedHeaders = null;
        Func<List<object?>, Task<object?>> postFunc = args =>
        {
            receivedHeaders = args.Count > 2 ? args[2] as DataObject : null;
            return Task.FromResult<object?>(new DataObject("Response"));
        };
        registry.RegisterProviderFunction("http", "Post", postFunc);

        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", registry);

        // http.Post('url', 'body', { Authorization: 'Bearer token' })
        var headersExpr = new ObjectLiteralExpr(null, new Dictionary<string, Expression>
        {
            ["Authorization"] = new LiteralExpr("Bearer token"),
            ["Content-Type"] = new LiteralExpr("application/json")
        });

        var expr = new PredicateCallExpr(
            new IdentifierExpr("http"),
            "Post",
            [new LiteralExpr("https://api.example.com"), new LiteralExpr("{}"), headersExpr]);

        evaluator.EvaluateField(expr, "dummy", "string");

        Assert.That(receivedHeaders, Is.Not.Null);
        Assert.That(receivedHeaders!.GetField("Authorization"), Is.EqualTo("Bearer token"));
        Assert.That(receivedHeaders.GetField("Content-Type"), Is.EqualTo("application/json"));
    }
}
