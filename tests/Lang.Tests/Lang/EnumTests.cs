using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class EnumTests
{
    [Test]
    public void Parse_EnumDefinition()
    {
        var file = ScriptParser.Parse(
            "enum TypeKind = Class | Struct | Interface | Enum", "test.cop");
        Assert.That(file.EnumDefinitions, Has.Count.EqualTo(1));
        var enumDef = file.EnumDefinitions![0];
        Assert.That(enumDef.Name, Is.EqualTo("TypeKind"));
        Assert.That(enumDef.Members, Is.EqualTo(new[] { "Class", "Struct", "Interface", "Enum" }));
        Assert.That(enumDef.IsExported, Is.False);
    }

    [Test]
    public void Parse_ExportedEnumDefinition()
    {
        var file = ScriptParser.Parse(
            "export enum ApiKind = Method | Property | Event | Type", "test.cop");
        Assert.That(file.EnumDefinitions, Has.Count.EqualTo(1));
        var enumDef = file.EnumDefinitions![0];
        Assert.That(enumDef.IsExported, Is.True);
        Assert.That(enumDef.Name, Is.EqualTo("ApiKind"));
        Assert.That(enumDef.Members, Has.Count.EqualTo(4));
    }

    [Test]
    public void TypeRegistry_LoadEnumDefinitions()
    {
        var registry = new TypeRegistry();
        var enumDef = new EnumDefinition("TypeKind",
            ["Class", "Struct", "Interface"], 1);
        var errors = registry.LoadEnumDefinitions([enumDef]);
        Assert.That(errors, Is.Empty);
        Assert.That(registry.TryResolveEnumConstant("Class"), Is.EqualTo("Class"));
        Assert.That(registry.TryResolveEnumConstant("Struct"), Is.EqualTo("Struct"));
        Assert.That(registry.TryResolveEnumConstant("Interface"), Is.EqualTo("Interface"));
        Assert.That(registry.TryResolveEnumConstant("Unknown"), Is.Null);
    }

    [Test]
    public void TypeRegistry_DuplicateEnumType_ReportsError()
    {
        var registry = new TypeRegistry();
        var enumDef = new EnumDefinition("TypeKind", ["Class", "Struct"], 1);
        registry.LoadEnumDefinitions([enumDef]);
        var errors = registry.LoadEnumDefinitions([enumDef]);
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("duplicate"));
    }

    [Test]
    public void TypeRegistry_EnumMemberCollision_AllowsQualifiedAccess()
    {
        var registry = new TypeRegistry();
        var kind1 = new EnumDefinition("TypeKind", ["Class", "Interface"], 1);
        var kind2 = new EnumDefinition("ApiKind", ["Class", "Method"], 2);
        registry.LoadEnumDefinitions([kind1]);
        var errors = registry.LoadEnumDefinitions([kind2]);
        Assert.That(errors, Has.Count.EqualTo(0), "Overlapping members should not produce load errors");

        // Bare lookup should return null (ambiguous)
        Assert.That(registry.TryResolveEnumConstant("Class"), Is.Null);

        // Qualified lookup should work for both types
        Assert.That(registry.TryResolveQualifiedEnumConstant("TypeKind", "Class"), Is.EqualTo("Class"));
        Assert.That(registry.TryResolveQualifiedEnumConstant("ApiKind", "Class"), Is.EqualTo("Class"));

        // Non-ambiguous members still resolve bare
        Assert.That(registry.TryResolveEnumConstant("Interface"), Is.EqualTo("Interface"));
        Assert.That(registry.TryResolveEnumConstant("Method"), Is.EqualTo("Method"));

        // Ambiguity info available for error messages
        var owners = registry.GetEnumMemberOwners("Class");
        Assert.That(owners, Is.Not.Null);
        Assert.That(owners, Has.Count.EqualTo(2));
        Assert.That(owners, Does.Contain("TypeKind"));
        Assert.That(owners, Does.Contain("ApiKind"));
    }

    [Test]
    public void TypeRegistry_EnumFlagsCollision_ReportsError()
    {
        var registry = new TypeRegistry();
        var flagsDef = new FlagsDefinition("Modifier", ["Public", "Private"], 1);
        registry.LoadFlagsDefinitions([flagsDef]);
        var enumDef = new EnumDefinition("Visibility", ["Public", "Hidden"], 2);
        var errors = registry.LoadEnumDefinitions([enumDef]);
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("Public"));
    }

    [Test]
    public void TypeRegistry_IsEnumType()
    {
        var registry = new TypeRegistry();
        var enumDef = new EnumDefinition("TypeKind", ["Class", "Struct"], 1);
        registry.LoadEnumDefinitions([enumDef]);
        Assert.That(registry.IsEnumType("TypeKind"), Is.True);
        Assert.That(registry.IsEnumType("string"), Is.False);
    }

    [Test]
    public void Eval_EnumConstantResolvesToString()
    {
        var registry = new TypeRegistry();
        var enumDef = new EnumDefinition("TypeKind",
            ["Class", "Struct", "Interface"], 1);
        registry.LoadEnumDefinitions([enumDef]);

        // Register a type with a Kind property
        var typeDesc = new TypeDescriptor("MyType");
        typeDesc.Properties["Kind"] = new PropertyDescriptor("Kind", "string", false, false)
        {
            Accessor = obj => ((Dictionary<string, object>)obj)["Kind"]
        };
        registry.Register(typeDesc);
        registry.RegisterClrType(typeof(Dictionary<string, object>), "MyType");

        var evaluator = new PredicateEvaluator([], "test.cop", registry);
        var item = new Dictionary<string, object> { ["Kind"] = "Class" };

        // Type.Kind == Class → "Class" == "Class" → true
        var expr = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("MyType"), "Kind"),
            "==",
            new IdentifierExpr("Class"));
        var (result, _) = evaluator.EvaluateAsBool(expr, item, "MyType");
        Assert.That(result, Is.True);

        // Type.Kind == Struct → "Class" == "Struct" → false
        var expr2 = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("MyType"), "Kind"),
            "==",
            new IdentifierExpr("Struct"));
        var (result2, _) = evaluator.EvaluateAsBool(expr2, item, "MyType");
        Assert.That(result2, Is.False);
    }

    [Test]
    public void Eval_EnumComparison_CaseInsensitive()
    {
        var registry = new TypeRegistry();
        var enumDef = new EnumDefinition("TypeKind", ["Class", "Struct"], 1);
        registry.LoadEnumDefinitions([enumDef]);

        var typeDesc = new TypeDescriptor("MyType");
        typeDesc.Properties["Kind"] = new PropertyDescriptor("Kind", "string", false, false)
        {
            Accessor = obj => ((Dictionary<string, object>)obj)["Kind"]
        };
        registry.Register(typeDesc);
        registry.RegisterClrType(typeof(Dictionary<string, object>), "MyType");

        var evaluator = new PredicateEvaluator([], "test.cop", registry);
        // Provider returns "class" (lowercase) — should match enum constant "Class"
        var item = new Dictionary<string, object> { ["Kind"] = "class" };

        var expr = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("MyType"), "Kind"),
            "==",
            new IdentifierExpr("Class"));
        var (result, _) = evaluator.EvaluateAsBool(expr, item, "MyType");
        Assert.That(result, Is.True);
    }

    [Test]
    public void Eval_EnumWithLowercaseMembers()
    {
        var registry = new TypeRegistry();
        var enumDef = new EnumDefinition("StatementKind",
            ["call", "declaration", "import"], 1);
        registry.LoadEnumDefinitions([enumDef]);

        var typeDesc = new TypeDescriptor("Statement");
        typeDesc.Properties["Kind"] = new PropertyDescriptor("Kind", "string", false, false)
        {
            Accessor = obj => ((Dictionary<string, object>)obj)["Kind"]
        };
        registry.Register(typeDesc);
        registry.RegisterClrType(typeof(Dictionary<string, object>), "Statement");

        var evaluator = new PredicateEvaluator([], "test.cop", registry);
        var item = new Dictionary<string, object> { ["Kind"] = "call" };

        // Statement.Kind == call → "call" == "call" → true
        var expr = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("Statement"), "Kind"),
            "==",
            new IdentifierExpr("call"));
        var (result, _) = evaluator.EvaluateAsBool(expr, item, "Statement");
        Assert.That(result, Is.True);

        // Statement.Kind == declaration → "call" == "declaration" → false
        var expr2 = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("Statement"), "Kind"),
            "==",
            new IdentifierExpr("declaration"));
        var (result2, _) = evaluator.EvaluateAsBool(expr2, item, "Statement");
        Assert.That(result2, Is.False);
    }

    [Test]
    public void Eval_ExtensibleEnum_UnknownValueStillWorks()
    {
        var registry = new TypeRegistry();
        var enumDef = new EnumDefinition("TypeKind", ["Class", "Struct"], 1);
        registry.LoadEnumDefinitions([enumDef]);

        var typeDesc = new TypeDescriptor("MyType");
        typeDesc.Properties["Kind"] = new PropertyDescriptor("Kind", "string", false, false)
        {
            Accessor = obj => ((Dictionary<string, object>)obj)["Kind"]
        };
        registry.Register(typeDesc);
        registry.RegisterClrType(typeof(Dictionary<string, object>), "MyType");

        var evaluator = new PredicateEvaluator([], "test.cop", registry);
        // Provider returns "Record" — not in the enum, but it's extensible
        var item = new Dictionary<string, object> { ["Kind"] = "Record" };

        // String comparison with literal still works
        var expr = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("MyType"), "Kind"),
            "==",
            new LiteralExpr("Record"));
        var (result, _) = evaluator.EvaluateAsBool(expr, item, "MyType");
        Assert.That(result, Is.True);

        // Doesn't match known enum values
        var expr2 = new BinaryExpr(
            new MemberAccessExpr(new IdentifierExpr("MyType"), "Kind"),
            "==",
            new IdentifierExpr("Class"));
        var (result2, _) = evaluator.EvaluateAsBool(expr2, item, "MyType");
        Assert.That(result2, Is.False);
    }

    [Test]
    public void Parse_EnumWithStringLiteralMembers()
    {
        var file = ScriptParser.Parse(
            "enum ContentType = 'application/json' | 'text/plain' | 'text/html'", "test.cop");
        Assert.That(file.EnumDefinitions, Has.Count.EqualTo(1));
        var enumDef = file.EnumDefinitions![0];
        Assert.That(enumDef.Name, Is.EqualTo("ContentType"));
        Assert.That(enumDef.Members, Is.EqualTo(new[] { "application/json", "text/plain", "text/html" }));
    }

    [Test]
    public void TypeRegistry_StringLiteralEnumMembers_Resolve()
    {
        var registry = new TypeRegistry();
        var enumDef = new EnumDefinition("ContentType",
            ["application/json", "text/plain", "text/html"], 1);
        var errors = registry.LoadEnumDefinitions([enumDef]);
        Assert.That(errors, Is.Empty);
        Assert.That(registry.TryResolveEnumConstant("application/json"), Is.EqualTo("application/json"));
        Assert.That(registry.IsEnumType("ContentType"), Is.True);
        Assert.That(registry.GetEnumType("ContentType")!.Members, Has.Count.EqualTo(3));
    }
}
