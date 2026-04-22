using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class TypeRegistryTests
{
    [Test]
    public void CorePrimitives_Registered()
    {
        var registry = new TypeRegistry();
        Assert.That(registry.HasType("Object"), Is.True);
        Assert.That(registry.HasType("string"), Is.True);
        Assert.That(registry.HasType("int"), Is.True);
        Assert.That(registry.HasType("bool"), Is.True);
        Assert.That(registry.HasType("byte"), Is.True);
    }

    [Test]
    public void LoadTypeDefinitions_SimpleRecord()
    {
        var registry = new TypeRegistry();
        var typeDefs = new[]
        {
            new TypeDefinition("Foo", null, [
                new PropertyDefinition("Name", "string", false, false, 1),
                new PropertyDefinition("Age", "int", false, false, 1)
            ], 1)
        };
        var errors = registry.LoadTypeDefinitions(typeDefs);
        Assert.That(errors, Is.Empty);
        Assert.That(registry.HasType("Foo"), Is.True);
        var desc = registry.GetType("Foo")!;
        Assert.That(desc.Properties, Has.Count.EqualTo(2));
        Assert.That(desc.GetProperty("Name"), Is.Not.Null);
        Assert.That(desc.GetProperty("Name")!.TypeName, Is.EqualTo("string"));
    }

    [Test]
    public void LoadTypeDefinitions_SubtypeInheritsBaseProperties()
    {
        var registry = new TypeRegistry();
        var typeDefs = new[]
        {
            new TypeDefinition("Base", null, [
                new PropertyDefinition("Name", "string", false, false, 1)
            ], 1),
            new TypeDefinition("Child", "Base", [
                new PropertyDefinition("Extra", "int", false, false, 2)
            ], 2)
        };
        var errors = registry.LoadTypeDefinitions(typeDefs);
        Assert.That(errors, Is.Empty);

        var child = registry.GetType("Child")!;
        // Child has its own property
        Assert.That(child.GetProperty("Extra"), Is.Not.Null);
        // Child inherits Base's property
        Assert.That(child.GetProperty("Name"), Is.Not.Null);
        // All properties includes both
        Assert.That(child.GetAllProperties().Count(), Is.EqualTo(2));
    }

    [Test]
    public void LoadTypeDefinitions_DuplicatePropertyInSubtype_ReturnsError()
    {
        var registry = new TypeRegistry();
        var typeDefs = new[]
        {
            new TypeDefinition("Base", null, [
                new PropertyDefinition("Name", "string", false, false, 1)
            ], 1),
            new TypeDefinition("Child", "Base", [
                new PropertyDefinition("Name", "int", false, false, 2)
            ], 2)
        };
        var errors = registry.LoadTypeDefinitions(typeDefs);
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("Name").And.Contain("already defined"));
    }

    [Test]
    public void LoadTypeDefinitions_InheritanceCycle_ReturnsError()
    {
        var registry = new TypeRegistry();
        var typeDefs = new[]
        {
            new TypeDefinition("A", "B", [], 1),
            new TypeDefinition("B", "A", [], 2)
        };
        var errors = registry.LoadTypeDefinitions(typeDefs);
        Assert.That(errors, Has.Count.GreaterThan(0));
        Assert.That(errors.Any(e => e.Contains("cycle")), Is.True);
    }

    [Test]
    public void LoadTypeDefinitions_UnknownBaseType_ReturnsError()
    {
        var registry = new TypeRegistry();
        var typeDefs = new[]
        {
            new TypeDefinition("Child", "NonExistent", [], 1)
        };
        var errors = registry.LoadTypeDefinitions(typeDefs);
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("NonExistent").And.Contain("not found"));
    }

    [Test]
    public void LoadTypeDefinitions_DuplicateType_ReturnsError()
    {
        var registry = new TypeRegistry();
        var typeDefs = new[]
        {
            new TypeDefinition("Foo", null, [], 1),
            new TypeDefinition("Foo", null, [], 2)
        };
        var errors = registry.LoadTypeDefinitions(typeDefs);
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("duplicate"));
    }

    [Test]
    public void LoadTypeDefinitions_OptionalProperty()
    {
        var registry = new TypeRegistry();
        var typeDefs = new[]
        {
            new TypeDefinition("Foo", null, [
                new PropertyDefinition("Value", "string", true, false, 1)
            ], 1)
        };
        var errors = registry.LoadTypeDefinitions(typeDefs);
        Assert.That(errors, Is.Empty);
        var prop = registry.GetType("Foo")!.GetProperty("Value")!;
        Assert.That(prop.IsOptional, Is.True);
    }

    [Test]
    public void LoadTypeDefinitions_CollectionProperty()
    {
        var registry = new TypeRegistry();
        var typeDefs = new[]
        {
            new TypeDefinition("Foo", null, [
                new PropertyDefinition("Items", "string", false, true, 1)
            ], 1)
        };
        var errors = registry.LoadTypeDefinitions(typeDefs);
        Assert.That(errors, Is.Empty);
        var prop = registry.GetType("Foo")!.GetProperty("Items")!;
        Assert.That(prop.IsCollection, Is.True);
    }

    [Test]
    public void ConvertToText_Primitives()
    {
        var registry = new TypeRegistry();
        Assert.That(registry.ConvertToText(null), Is.EqualTo("null"));
        Assert.That(registry.ConvertToText("hello"), Is.EqualTo("hello"));
        Assert.That(registry.ConvertToText(42), Is.EqualTo("42"));
        Assert.That(registry.ConvertToText(true), Is.EqualTo("true"));
        Assert.That(registry.ConvertToText(false), Is.EqualTo("false"));
        Assert.That(registry.ConvertToText((byte)255), Is.EqualTo("255"));
    }

    [Test]
    public void RegisterCollection_And_GetCollection()
    {
        var registry = new TypeRegistry();
        registry.RegisterCollection(new CollectionDeclaration("Types", "Type", 1));
        var coll = registry.GetCollection("Types");
        Assert.That(coll, Is.Not.Null);
        Assert.That(coll!.ItemType, Is.EqualTo("Type"));
    }

    [Test]
    public void LoadTypeDefinitions_FromCodeAlan()
    {
        var source = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "..", "packages", "code", "src", "code.cop"));
        var parsed = ScriptParser.Parse(source, "code.cop");

        var registry = new TypeRegistry();
        var errors = registry.LoadTypeDefinitions(parsed.TypeDefinitions);
        Assert.That(errors, Is.Empty, $"Errors: {string.Join("; ", errors)}");

        // Verify key types exist
        Assert.That(registry.HasType("Type"), Is.True);
        Assert.That(registry.HasType("Method"), Is.True);
        Assert.That(registry.HasType("Constructor"), Is.True);
        Assert.That(registry.HasType("Parameter"), Is.True);

        // Verify Constructor inherits from Method
        var ctor = registry.GetType("Constructor")!;
        Assert.That(ctor.BaseType, Is.Not.Null);
        Assert.That(ctor.BaseType!.Name, Is.EqualTo("Method"));
        Assert.That(ctor.GetProperty("Name"), Is.Not.Null); // inherited
    }
}
