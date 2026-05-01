using System.Collections;
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
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);

        // Load flags definitions and isX predicates from code.cop
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        var allPredicates = new Dictionary<string, List<PredicateDefinition>>();
        foreach (var pred in codeFile.Predicates)
        {
            if (!allPredicates.TryGetValue(pred.Name, out var group))
            {
                group = [];
                allPredicates[pred.Name] = group;
            }
            group.Add(pred);
        }
        if (predicates != null)
        {
            foreach (var (name, group) in predicates)
                allPredicates[name] = group;
        }

        return new PredicateEvaluator(allPredicates, filePath, registry);
    }

    private static TypeReference TR(string name) => new(name, null, [], name);

    private static TypeRegistry CreateTestRegistry()
    {
        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);
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
            new PredicateCallExpr(new IdentifierExpr("Type"), "isPublic", []),
            "&&",
            new PredicateCallExpr(new IdentifierExpr("Type"), "isSealed", []));
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void BooleanOperators_Not()
    {
        var eval = CreateEvaluator();
        var type = MakeType("BlobClient");
        var expr = new UnaryExpr("!",
            new PredicateCallExpr(new IdentifierExpr("Type"), "isSealed", []));
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
            new PredicateCallExpr(new IdentifierExpr("Type"), "isSealed", []), 1);
        var unsealedPred = new PredicateDefinition("unsealed", "Type", null,
            new PredicateCallExpr(new IdentifierExpr("Type"), "isSealed", [], Negated: true), 1);

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
                new PredicateCallExpr(new IdentifierExpr("Method"), "isPublic", []),
                "&&",
                new PredicateCallExpr(new IdentifierExpr("Method"), "isAsync", [])),
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
        // Parameters.First.Type:ew("ServiceVersion")
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

        // Parameters.First(IsOptions) → should return "options" (first match)
        var expr = new MemberAccessExpr(
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
                "First", [new IdentifierExpr("IsOptions")]),
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
            "First", [new IdentifierExpr("IsOptions")]);
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

        // Parameters.Last(IsOptions) → should return "retryOptions" (last match)
        var expr = new MemberAccessExpr(
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
                "Last", [new IdentifierExpr("IsOptions")]),
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

        // Parameters.Single(IsOptions) → "options" (exactly one match)
        var expr = new MemberAccessExpr(
            new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
                "Single", [new IdentifierExpr("IsOptions")]),
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

        // Parameters.Single(IsOptions) → null (two matches)
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Constructor"), "Parameters"),
            "Single", [new IdentifierExpr("IsOptions")]);
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
            new PredicateCallExpr(new IdentifierExpr("Method"), "isAsync", []), 1);

        var eval = CreateEvaluator(new Dictionary<string, List<PredicateDefinition>>
        {
            ["IsAsync"] = [isAsync]
        });

        var type = MakeType("TestClient", methods: [
            new MethodDeclaration("Sync", Modifier.Public, [], TR("void"), [], 1),
            new MethodDeclaration("AsyncMethod", Modifier.Public | Modifier.Async, [], TR("Task"), [], 2)
        ]);

        // Methods.First(IsAsync) → captures AsyncMethod in context
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Methods"),
            "First", [new IdentifierExpr("IsAsync")]);
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

        // Type.Methods:any(item:isPublic && item:isAsync) — inline expression with item keyword
        var inlineExpr = new BinaryExpr(
            new PredicateCallExpr(new IdentifierExpr("item"), "isPublic", []),
            "&&",
            new PredicateCallExpr(new IdentifierExpr("item"), "isAsync", []));
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

        // Type.Methods:none(item.Name:sw("Delete")) — should be true (no Delete methods)
        var inlineExpr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("item"), "Name"),
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

        // Type.Methods:all(item:isPublic) — all public
        var inlineExpr = new PredicateCallExpr(new IdentifierExpr("item"), "isPublic", []);
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

        // Type.Methods:any(item.Name == "Dispose")
        var inlineExpr = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("item"), "Name"),
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

        // Type.Methods:any(item:!isPublic) — at least one non-public method
        var inlineExpr = new PredicateCallExpr(new IdentifierExpr("item"), "isPublic", [], Negated: true);
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

        // Type.BaseTypes:any(item:ct("Service"))
        var inlineExpr = new PredicateCallExpr(
            new IdentifierExpr("item"),
            "contains", [new LiteralExpr("Service")]);
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "BaseTypes"),
            "any", [inlineExpr]);
        var (result, _) = eval.EvaluateAsBool(expr, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void CollectionOp_OrderBy_SortsByName()
    {
        var eval = CreateEvaluator();
        var type = MakeType("TestClient", methods: [
            new MethodDeclaration("Zebra", Modifier.Public, [], TR("void"), [], 1),
            new MethodDeclaration("Alpha", Modifier.Public, [], TR("void"), [], 2),
            new MethodDeclaration("Middle", Modifier.Public, [], TR("void"), [], 3)
        ]);

        // Type.Methods.OrderBy(item.Name) — sort by name ascending
        var fieldExpr = new MemberAccessExpr(new IdentifierExpr("item"), "Name");
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Methods"),
            "OrderBy", [fieldExpr]);
        var result = eval.EvaluateField(expr, type, "Type") as IList;
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Count, Is.EqualTo(3));
        Assert.That(((dynamic)result[0]!).Name, Is.EqualTo("Alpha"));
        Assert.That(((dynamic)result[2]!).Name, Is.EqualTo("Zebra"));
    }

    [Test]
    public void CollectionOp_OrderByDescending_SortsByNameDesc()
    {
        var eval = CreateEvaluator();
        var type = MakeType("TestClient", methods: [
            new MethodDeclaration("Alpha", Modifier.Public, [], TR("void"), [], 1),
            new MethodDeclaration("Zebra", Modifier.Public, [], TR("void"), [], 2)
        ]);

        var fieldExpr = new MemberAccessExpr(new IdentifierExpr("item"), "Name");
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Methods"),
            "OrderByDescending", [fieldExpr]);
        var result = eval.EvaluateField(expr, type, "Type") as IList;
        Assert.That(result, Is.Not.Null);
        Assert.That(((dynamic)result![0]!).Name, Is.EqualTo("Zebra"));
        Assert.That(((dynamic)result[1]!).Name, Is.EqualTo("Alpha"));
    }

    [Test]
    public void CollectionOp_Select_PreservesTypedValues()
    {
        var eval = CreateEvaluator();
        var type = MakeType("TestClient", methods: [
            new MethodDeclaration("A", Modifier.Public, [], TR("void"), [], 1),
            new MethodDeclaration("BB", Modifier.Public, [], TR("void"), [], 2),
            new MethodDeclaration("CCC", Modifier.Public, [], TR("void"), [], 3)
        ]);

        // Type.Methods.Select(item.Name) — returns actual string values, not ToString()
        var fieldExpr = new MemberAccessExpr(new IdentifierExpr("item"), "Name");
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Methods"),
            "Select", [fieldExpr]);
        var result = eval.EvaluateField(expr, type, "Type") as IList;
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Count, Is.EqualTo(3));
        Assert.That(result[0], Is.EqualTo("A"));
        Assert.That(result[1], Is.EqualTo("BB"));
        Assert.That(result[2], Is.EqualTo("CCC"));
    }

    [Test]
    public void CollectionOp_Distinct_DeduplicatesByExpression()
    {
        var eval = CreateEvaluator();
        var type = MakeType("TestClient", methods: [
            new MethodDeclaration("GetA", Modifier.Public, [], TR("void"), [], 1),
            new MethodDeclaration("GetB", Modifier.Public, [], TR("void"), [], 2),
            new MethodDeclaration("GetA", Modifier.Public, [], TR("void"), [], 3)
        ]);

        // Type.Methods.Distinct(item.Name) — deduplicate by name
        var fieldExpr = new MemberAccessExpr(new IdentifierExpr("item"), "Name");
        var expr = new PredicateCallExpr(
            new MemberAccessExpr(new IdentifierExpr("Type"), "Methods"),
            "Distinct", [fieldExpr]);
        var result = eval.EvaluateField(expr, type, "Type") as IList;
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Count, Is.EqualTo(2)); // GetA deduplicated
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

        // File.Usings:any(item:ct("System.IO"))
        var inlineExpr = new PredicateCallExpr(
            new IdentifierExpr("item"),
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

        // File.Usings:none(item:sw("Microsoft"))
        var inlineExpr = new PredicateCallExpr(
            new IdentifierExpr("item"),
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
        // Type.Name:!ew("Options") → true (BlobClient does NOT end with Options)
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
        // Type.Name:!ew("Client") → false (BlobClient DOES end with Client)
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
        // Verify the parser produces Negated=true for :!ew syntax
        var ScriptFile = ScriptParser.Parse("""
            predicate IsNotClient(Type) => Type.Name:!ew('Client')
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

        Assert.That(result, Is.TypeOf<DataObject>());
        Assert.That(((DataObject)result!).TypeName, Is.EqualTo("Violation"));
        Assert.That(((DataObject)result!).GetField("Severity"), Is.EqualTo("error"));
        Assert.That(((DataObject)result!).GetField("Message"), Is.EqualTo("Do not use var"));
    }

    [Test]
    public void Function_InChain_ProducesAlanObject()
    {
        // Test function call in a chain: Statement:error("msg")
        // The evaluator should produce an DataObject, not a bool
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
        Assert.That(((DataObject)result!).TypeName, Is.EqualTo("Violation"));
        Assert.That(((DataObject)result!).GetField("Message"), Is.EqualTo("Do not use var"));
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
            [new LiteralExpr("Do not use var for {item.MemberName}")]);

        Assert.That(((DataObject)result!).GetField("Message"), Is.EqualTo("Do not use var for myField"));
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

        Assert.That(((DataObject)result!).GetField("Line"), Is.EqualTo(42));
    }

    [Test]
    public void AlanObject_GetField_ReturnsNullForMissing()
    {
        var obj = new DataObject("Violation", new Dictionary<string, object?>
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
        // After a function produces an DataObject, member access should work
        var obj = new DataObject("Violation", new Dictionary<string, object?>
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

    #region Map/Dictionary Tests

    [Test]
    public void Map_StringLiteralKeys_Parse()
    {
        // Parser should accept string-literal keys in object literal
        var file = ScriptParser.Parse(
            "let colors = { 'error': 'red', 'warning': 'yellow', 'info': 'white' }\n" +
            "predicate test(Type) => true\n", "test.cop");
        Assert.That(file.LetDeclarations, Has.Count.EqualTo(1));
        Assert.That(file.LetDeclarations[0].IsValueBinding, Is.True);
    }

    [Test]
    public void Map_Get_DynamicLookup()
    {
        var eval = CreateEvaluator();
        // Create a map: { error: 'red', warning: 'yellow' }
        var mapExpr = new ObjectLiteralExpr(null, new Dictionary<string, Expression>
        {
            ["error"] = new LiteralExpr("red"),
            ["warning"] = new LiteralExpr("yellow")
        });
        var map = eval.EvaluateField(mapExpr, MakeType("Dummy"), "Type");
        Assert.That(map, Is.TypeOf<DataObject>());

        // Get with a literal key
        var getExpr = new PredicateCallExpr(mapExpr, "Get", [new LiteralExpr("error")]);
        var result = eval.EvaluateField(getExpr, MakeType("Dummy"), "Type");
        Assert.That(result, Is.EqualTo("red"));
    }

    [Test]
    public void Map_Get_CaseInsensitive()
    {
        var eval = CreateEvaluator();
        var mapExpr = new ObjectLiteralExpr(null, new Dictionary<string, Expression>
        {
            ["Error"] = new LiteralExpr("red")
        });
        // DataObject field lookup is case-insensitive by default
        var getExpr = new PredicateCallExpr(mapExpr, "Get", [new LiteralExpr("error")]);
        var result = eval.EvaluateField(getExpr, MakeType("Dummy"), "Type");
        Assert.That(result, Is.EqualTo("red"));
    }

    [Test]
    public void Map_ContainsKey_TrueAndFalse()
    {
        var eval = CreateEvaluator();
        var mapExpr = new ObjectLiteralExpr(null, new Dictionary<string, Expression>
        {
            ["error"] = new LiteralExpr("red")
        });
        var containsTrue = new PredicateCallExpr(mapExpr, "containsKey", [new LiteralExpr("error")]);
        var containsFalse = new PredicateCallExpr(mapExpr, "containsKey", [new LiteralExpr("missing")]);

        Assert.That(eval.EvaluateField(containsTrue, MakeType("Dummy"), "Type"), Is.EqualTo(true));
        Assert.That(eval.EvaluateField(containsFalse, MakeType("Dummy"), "Type"), Is.EqualTo(false));
    }

    [Test]
    public void Map_Keys_ReturnsKeyList()
    {
        var eval = CreateEvaluator();
        var mapExpr = new ObjectLiteralExpr(null, new Dictionary<string, Expression>
        {
            ["a"] = new LiteralExpr(1),
            ["b"] = new LiteralExpr(2)
        });
        var keysExpr = new MemberAccessExpr(mapExpr, "Keys");
        var result = eval.EvaluateField(keysExpr, MakeType("Dummy"), "Type") as IList;
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain("a"));
        Assert.That(result, Does.Contain("b"));
    }

    [Test]
    public void Map_Values_ReturnsValueList()
    {
        var eval = CreateEvaluator();
        var mapExpr = new ObjectLiteralExpr(null, new Dictionary<string, Expression>
        {
            ["a"] = new LiteralExpr("x"),
            ["b"] = new LiteralExpr("y")
        });
        var valuesExpr = new MemberAccessExpr(mapExpr, "Values");
        var result = eval.EvaluateField(valuesExpr, MakeType("Dummy"), "Type") as IList;
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain("x"));
        Assert.That(result, Does.Contain("y"));
    }

    [Test]
    public void Map_Count_Property()
    {
        var eval = CreateEvaluator();
        var mapExpr = new ObjectLiteralExpr(null, new Dictionary<string, Expression>
        {
            ["a"] = new LiteralExpr(1),
            ["b"] = new LiteralExpr(2),
            ["c"] = new LiteralExpr(3)
        });
        var countExpr = new MemberAccessExpr(mapExpr, "Count");
        var result = eval.EvaluateField(countExpr, MakeType("Dummy"), "Type");
        Assert.That(result, Is.EqualTo(3));
    }

    #endregion

    #region Function Constraint Resolution

    [Test]
    public void ResolveFunction_ConstrainedOverload_MatchesFirst()
    {
        var registry = new TypeRegistry();

        // Two overloads: one constrained to Path:eq('/'), one unconstrained
        // Constraint: PredicateCallExpr(MemberAccessExpr(IdentifierExpr("item"), "Path"), "equals", ["/"])
        var constrained = new FunctionDefinition(
            "handle", "Request", "Response", [],
            new() { ["StatusCode"] = new LiteralExpr(200) }, 1,
            Constraint: new PredicateCallExpr(
                new MemberAccessExpr(new IdentifierExpr("item"), "Path"),
                "equals", [new LiteralExpr("/")], false));

        var fallback = new FunctionDefinition(
            "handle", "Request", "Response", [],
            new() { ["StatusCode"] = new LiteralExpr(404) }, 2);

        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["handle"] = [constrained, fallback]
        };

        var eval = new PredicateEvaluator(
            new Dictionary<string, List<PredicateDefinition>>(),
            "test.cop",
            registry,
            functions: functions);

        // Item with Path = "/" → should match constrained overload
        var matchItem = new DataObject("Request", new() { ["Path"] = "/", ["Method"] = "GET" });
        var result = eval.ApplyFunction("handle", matchItem, "Request", []);
        Assert.That(result, Is.TypeOf<DataObject>());
        Assert.That(((DataObject)result!).GetField("StatusCode"), Is.EqualTo(200));

        // Item with Path = "/other" → should fall through to unconstrained
        var noMatchItem = new DataObject("Request", new() { ["Path"] = "/other", ["Method"] = "GET" });
        var result2 = eval.ApplyFunction("handle", noMatchItem, "Request", []);
        Assert.That(result2, Is.TypeOf<DataObject>());
        Assert.That(((DataObject)result2!).GetField("StatusCode"), Is.EqualTo(404));
    }

    #endregion
}
