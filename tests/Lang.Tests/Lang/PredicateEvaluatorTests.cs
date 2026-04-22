using Cop.Lang;
using Cop.Providers;
using Cop.Providers.SourceModel;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class PredicateEvaluatorTests
{
    private static PredicateEvaluator CreateEvaluator(
        Dictionary<string, List<PredicateDefinition>>? predicates = null,
        string filePath = "test.cs")
    {
        var registry = new TypeRegistry();
        CodeTypeRegistrar.Register(registry);
        return new PredicateEvaluator(predicates ?? [], filePath, registry);
    }

    private static TypeReference TR(string name) => new(name, null, [], name);

    private static TypeRegistry CreateTestRegistry()
    {
        var registry = new TypeRegistry();
        CodeTypeRegistrar.Register(registry);
        return registry;
    }

    private static TypeDeclaration MakeType(string name, bool isSealed = false, bool isPublic = true,
        List<string>? baseTypes = null, List<MethodDeclaration>? methods = null,
        List<MethodDeclaration>? constructors = null)
    {
        var mods = Modifier.None;
        if (isPublic) mods |= Modifier.Public;
        if (isSealed) mods |= Modifier.Sealed;
        return new TypeDeclaration(name, TypeKind.Class, mods,
            baseTypes ?? [], [], constructors ?? [], methods ?? [], [], [], 1);
    }

    [Test]
    public void MemberAccess_TypeName()
    {
        var eval = CreateEvaluator();
        var type = MakeType("BlobClient");
        var expr = new MemberAccessExpr(new IdentifierExpr("Type"), "Name");
        var (result, _) = eval.EvaluateAsBool(
            new BinaryExpr(expr, "==", new LiteralExpr("BlobClient")),
            type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void StringMethod_EndsWith()
    {
        var eval = CreateEvaluator();
        var type = MakeType("BlobClient");
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Name"),
            "endsWith", [new LiteralExpr("Client")]);
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void StringMethod_StartsWith()
    {
        var eval = CreateEvaluator();
        var type = MakeType("BlobClient");
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Name"),
            "startsWith", [new LiteralExpr("Blob")]);
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void BooleanOperators_And()
    {
        var eval = CreateEvaluator();
        var type = MakeType("BlobClient", isSealed: true);
        var expr = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Public"),
            "&&",
            new MemberAccessExpr(new IdentifierExpr("Type"), "Sealed"));
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void BooleanOperators_Not()
    {
        var eval = CreateEvaluator();
        var type = MakeType("BlobClient");
        var expr = new UnaryExpr("!",
            new MemberAccessExpr(new IdentifierExpr("Type"), "Sealed"));
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void CollectionMethod_Any()
    {
        // Build IsOptionsType predicate
        var isOptionsType = new PredicateDefinition("IsOptionsType", "Parameter", null,
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Parameter"), "Type"),
                "endsWith", [new LiteralExpr("Options")]), 1);

        var eval = CreateEvaluator(new Dictionary<string, List<PredicateDefinition>>
        {
            ["IsOptionsType"] = [isOptionsType]
        });

        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("options", TR("BlobClientOptions"), false, false, false, 1)
        ], 1);

        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
            "any", [new IdentifierExpr("IsOptionsType")]);
        var (result, _) = eval.EvaluateAsBool(expr, ctor, "Constructor");
        Assert.That(result, Is.True);
    }

    [Test]
    public void CollectionMethod_None()
    {
        var isOptionsType = new PredicateDefinition("IsOptionsType", "Parameter", null,
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Parameter"), "Type"),
                "endsWith", [new LiteralExpr("Options")]), 1);

        var eval = CreateEvaluator(new Dictionary<string, List<PredicateDefinition>>
        {
            ["IsOptionsType"] = [isOptionsType]
        });

        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("connectionString", TR("string"), false, false, false, 1)
        ], 1);

        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
            "none", [new IdentifierExpr("IsOptionsType")]);
        var (result, _) = eval.EvaluateAsBool(expr, ctor, "Constructor");
        Assert.That(result, Is.True);
    }

    [Test]
    public void PredicateComposition()
    {
        // IsClient(Type) => Type.Name:endsWith("Client")
        var isClient = new PredicateDefinition("IsClient", "Type", null,
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Type"), "Name"),
                "endsWith", [new LiteralExpr("Client")]), 1);

        var eval = CreateEvaluator(new Dictionary<string, List<PredicateDefinition>>
        {
            ["IsClient"] = [isClient]
        });

        var type = MakeType("BlobClient");
        var (result, _) = eval.EvaluateAsBool(new IdentifierExpr("IsClient"), type, "Type");
        Assert.That(result, Is.True);

        var type2 = MakeType("SomeHelper");
        var (result2, _) = eval.EvaluateAsBool(new IdentifierExpr("IsClient"), type2, "Type");
        Assert.That(result2, Is.False);
    }

    [Test]
    public void ConstraintPredicate_EvaluatesAsFilter()
    {
        // "csharp" is now a regular predicate, not a built-in keyword.
        // Register it as a predicate that checks Type.Name:endsWith('Client').
        var csharpPred = new PredicateDefinition("csharp", "Type", null,
            new PredicateCallExpr(new MemberAccessExpr(new IdentifierExpr("Type"), "Name"),
                "endsWith", [new LiteralExpr("Client")]), 1);
        var preds = new Dictionary<string, List<PredicateDefinition>>
        {
            ["csharp"] = [csharpPred]
        };

        var eval = CreateEvaluator(preds);
        var (r1, _) = eval.EvaluateAsBool(new IdentifierExpr("csharp"), MakeType("FooClient"), "Type");
        Assert.That(r1, Is.True);

        var (r2, _) = eval.EvaluateAsBool(new IdentifierExpr("csharp"), MakeType("FooPython"), "Type");
        Assert.That(r2, Is.False);
    }

    [Test]
    public void ConstraintDispatch_MatchesConstrainedOverload()
    {
        // Constraint predicates that distinguish items
        var sealedPred = new PredicateDefinition("sealed", "Type", null,
            new MemberAccessExpr(new IdentifierExpr("Type"), "Sealed"), 1);
        var unsealedPred = new PredicateDefinition("unsealed", "Type", null,
            new UnaryExpr("!", new MemberAccessExpr(new IdentifierExpr("Type"), "Sealed")), 1);

        // Two constrained overloads of "client"
        var sealedClient = new PredicateDefinition("client", "Type", "sealed",
            new PredicateCallExpr(new MemberAccessExpr(new IdentifierExpr("Type"), "Name"),
                "endsWith", [new LiteralExpr("_client")]), 1);
        var unsealedClient = new PredicateDefinition("client", "Type", "unsealed",
            new PredicateCallExpr(new MemberAccessExpr(new IdentifierExpr("Type"), "Name"),
                "endsWith", [new LiteralExpr("Client")]), 1);

        var preds = new Dictionary<string, List<PredicateDefinition>>
        {
            ["sealed"] = [sealedPred],
            ["unsealed"] = [unsealedPred],
            ["client"] = [sealedClient, unsealedClient]
        };

        var eval = CreateEvaluator(preds);

        // Unsealed type "FooClient" → matches "unsealed" constraint → uses unsealedClient → true
        var (r1, _) = eval.EvaluateAsBool(new IdentifierExpr("client"), MakeType("FooClient", isSealed: false), "Type");
        Assert.That(r1, Is.True);

        // Sealed type "foo_client" → matches "sealed" constraint → uses sealedClient → true
        var (r2, _) = eval.EvaluateAsBool(new IdentifierExpr("client"), MakeType("foo_client", isSealed: true), "Type");
        Assert.That(r2, Is.True);
    }

    [Test]
    public void ConstraintDispatch_FallsBackToUnconstrained()
    {
        // A constraint that never matches
        var neverPred = new PredicateDefinition("never", "Type", null,
            new LiteralExpr(false), 1);

        var constrained = new PredicateDefinition("client", "Type", "never",
            new LiteralExpr(false), 1);
        var unconstrained = new PredicateDefinition("client", "Type", null,
            new PredicateCallExpr(new MemberAccessExpr(new IdentifierExpr("Type"), "Name"),
                "endsWith", [new LiteralExpr("Client")]), 1);

        var preds = new Dictionary<string, List<PredicateDefinition>>
        {
            ["never"] = [neverPred],
            ["client"] = [constrained, unconstrained]
        };

        // Constraint doesn't match → falls back to unconstrained → true for "FooClient"
        var eval = CreateEvaluator(preds);
        var (result, _) = eval.EvaluateAsBool(new IdentifierExpr("client"), MakeType("FooClient"), "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void ConstraintDispatch_NoMatch_ReturnsFalse()
    {
        // A constraint that never matches
        var neverPred = new PredicateDefinition("never", "Type", null,
            new LiteralExpr(false), 1);

        // Only a constrained overload, no unconstrained fallback
        var constrainedOnly = new PredicateDefinition("special", "Type", "never",
            new LiteralExpr(true), 1);
        var preds = new Dictionary<string, List<PredicateDefinition>>
        {
            ["never"] = [neverPred],
            ["special"] = [constrainedOnly]
        };

        // Constraint doesn't match, no fallback → false
        var eval = CreateEvaluator(preds);
        var (result, _) = eval.EvaluateAsBool(new IdentifierExpr("special"), MakeType("Foo"), "Type");
        Assert.That(result, Is.False);
    }

    [Test]
    public void Equality_Operators()
    {
        var eval = CreateEvaluator();
        var stmt = new StatementInfo("declaration", ["var"], "var", "x", [], 1, true);
        var eq = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("Statement"), "Kind"),
            "==", new LiteralExpr("declaration"));
        var (result, _) = eval.EvaluateAsBool(eq, stmt, "Statement");
        Assert.That(result, Is.True);

        var neq = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("Statement"), "Kind"),
            "!=", new LiteralExpr("call"));
        var (result2, _) = eval.EvaluateAsBool(neq, stmt, "Statement");
        Assert.That(result2, Is.True);
    }

    [Test]
    public void ContextCapture_AnyCapturesFirstMatch()
    {
        var isPublicAsync = new PredicateDefinition("IsPublicAsync", "Method", null,
            new BinaryExpr(
                new MemberAccessExpr(new IdentifierExpr("Method"), "Public"),
                "&&",
                new MemberAccessExpr(new IdentifierExpr("Method"), "Async")),
            1);

        var eval = CreateEvaluator(new Dictionary<string, List<PredicateDefinition>>
        {
            ["IsPublicAsync"] = [isPublicAsync]
        });

        var type = MakeType("TestClient", methods: [
            new MethodDeclaration("Sync", Modifier.Public, [], TR("void"), [], 1),
            new MethodDeclaration("AsyncMethod", Modifier.Public | Modifier.Async, [], TR("Task"), [], 2)
        ]);

        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Methods"),
            "any", [new IdentifierExpr("IsPublicAsync")]);
        var (result, ctx) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);

        var captured = ctx.Get("Method") as MethodDeclaration;
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Name, Is.EqualTo("AsyncMethod"));
    }

    [Test]
    public void GlobMatch_DoubleStarPrefix()
    {
        Assert.That(PredicateEvaluator.GlobMatch("src/Tests/Foo.cs", "**/Tests/**"), Is.True);
        Assert.That(PredicateEvaluator.GlobMatch("Tests/Foo.cs", "**/Tests/**"), Is.True);
        Assert.That(PredicateEvaluator.GlobMatch("src/Main/Foo.cs", "**/Tests/**"), Is.False);
    }

    [Test]
    public void GlobMatch_FileExtension()
    {
        Assert.That(PredicateEvaluator.GlobMatch("src/Foo.cs", "**/*.cs"), Is.True);
        Assert.That(PredicateEvaluator.GlobMatch("src/Foo.py", "**/*.cs"), Is.False);
    }

    #region Property-style First/Last/Single (no parentheses)

    [Test]
    public void PropertyAccess_First_ReturnsFirstItem()
    {
        var eval = CreateEvaluator();
        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("endpoint", TR("string"), false, false, false, 1),
            new ParameterDeclaration("options", TR("BlobClientOptions"), false, false, false, 2)
        ], 1);

        // Constructor.Parameters.First → MemberAccessExpr chain
        var expr = new MemberAccessExpr(
            new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
            "First");
        var (result, _) = eval.EvaluateAsBool(
            new BinaryExpr(
                new MemberAccessExpr(expr, "Name"),
                "==", new LiteralExpr("endpoint")),
            ctor, "Constructor");
        Assert.That(result, Is.True);
    }

    [Test]
    public void PropertyAccess_Last_ReturnsLastItem()
    {
        var eval = CreateEvaluator();
        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("endpoint", TR("string"), false, false, false, 1),
            new ParameterDeclaration("options", TR("BlobClientOptions"), false, false, false, 2)
        ], 1);

        var expr = new MemberAccessExpr(
            new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
            "Last");
        var (result, _) = eval.EvaluateAsBool(
            new BinaryExpr(
                new MemberAccessExpr(expr, "Name"),
                "==", new LiteralExpr("options")),
            ctor, "Constructor");
        Assert.That(result, Is.True);
    }

    [Test]
    public void PropertyAccess_Single_ReturnsItemWhenExactlyOne()
    {
        var eval = CreateEvaluator();
        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("options", TR("BlobClientOptions"), false, false, false, 1)
        ], 1);

        var expr = new MemberAccessExpr(
            new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
            "Single");
        var (result, _) = eval.EvaluateAsBool(
            new BinaryExpr(
                new MemberAccessExpr(expr, "Name"),
                "==", new LiteralExpr("options")),
            ctor, "Constructor");
        Assert.That(result, Is.True);
    }

    [Test]
    public void PropertyAccess_Single_ReturnsNullWhenMultiple()
    {
        var eval = CreateEvaluator();
        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("a", TR("string"), false, false, false, 1),
            new ParameterDeclaration("b", TR("string"), false, false, false, 2)
        ], 1);

        var expr = new MemberAccessExpr(
            new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
            "Single");
        // Single on 2 items → null → accessing .Name on null → null → != "a"
        var (result, _) = eval.EvaluateAsBool(
            new BinaryExpr(
                new MemberAccessExpr(expr, "Name"),
                "==", new LiteralExpr("a")),
            ctor, "Constructor");
        Assert.That(result, Is.False);
    }

    [Test]
    public void PropertyAccess_First_EmptyCollection_ReturnsNull()
    {
        var eval = CreateEvaluator();
        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [], 1);

        var expr = new MemberAccessExpr(
            new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
            "First");
        // First on empty → null → accessing .Name on null → null → not equal
        var (result, _) = eval.EvaluateAsBool(
            new BinaryExpr(
                new MemberAccessExpr(expr, "Name"),
                "==", new LiteralExpr("anything")),
            ctor, "Constructor");
        Assert.That(result, Is.False);
    }

    [Test]
    public void PropertyAccess_First_ChainsWithTypeMethod()
    {
        // Parameters.First.Type:endsWith("ServiceVersion")
        var eval = CreateEvaluator();
        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("version", TR("ServiceVersion"), false, false, false, 1)
        ], 1);

        var expr = new PredicateCallExpr(
            new MemberAccessExpr(
                new MemberAccessExpr(
                    new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
                    "First"),
                "Type"),
            "endsWith", [new LiteralExpr("ServiceVersion")]);
        var (result, _) = eval.EvaluateAsBool(expr, ctor, "Constructor");
        Assert.That(result, Is.True);
    }

    #endregion

    #region Predicate-filtered First(pred)/Last(pred)/Single(pred)

    [Test]
    public void CollectionMethod_FirstWithPredicate_ReturnsFirstMatch()
    {
        var isOptions = new PredicateDefinition("IsOptions", "Parameter", null,
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Parameter"), "Type"),
                "endsWith", [new LiteralExpr("Options")]), 1);

        var eval = CreateEvaluator(new Dictionary<string, List<PredicateDefinition>>
        {
            ["IsOptions"] = [isOptions]
        });

        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("endpoint", TR("string"), false, false, false, 1),
            new ParameterDeclaration("options", TR("BlobClientOptions"), false, false, false, 2),
            new ParameterDeclaration("moreOptions", TR("RetryOptions"), false, false, false, 3)
        ], 1);

        // Parameters:first(IsOptions) → should return "options" (first match)
        var expr = new MemberAccessExpr(
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
                "first", [new IdentifierExpr("IsOptions")]),
            "Name");
        var (result, _) = eval.EvaluateAsBool(
            new BinaryExpr(expr, "==", new LiteralExpr("options")),
            ctor, "Constructor");
        Assert.That(result, Is.True);
    }

    [Test]
    public void CollectionMethod_FirstWithPredicate_ReturnsNullWhenNoMatch()
    {
        var isOptions = new PredicateDefinition("IsOptions", "Parameter", null,
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Parameter"), "Type"),
                "endsWith", [new LiteralExpr("Options")]), 1);

        var eval = CreateEvaluator(new Dictionary<string, List<PredicateDefinition>>
        {
            ["IsOptions"] = [isOptions]
        });

        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("endpoint", TR("string"), false, false, false, 1)
        ], 1);

        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
            "first", [new IdentifierExpr("IsOptions")]);
        var (result, _) = eval.EvaluateAsBool(
            new BinaryExpr(
                new MemberAccessExpr(expr, "Name"),
                "==", new LiteralExpr("anything")),
            ctor, "Constructor");
        Assert.That(result, Is.False);
    }

    [Test]
    public void CollectionMethod_LastWithPredicate_ReturnsLastMatch()
    {
        var isOptions = new PredicateDefinition("IsOptions", "Parameter", null,
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Parameter"), "Type"),
                "endsWith", [new LiteralExpr("Options")]), 1);

        var eval = CreateEvaluator(new Dictionary<string, List<PredicateDefinition>>
        {
            ["IsOptions"] = [isOptions]
        });

        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("options", TR("BlobClientOptions"), false, false, false, 1),
            new ParameterDeclaration("retryOptions", TR("RetryOptions"), false, false, false, 2),
            new ParameterDeclaration("endpoint", TR("string"), false, false, false, 3)
        ], 1);

        // Parameters:last(IsOptions) → should return "retryOptions" (last match)
        var expr = new MemberAccessExpr(
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
                "last", [new IdentifierExpr("IsOptions")]),
            "Name");
        var (result, _) = eval.EvaluateAsBool(
            new BinaryExpr(expr, "==", new LiteralExpr("retryOptions")),
            ctor, "Constructor");
        Assert.That(result, Is.True);
    }

    [Test]
    public void CollectionMethod_SingleWithPredicate_ReturnsItemWhenExactlyOneMatch()
    {
        var isOptions = new PredicateDefinition("IsOptions", "Parameter", null,
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Parameter"), "Type"),
                "endsWith", [new LiteralExpr("Options")]), 1);

        var eval = CreateEvaluator(new Dictionary<string, List<PredicateDefinition>>
        {
            ["IsOptions"] = [isOptions]
        });

        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("endpoint", TR("string"), false, false, false, 1),
            new ParameterDeclaration("options", TR("BlobClientOptions"), false, false, false, 2)
        ], 1);

        // Parameters:single(IsOptions) → "options" (exactly one match)
        var expr = new MemberAccessExpr(
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
                "single", [new IdentifierExpr("IsOptions")]),
            "Name");
        var (result, _) = eval.EvaluateAsBool(
            new BinaryExpr(expr, "==", new LiteralExpr("options")),
            ctor, "Constructor");
        Assert.That(result, Is.True);
    }

    [Test]
    public void CollectionMethod_SingleWithPredicate_ReturnsNullWhenMultipleMatch()
    {
        var isOptions = new PredicateDefinition("IsOptions", "Parameter", null,
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Parameter"), "Type"),
                "endsWith", [new LiteralExpr("Options")]), 1);

        var eval = CreateEvaluator(new Dictionary<string, List<PredicateDefinition>>
        {
            ["IsOptions"] = [isOptions]
        });

        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("opts1", TR("BlobClientOptions"), false, false, false, 1),
            new ParameterDeclaration("opts2", TR("RetryOptions"), false, false, false, 2)
        ], 1);

        // Parameters:single(IsOptions) → null (two matches)
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
            "single", [new IdentifierExpr("IsOptions")]);
        var (result, _) = eval.EvaluateAsBool(
            new BinaryExpr(
                new MemberAccessExpr(expr, "Name"),
                "==", new LiteralExpr("anything")),
            ctor, "Constructor");
        Assert.That(result, Is.False);
    }

    [Test]
    public void CollectionMethod_FirstWithPredicate_CapturesContext()
    {
        var isAsync = new PredicateDefinition("IsAsync", "Method", null,
            new MemberAccessExpr(new IdentifierExpr("Method"), "Async"), 1);

        var eval = CreateEvaluator(new Dictionary<string, List<PredicateDefinition>>
        {
            ["IsAsync"] = [isAsync]
        });

        var type = MakeType("TestClient", methods: [
            new MethodDeclaration("Sync", Modifier.Public, [], TR("void"), [], 1),
            new MethodDeclaration("AsyncMethod", Modifier.Public | Modifier.Async, [], TR("Task"), [], 2)
        ]);

        // Methods:first(IsAsync) → captures AsyncMethod in context
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Methods"),
            "first", [new IdentifierExpr("IsAsync")]);
        var (_, ctx) = eval.EvaluateAsBool(
            new BinaryExpr(
                new MemberAccessExpr(expr, "Name"),
                "==", new LiteralExpr("AsyncMethod")),
            type, "Type");

        var captured = ctx.Get("Method") as MethodDeclaration;
        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.Name, Is.EqualTo("AsyncMethod"));
    }

    #endregion

    #region Inline expressions in collection methods

    [Test]
    public void InlineExpression_Any_MethodIsPublicAndAsync()
    {
        var eval = CreateEvaluator();
        var type = MakeType("TestClient", methods: [
            new MethodDeclaration("Sync", Modifier.Public, [], TR("void"), [], 1),
            new MethodDeclaration("AsyncMethod", Modifier.Public | Modifier.Async, [], TR("Task"), [], 2)
        ]);

        // Type.Methods:any(Method.Public && Method.Async) — inline expression, no named predicate
        var inlineExpr = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("Method"), "Public"),
            "&&",
            new MemberAccessExpr(new IdentifierExpr("Method"), "Async"));
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Methods"),
            "any", [inlineExpr]);
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void InlineExpression_None_NoMethodNameStartsWith()
    {
        var eval = CreateEvaluator();
        var type = MakeType("TestClient", methods: [
            new MethodDeclaration("GetBlob", Modifier.Public, [], TR("void"), [], 1),
            new MethodDeclaration("ListBlobs", Modifier.Public, [], TR("void"), [], 2)
        ]);

        // Type.Methods:none(Method.Name:startsWith("Delete")) — should be true (no Delete methods)
        var inlineExpr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Method"), "Name"),
            "startsWith", [new LiteralExpr("Delete")]);
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Methods"),
            "none", [inlineExpr]);
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void InlineExpression_All_AllMethodsPublic()
    {
        var eval = CreateEvaluator();
        var type = MakeType("TestClient", methods: [
            new MethodDeclaration("Get", Modifier.Public, [], TR("void"), [], 1),
            new MethodDeclaration("List", Modifier.Public, [], TR("void"), [], 2)
        ]);

        // Type.Methods:all(Method.Public) — all public
        var inlineExpr = new MemberAccessExpr(new IdentifierExpr("Method"), "Public");
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Methods"),
            "all", [inlineExpr]);
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void InlineExpression_Any_WithEquality()
    {
        var eval = CreateEvaluator();
        var type = MakeType("TestClient", methods: [
            new MethodDeclaration("Dispose", Modifier.Public, [], TR("void"), [], 1)
        ]);

        // Type.Methods:any(Method.Name == "Dispose")
        var inlineExpr = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("Method"), "Name"),
            "==", new LiteralExpr("Dispose"));
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Methods"),
            "any", [inlineExpr]);
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void InlineExpression_Any_WithNegation()
    {
        var eval = CreateEvaluator();
        var type = MakeType("TestClient", methods: [
            new MethodDeclaration("GetBlob", Modifier.Public, [], TR("void"), [], 1),
            new MethodDeclaration("internal_method", Modifier.None, [], TR("void"), [], 2)
        ]);

        // Type.Methods:any(!Method.Public) — at least one non-public method
        var inlineExpr = new UnaryExpr("!",
            new MemberAccessExpr(new IdentifierExpr("Method"), "Public"));
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Methods"),
            "any", [inlineExpr]);
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void InlineExpression_BaseTypeContains()
    {
        var eval = CreateEvaluator();
        var type = MakeType("BlobClient", baseTypes: ["ServiceClient", "IDisposable"]);

        // Type.BaseTypes:any(BaseType:contains("Service"))
        var inlineExpr = new PredicateCallExpr(
            new IdentifierExpr("BaseType"),
            "contains", [new LiteralExpr("Service")]);
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "BaseTypes"),
            "any", [inlineExpr]);
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    #endregion

    #region Files collection (SourceFile as item)

    [Test]
    public void FileItem_PathReturnsRelativePath()
    {
        var eval = CreateEvaluator(filePath: "src/Controllers/FooController.cs");
        // In production, ScriptInterpreter normalizes SourceFile.Path to relative before evaluation.
        // The evaluator no longer special-cases File.Path — it reads directly from the object.
        var sf = new SourceFile("src/Controllers/FooController.cs", "csharp", [], [], "")
        {
            Usings = ["System", "System.IO"],
            Namespace = "MyApp.Controllers"
        };

        // File.Path returns the path from the SourceFile object
        var expr = new MemberAccessExpr(new IdentifierExpr("File"), "Path");
        var (result, _) = eval.EvaluateAsBool(
            new PredicateCallExpr(expr, "contains", [new LiteralExpr("Controllers")]),
            sf, "File");
        Assert.That(result, Is.True);
    }

    [Test]
    public void FileItem_LanguageProperty()
    {
        var eval = CreateEvaluator();
        var sf = new SourceFile("test.cs", "csharp", [], [], "");

        var expr = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("File"), "Language"),
            "==", new LiteralExpr("csharp"));
        var (result, _) = eval.EvaluateAsBool(expr, sf, "File");
        Assert.That(result, Is.True);
    }

    [Test]
    public void FileItem_NamespaceProperty()
    {
        var eval = CreateEvaluator();
        var sf = new SourceFile("test.cs", "csharp", [], [], "")
        {
            Namespace = "MyApp.Domain"
        };

        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("File"), "Namespace"),
            "startsWith", [new LiteralExpr("MyApp")]);
        var (result, _) = eval.EvaluateAsBool(expr, sf, "File");
        Assert.That(result, Is.True);
    }

    [Test]
    public void FileItem_UsingsCollection_AnyContains()
    {
        var eval = CreateEvaluator();
        var sf = new SourceFile("test.cs", "csharp", [], [], "")
        {
            Usings = ["System", "System.IO", "Microsoft.Extensions"]
        };

        // File.Usings:any(Using:contains("System.IO"))
        var inlineExpr = new PredicateCallExpr(
            new IdentifierExpr("Using"),
            "contains", [new LiteralExpr("System.IO")]);
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("File"), "Usings"),
            "any", [inlineExpr]);
        var (result, _) = eval.EvaluateAsBool(expr, sf, "File");
        Assert.That(result, Is.True);
    }

    [Test]
    public void FileItem_UsingsCollection_NoneStartsWith()
    {
        var eval = CreateEvaluator();
        var sf = new SourceFile("test.cs", "csharp", [], [], "")
        {
            Usings = ["System", "System.Collections"]
        };

        // File.Usings:none(Using:startsWith("Microsoft"))
        var inlineExpr = new PredicateCallExpr(
            new IdentifierExpr("Using"),
            "startsWith", [new LiteralExpr("Microsoft")]);
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("File"), "Usings"),
            "none", [inlineExpr]);
        var (result, _) = eval.EvaluateAsBool(expr, sf, "File");
        Assert.That(result, Is.True);
    }

    [Test]
    public void FileItem_TypesCount()
    {
        var eval = CreateEvaluator();
        var sf = new SourceFile("test.cs", "csharp",
            [MakeType("Foo"), MakeType("Bar")], [], "");

        // File.Types.Count > 1
        var expr = new BinaryExpr(
            new MemberAccessExpr(
                new MemberAccessExpr(new IdentifierExpr("File"), "Types"),
                "Count"),
            ">", new LiteralExpr(1));
        var (result, _) = eval.EvaluateAsBool(expr, sf, "File");
        Assert.That(result, Is.True);
    }

    #endregion

    #region Negated Predicate Call (foo:!predicate)

    [Test]
    public void NegatedMethodCall_StringContains_False()
    {
        var eval = CreateEvaluator();
        var type = MakeType("BlobClient");
        // Type.Name:!endsWith("Options") → true (BlobClient does NOT end with Options)
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Name"),
            "endsWith", [new LiteralExpr("Options")], Negated: true);
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void NegatedMethodCall_StringContains_True()
    {
        var eval = CreateEvaluator();
        var type = MakeType("BlobClient");
        // Type.Name:!endsWith("Client") → false (BlobClient DOES end with Client)
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Name"),
            "endsWith", [new LiteralExpr("Client")], Negated: true);
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.False);
    }

    [Test]
    public void NegatedMethodCall_CollectionAny_EquivalentToNone()
    {
        var isOptionsType = new PredicateDefinition("IsOptionsType", "Parameter", null,
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Parameter"), "Type"),
                "endsWith", [new LiteralExpr("Options")]), 1);

        var eval = CreateEvaluator(new Dictionary<string, List<PredicateDefinition>>
        {
            ["IsOptionsType"] = [isOptionsType]
        });

        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            new ParameterDeclaration("connectionString", TR("string"), false, false, false, 1)
        ], 1);

        // Parameters:!any(IsOptionsType) — equivalent to Parameters:none(IsOptionsType)
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
            "any", [new IdentifierExpr("IsOptionsType")], Negated: true);
        var (result, _) = eval.EvaluateAsBool(expr, ctor, "Constructor");
        Assert.That(result, Is.True);
    }

    [Test]
    public void NegatedMethodCall_ParsedFromDsl()
    {
        // Verify the parser produces Negated=true for :!endsWith syntax
        var ScriptFile = ScriptParser.Parse("""
            predicate IsNotClient(Type) => Type.Name:!endsWith('Client')
            """, "test.cop");
        var pred = ScriptFile.Predicates[0];
        var body = pred.Body as PredicateCallExpr;
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Negated, Is.True);
        Assert.That(body.Name, Is.EqualTo("endsWith"));
    }

    #endregion

    #region Function Evaluation

    private static StatementInfo MakeStatement(string kind, string memberName, int line) =>
        new(kind, [], null, memberName, [], line, false);

    private static FunctionDefinition MakeFunction(
        string name, string inputType, string returnType,
        List<FunctionParameter> parameters,
        Dictionary<string, Expression> fieldMappings)
    {
        return new FunctionDefinition(name, inputType, returnType, parameters, fieldMappings, 1);
    }

    [Test]
    public void Function_ApplyFunction_ProducesAlanObject()
    {
        var func = MakeFunction("error", "Statement", "Violation",
            [new FunctionParameter("message", "string")],
            new Dictionary<string, Expression>
            {
                ["Severity"] = new LiteralExpr("error"),
                ["Message"] = new IdentifierExpr("message")
            });

        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["error"] = [func]
        };

        var eval = new PredicateEvaluator([], "test.cs", CreateTestRegistry(), functions: functions);

        var stmt = MakeStatement("var", "x", 10);
        var result = eval.ApplyFunction("error", stmt, "Statement", [new LiteralExpr("Do not use var")]);

        Assert.That(result, Is.TypeOf<ScriptObject>());
        Assert.That(result.TypeName, Is.EqualTo("Violation"));
        Assert.That(result.GetField("Severity"), Is.EqualTo("error"));
        Assert.That(result.GetField("Message"), Is.EqualTo("Do not use var"));
    }

    [Test]
    public void Function_InChain_ProducesAlanObject()
    {
        // Test function call in a chain: Statement:error("msg")
        // The evaluator should produce an ScriptObject, not a bool
        var func = MakeFunction("error", "Statement", "Violation",
            [new FunctionParameter("message", "string")],
            new Dictionary<string, Expression>
            {
                ["Severity"] = new LiteralExpr("error"),
                ["Message"] = new IdentifierExpr("message")
            });

        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["error"] = [func]
        };

        var eval = new PredicateEvaluator([], "test.cs", CreateTestRegistry(), functions: functions);

        var stmt = MakeStatement("var", "x", 10);
        // Simulate :error("Do not use var") in chain
        var expr = new PredicateCallExpr(
            new IdentifierExpr("Statement"),
            "error",
            [new LiteralExpr("Do not use var")]);

        var ctx = new EvaluationContext();
        // Use ApplyFunction directly (chain evaluation path)
        var result = eval.ApplyFunction("error", stmt, "Statement", [new LiteralExpr("Do not use var")]);
        Assert.That(result.TypeName, Is.EqualTo("Violation"));
        Assert.That(result.GetField("Message"), Is.EqualTo("Do not use var"));
    }

    [Test]
    public void Function_NegationThrows()
    {
        var func = MakeFunction("error", "Statement", "Violation",
            [new FunctionParameter("message", "string")],
            new Dictionary<string, Expression>
            {
                ["Message"] = new IdentifierExpr("message")
            });

        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["error"] = [func]
        };

        var eval = new PredicateEvaluator([], "test.cs", CreateTestRegistry(), functions: functions);

        var stmt = MakeStatement("var", "x", 10);
        var expr = new PredicateCallExpr(
            new IdentifierExpr("Statement"),
            "error",
            [new LiteralExpr("msg")],
            Negated: true);

        Assert.Throws<InvalidOperationException>(() =>
            eval.EvaluateAsBool(expr, stmt, "Statement"));
    }

    [Test]
    public void Function_StringTemplateResolution()
    {
        // Test that string templates in function args resolve against the input item
        var func = MakeFunction("error", "Statement", "Violation",
            [new FunctionParameter("message", "string")],
            new Dictionary<string, Expression>
            {
                ["Message"] = new IdentifierExpr("message")
            });

        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["error"] = [func]
        };

        var eval = new PredicateEvaluator([], "test.cs", CreateTestRegistry(), functions: functions);

        var stmt = MakeStatement("var", "myField", 10);
        var result = eval.ApplyFunction("error", stmt, "Statement",
            [new LiteralExpr("Do not use var for {Statement.MemberName}")]);

        Assert.That(result.GetField("Message"), Is.EqualTo("Do not use var for myField"));
    }

    [Test]
    public void Function_FieldMappingWithMemberAccess()
    {
        // Function body maps fields from the input item via member access
        var func = MakeFunction("error", "Statement", "Violation",
            [new FunctionParameter("message", "string")],
            new Dictionary<string, Expression>
            {
                ["Severity"] = new LiteralExpr("error"),
                ["Message"] = new IdentifierExpr("message"),
                ["Line"] = new MemberAccessExpr(new IdentifierExpr("Statement"), "Line")
            });

        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["error"] = [func]
        };

        var eval = new PredicateEvaluator([], "test.cs", CreateTestRegistry(), functions: functions);

        var stmt = MakeStatement("var", "x", 42);
        var result = eval.ApplyFunction("error", stmt, "Statement", [new LiteralExpr("msg")]);

        Assert.That(result.GetField("Line"), Is.EqualTo(42));
    }

    [Test]
    public void AlanObject_GetField_ReturnsNullForMissing()
    {
        var obj = new ScriptObject("Violation", new Dictionary<string, object?>
        {
            ["Severity"] = "error",
            ["Message"] = "test"
        });
        Assert.That(obj.GetField("Severity"), Is.EqualTo("error"));
        Assert.That(obj.GetField("NonExistent"), Is.Null);
    }

    [Test]
    public void AlanObject_GetMember_WorksInEvaluator()
    {
        // After a function produces an ScriptObject, member access should work
        var obj = new ScriptObject("Violation", new Dictionary<string, object?>
        {
            ["Severity"] = "error",
            ["Message"] = "test message"
        });

        var eval = new PredicateEvaluator([], "test.cs", CreateTestRegistry());
        var expr = new MemberAccessExpr(new IdentifierExpr("Violation"), "Severity");
        var (result, _) = eval.EvaluateAsBool(
            new BinaryExpr(expr, "==", new LiteralExpr("error")),
            obj, "Violation");
        Assert.That(result, Is.True);
    }

    #endregion
}
