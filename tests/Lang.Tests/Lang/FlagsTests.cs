using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class FlagsTests
{
    [Test]
    public void Parse_FlagsDefinition()
    {
        var file = ScriptParser.Parse(
            "flags Visibility = Public | Protected | Private | Internal", "test.cop");
        Assert.That(file.FlagsDefinitions, Has.Count.EqualTo(1));
        var flags = file.FlagsDefinitions![0];
        Assert.That(flags.Name, Is.EqualTo("Visibility"));
        Assert.That(flags.Members, Is.EqualTo(new[] { "Public", "Protected", "Private", "Internal" }));
    }

    [Test]
    public void Parse_ExportedFlagsDefinition()
    {
        var file = ScriptParser.Parse(
            "export flags Modifier = Sealed | Abstract | Static | Async", "test.cop");
        Assert.That(file.FlagsDefinitions, Has.Count.EqualTo(1));
        var flags = file.FlagsDefinitions![0];
        Assert.That(flags.IsExported, Is.True);
        Assert.That(flags.Members, Has.Count.EqualTo(4));
    }

    [Test]
    public void Parse_BitwiseAndExpression()
    {
        var file = ScriptParser.Parse(
            "predicate isPublic(Type) => Type.Visibility & Public != 0", "test.cop");
        var body = file.Predicates[0].Body;
        // Should parse as (Type.Visibility & Public) != 0
        Assert.That(body, Is.TypeOf<BinaryExpr>());
        var outer = (BinaryExpr)body;
        Assert.That(outer.Operator, Is.EqualTo("!="));
        Assert.That(outer.Left, Is.TypeOf<BinaryExpr>());
        var inner = (BinaryExpr)outer.Left;
        Assert.That(inner.Operator, Is.EqualTo("&"));
    }

    [Test]
    public void Parse_BitwiseOrExpression()
    {
        var file = ScriptParser.Parse(
            "predicate hasAccess(Type) => Type.Visibility & (Public | Internal) != 0", "test.cop");
        var body = file.Predicates[0].Body;
        Assert.That(body, Is.TypeOf<BinaryExpr>());
        var outer = (BinaryExpr)body;
        Assert.That(outer.Operator, Is.EqualTo("!="));
        // Left side: Type.Visibility & (Public | Internal)
        var andExpr = (BinaryExpr)outer.Left;
        Assert.That(andExpr.Operator, Is.EqualTo("&"));
        // Right side of &: (Public | Internal)
        Assert.That(andExpr.Right, Is.TypeOf<BinaryExpr>());
        var orExpr = (BinaryExpr)andExpr.Right;
        Assert.That(orExpr.Operator, Is.EqualTo("|"));
    }

    [Test]
    public void Parse_FlagsEqualityComparison()
    {
        var file = ScriptParser.Parse(
            "predicate isPublic(Type) => Type.Visibility == Public", "test.cop");
        var body = file.Predicates[0].Body;
        Assert.That(body, Is.TypeOf<BinaryExpr>());
        var bin = (BinaryExpr)body;
        Assert.That(bin.Operator, Is.EqualTo("=="));
        Assert.That(bin.Right, Is.TypeOf<IdentifierExpr>());
        Assert.That(((IdentifierExpr)bin.Right).Name, Is.EqualTo("Public"));
    }

    [Test]
    public void TypeRegistry_LoadFlagsDefinitions()
    {
        var registry = new TypeRegistry();
        var flagsDef = new FlagsDefinition("Visibility",
            ["Public", "Protected", "Private", "Internal"], 1);
        var errors = registry.LoadFlagsDefinitions([flagsDef]);
        Assert.That(errors, Is.Empty);
        Assert.That(registry.TryResolveFlagsConstant("Public"), Is.EqualTo(1));
        Assert.That(registry.TryResolveFlagsConstant("Protected"), Is.EqualTo(2));
        Assert.That(registry.TryResolveFlagsConstant("Private"), Is.EqualTo(4));
        Assert.That(registry.TryResolveFlagsConstant("Internal"), Is.EqualTo(8));
    }

    [Test]
    public void TypeRegistry_DuplicateFlagsType_ReportsError()
    {
        var registry = new TypeRegistry();
        var flagsDef = new FlagsDefinition("Visibility",
            ["Public", "Protected"], 1);
        registry.LoadFlagsDefinitions([flagsDef]);
        var errors = registry.LoadFlagsDefinitions([flagsDef]);
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("duplicate"));
    }

    [Test]
    public void TypeRegistry_FlagsMemberCollision_ReportsError()
    {
        var registry = new TypeRegistry();
        var vis = new FlagsDefinition("Visibility", ["Public", "Private"], 1);
        var acc = new FlagsDefinition("Access", ["Public", "Restricted"], 2);
        registry.LoadFlagsDefinitions([vis]);
        var errors = registry.LoadFlagsDefinitions([acc]);
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("Public"));
    }

    [Test]
    public void TypeRegistry_IsFlagsType()
    {
        var registry = new TypeRegistry();
        var flagsDef = new FlagsDefinition("Visibility",
            ["Public", "Protected"], 1);
        registry.LoadFlagsDefinitions([flagsDef]);
        Assert.That(registry.IsFlagsType("Visibility"), Is.True);
        Assert.That(registry.IsFlagsType("string"), Is.False);
    }

    [Test]
    public void Eval_BitwiseAndOnFlagsValue()
    {
        var registry = new TypeRegistry();
        var flagsDef = new FlagsDefinition("Visibility",
            ["Public", "Protected", "Private", "Internal"], 1);
        registry.LoadFlagsDefinitions([flagsDef]);

        // Register a dummy type with a Visibility property returning int
        var typeDesc = new TypeDescriptor("MyType");
        typeDesc.Properties["Visibility"] = new PropertyDescriptor(
            "Visibility", "int", false, false)
        {
            Accessor = obj => ((Dictionary<string, object>)obj)["Visibility"]
        };
        registry.Register(typeDesc);
        registry.RegisterClrType(typeof(Dictionary<string, object>), "MyType");

        var evaluator = new PredicateEvaluator([], "test.cop", registry);
        var item = new Dictionary<string, object> { ["Visibility"] = 1 }; // Public

        // Type.Visibility & Public != 0
        var expr = new BinaryExpr(
            new BinaryExpr(
                new MemberAccessExpr(new IdentifierExpr("MyType"), "Visibility"),
                "&",
                new IdentifierExpr("Public")),
            "!=",
            new LiteralExpr(0));
        var (result, _) = evaluator.EvaluateAsBool(expr, item, "MyType");
        Assert.That(result, Is.True);
    }

    [Test]
    public void Eval_BitwiseAndOnFlagsValue_NoMatch()
    {
        var registry = new TypeRegistry();
        var flagsDef = new FlagsDefinition("Visibility",
            ["Public", "Protected", "Private", "Internal"], 1);
        registry.LoadFlagsDefinitions([flagsDef]);

        var typeDesc = new TypeDescriptor("MyType");
        typeDesc.Properties["Visibility"] = new PropertyDescriptor(
            "Visibility", "int", false, false)
        {
            Accessor = obj => ((Dictionary<string, object>)obj)["Visibility"]
        };
        registry.Register(typeDesc);
        registry.RegisterClrType(typeof(Dictionary<string, object>), "MyType");

        var evaluator = new PredicateEvaluator([], "test.cop", registry);
        var item = new Dictionary<string, object> { ["Visibility"] = 4 }; // Private

        // Type.Visibility & Public != 0 → should be false
        var expr = new BinaryExpr(
            new BinaryExpr(
                new MemberAccessExpr(new IdentifierExpr("MyType"), "Visibility"),
                "&",
                new IdentifierExpr("Public")),
            "!=",
            new LiteralExpr(0));
        var (result, _) = evaluator.EvaluateAsBool(expr, item, "MyType");
        Assert.That(result, Is.False);
    }

    [Test]
    public void Eval_BitwiseOrCombinesFlags()
    {
        var registry = new TypeRegistry();
        var flagsDef = new FlagsDefinition("Visibility",
            ["Public", "Protected", "Private", "Internal"], 1);
        registry.LoadFlagsDefinitions([flagsDef]);

        var typeDesc = new TypeDescriptor("MyType");
        typeDesc.Properties["Visibility"] = new PropertyDescriptor(
            "Visibility", "int", false, false)
        {
            Accessor = obj => ((Dictionary<string, object>)obj)["Visibility"]
        };
        registry.Register(typeDesc);
        registry.RegisterClrType(typeof(Dictionary<string, object>), "MyType");

        var evaluator = new PredicateEvaluator([], "test.cop", registry);
        // Protected | Internal = 2 | 8 = 10
        var item = new Dictionary<string, object> { ["Visibility"] = 10 };

        // Type.Visibility & (Protected | Internal) != 0
        var expr = new BinaryExpr(
            new BinaryExpr(
                new MemberAccessExpr(new IdentifierExpr("MyType"), "Visibility"),
                "&",
                new BinaryExpr(
                    new IdentifierExpr("Protected"),
                    "|",
                    new IdentifierExpr("Internal"))),
            "!=",
            new LiteralExpr(0));
        var (result, _) = evaluator.EvaluateAsBool(expr, item, "MyType");
        Assert.That(result, Is.True);
    }

    [Test]
    public void Eval_FlagsExactEquality()
    {
        var registry = new TypeRegistry();
        var flagsDef = new FlagsDefinition("Visibility",
            ["Public", "Protected", "Private", "Internal"], 1);
        registry.LoadFlagsDefinitions([flagsDef]);

        var typeDesc = new TypeDescriptor("MyType");
        typeDesc.Properties["Visibility"] = new PropertyDescriptor(
            "Visibility", "int", false, false)
        {
            Accessor = obj => ((Dictionary<string, object>)obj)["Visibility"]
        };
        registry.Register(typeDesc);
        registry.RegisterClrType(typeof(Dictionary<string, object>), "MyType");

        var evaluator = new PredicateEvaluator([], "test.cop", registry);
        var item = new Dictionary<string, object> { ["Visibility"] = 1 }; // Public

        // Type.Visibility == Public
        var expr = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("MyType"), "Visibility"),
            "==",
            new IdentifierExpr("Public"));
        var (result, _) = evaluator.EvaluateAsBool(expr, item, "MyType");
        Assert.That(result, Is.True);
    }

    [Test]
    public void Parse_FlagsWithTypeAndPredicates()
    {
        var source = """
            flags Visibility = Public | Protected | Private | Internal
            
            type MyType = {
                Name : string,
                Visibility : int
            }
            
            predicate isPublic(MyType) => MyType.Visibility & Public != 0
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.FlagsDefinitions, Has.Count.EqualTo(1));
        Assert.That(file.TypeDefinitions, Has.Count.EqualTo(1));
        Assert.That(file.Predicates, Has.Count.EqualTo(1));
    }

    [Test]
    public void Eval_IsSealedPredicate_OnRealTypeDeclaration()
    {
        var registry = new TypeRegistry();
        Cop.Providers.ProviderLoader.RegisterSchema(new Cop.Providers.CodeSchemaProvider(), registry);
        var codeFile = TestInterpreter.CodePackage;
        if (codeFile.FlagsDefinitions != null)
            registry.LoadFlagsDefinitions(codeFile.FlagsDefinitions);

        Assert.That(registry.TryResolveFlagsConstant("Sealed"), Is.EqualTo(32));

        var predicateGroups = new Dictionary<string, List<PredicateDefinition>>();
        foreach (var pred in codeFile.Predicates)
        {
            if (!predicateGroups.TryGetValue(pred.Name, out var group))
            {
                group = [];
                predicateGroups[pred.Name] = group;
            }
            group.Add(pred);
        }

        // Create a sealed TypeDeclaration with a File (triggers the language filter path)
        var file = new Cop.Providers.SourceModel.SourceFile("test.cs", "csharp", [], [], "");
        var sealedType = new Cop.Providers.SourceModel.TypeDeclaration(
            "GoodClient",
            Cop.Providers.SourceModel.TypeKind.Class,
            Cop.Providers.SourceModel.Modifier.Public | Cop.Providers.SourceModel.Modifier.Sealed,
            [], [], [], [], [], [], 1) with { File = file };

        var evaluator = new PredicateEvaluator(predicateGroups, "test.cs", registry);

        // isSealed body should be true (regression: flags constants must resolve before language filter)
        var isSealedPred = predicateGroups["isSealed"][0];
        var (bodyResult, _) = evaluator.EvaluateAsBool(isSealedPred.Body, sealedType, "Type");
        Assert.That(bodyResult, Is.True, "isSealed body should be true for sealed type with File");

        // Type:isSealed via PredicateCallExpr
        var callExpr = new PredicateCallExpr(new IdentifierExpr("Type"), "isSealed", []);
        var (callResult, _) = evaluator.EvaluateAsBool(callExpr, sealedType, "Type");
        Assert.That(callResult, Is.True, "Type:isSealed should be true for sealed type with File");
    }
}
