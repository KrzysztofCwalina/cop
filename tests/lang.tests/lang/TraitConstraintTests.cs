using NUnit.Framework;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;

namespace Cop.Tests.Lang;

[TestFixture]
public class TraitConstraintTests
{
    // ========================================================================
    // TypeRef.Constraint parsing
    // ========================================================================

    [Test]
    public void TypeRef_ConstraintField_DefaultNull()
    {
        var typeRef = new TypeRef("T", true);
        Assert.That(typeRef.Constraint, Is.Null);
    }

    [Test]
    public void TypeRef_ConstraintField_SetExplicitly()
    {
        var typeRef = new TypeRef("T", true, 0, "comparable");
        Assert.That(typeRef.Constraint, Is.EqualTo("comparable"));
    }

    // ========================================================================
    // Parser: [T:comparable] syntax
    // ========================================================================

    [Test]
    public void Parse_ConstrainedTypeParameter()
    {
        var source = "function distinct(items: [T:comparable]) : [T] => items";
        var module = Cop.Lang.Parser.CopParser.Parse(source, "test.cop");
        var func = module.Declarations.OfType<FunctionDecl>().First();
        Assert.That(func.Params[0].Type, Is.Not.Null);
        Assert.That(func.Params[0].Type!.Name, Is.EqualTo("T"));
        Assert.That(func.Params[0].Type!.IsCollection, Is.True);
        Assert.That(func.Params[0].Type!.Constraint, Is.EqualTo("comparable"));
    }

    [Test]
    public void Parse_UnconstrainedTypeParameter()
    {
        var source = "function where(items: [T], pred: (T) => bool) : [T] => items";
        var module = Cop.Lang.Parser.CopParser.Parse(source, "test.cop");
        var func = module.Declarations.OfType<FunctionDecl>().First();
        Assert.That(func.Params[0].Type!.Constraint, Is.Null);
    }

    // ========================================================================
    // TypeRegistry: Trait detection and conformance
    // ========================================================================

    [Test]
    public void IsTrait_TypeWithFunctionProperties_IsTrue()
    {
        var desc = new TypeDescriptor("comparable");
        desc.Properties["equals"] = new PropertyDescriptor("equals", "(Self, Self) => bool", false, false);
        Assert.That(TypeRegistry.IsTrait(desc), Is.True);
    }

    [Test]
    public void IsTrait_TypeWithDataProperties_IsFalse()
    {
        var desc = new TypeDescriptor("Point");
        desc.Properties["X"] = new PropertyDescriptor("X", "int", false, false);
        desc.Properties["Y"] = new PropertyDescriptor("Y", "int", false, false);
        Assert.That(TypeRegistry.IsTrait(desc), Is.False);
    }

    [Test]
    public void IsTrait_EmptyType_IsFalse()
    {
        var desc = new TypeDescriptor("empty");
        Assert.That(TypeRegistry.IsTrait(desc), Is.False);
    }

    [Test]
    public void LoadTypeDefinitions_RegistersTraitConformance()
    {
        var registry = new TypeRegistry();
        var typeDefs = new List<TypeDefinition>
        {
            new("comparable", null, [
                new PropertyDefinition("equals", "(Self, Self) => bool", false, false, 1)
            ], 1),
            new("int", "comparable", [], 3)
        };
        var errors = registry.LoadTypeDefinitions(typeDefs);
        Assert.That(errors, Is.Empty);
        Assert.That(registry.ConformsTo("int", "comparable"), Is.True);
    }

    [Test]
    public void ConformsTo_UnregisteredType_ReturnsFalse()
    {
        var registry = new TypeRegistry();
        var typeDefs = new List<TypeDefinition>
        {
            new("comparable", null, [
                new PropertyDefinition("equals", "(Self, Self) => bool", false, false, 1)
            ], 1),
            new("int", "comparable", [], 3)
        };
        registry.LoadTypeDefinitions(typeDefs);
        Assert.That(registry.ConformsTo("Foo", "comparable"), Is.False);
    }

    [Test]
    public void ConformsTo_Object_AlwaysConforms()
    {
        var registry = new TypeRegistry();
        Assert.That(registry.ConformsTo("object", "comparable"), Is.True);
        Assert.That(registry.ConformsTo("object", "anything"), Is.True);
    }

    [Test]
    public void IsTraitName_RegisteredTrait_ReturnsTrue()
    {
        var registry = new TypeRegistry();
        var typeDefs = new List<TypeDefinition>
        {
            new("comparable", null, [
                new PropertyDefinition("equals", "(Self, Self) => bool", false, false, 1)
            ], 1)
        };
        registry.LoadTypeDefinitions(typeDefs);
        Assert.That(registry.IsTraitName("comparable"), Is.True);
    }

    [Test]
    public void IsTraitName_NonTrait_ReturnsFalse()
    {
        var registry = new TypeRegistry();
        Assert.That(registry.IsTraitName("int"), Is.False);
        Assert.That(registry.IsTraitName("nonexistent"), Is.False);
    }

    // ========================================================================
    // GenericInference: Constraint validation
    // ========================================================================

    [Test]
    public void ValidateConstraints_ConformingType_ReturnsNull()
    {
        var registry = new TypeRegistry();
        var typeDefs = new List<TypeDefinition>
        {
            new("comparable", null, [
                new PropertyDefinition("equals", "(Self, Self) => bool", false, false, 1)
            ], 1),
            new("int", "comparable", [], 3)
        };
        registry.LoadTypeDefinitions(typeDefs);

        var decl = MakeDecl("distinct", [
            new Parameter("items", new TypeRef("T", true, 0, "comparable"))
        ], new TypeRef("T", true));
        var bindings = new Dictionary<string, string> { ["T"] = "int" };

        var error = GenericInference.ValidateConstraints(decl, bindings, registry);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void ValidateConstraints_NonConformingType_ReturnsError()
    {
        var registry = new TypeRegistry();
        var typeDefs = new List<TypeDefinition>
        {
            new("comparable", null, [
                new PropertyDefinition("equals", "(Self, Self) => bool", false, false, 1)
            ], 1)
        };
        registry.LoadTypeDefinitions(typeDefs);

        var decl = MakeDecl("distinct", [
            new Parameter("items", new TypeRef("T", true, 0, "comparable"))
        ], new TypeRef("T", true));
        var bindings = new Dictionary<string, string> { ["T"] = "Foo" };

        var error = GenericInference.ValidateConstraints(decl, bindings, registry);
        Assert.That(error, Is.Not.Null);
        Assert.That(error, Does.Contain("comparable"));
        Assert.That(error, Does.Contain("Foo"));
    }

    [Test]
    public void ValidateConstraints_NoConstraints_ReturnsNull()
    {
        var registry = new TypeRegistry();
        var decl = MakeDecl("where", [
            new Parameter("items", new TypeRef("T", true))
        ], new TypeRef("T", true));
        var bindings = new Dictionary<string, string> { ["T"] = "anything" };

        var error = GenericInference.ValidateConstraints(decl, bindings, registry);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void ValidateConstraints_ObjectType_AlwaysPasses()
    {
        var registry = new TypeRegistry();
        var typeDefs = new List<TypeDefinition>
        {
            new("comparable", null, [
                new PropertyDefinition("equals", "(Self, Self) => bool", false, false, 1)
            ], 1)
        };
        registry.LoadTypeDefinitions(typeDefs);

        var decl = MakeDecl("distinct", [
            new Parameter("items", new TypeRef("T", true, 0, "comparable"))
        ], new TypeRef("T", true));
        var bindings = new Dictionary<string, string> { ["T"] = "object" };

        var error = GenericInference.ValidateConstraints(decl, bindings, registry);
        Assert.That(error, Is.Null);
    }

    // ========================================================================
    // Conformance does NOT create inheritance (regression test)
    // ========================================================================

    [Test]
    public void LoadTypeDefinitions_ConformanceDoesNotOverwriteExistingType()
    {
        var registry = new TypeRegistry();
        // "int" already exists as core primitive in TypeRegistry constructor
        var typeDefs = new List<TypeDefinition>
        {
            new("comparable", null, [
                new PropertyDefinition("equals", "(Self, Self) => bool", false, false, 1)
            ], 1),
            new("int", "comparable", [], 3)
        };
        var errors = registry.LoadTypeDefinitions(typeDefs);
        Assert.That(errors, Is.Empty);

        // int should still be accessible and not have BaseType set to comparable
        var intType = registry.GetType("int");
        Assert.That(intType, Is.Not.Null);
        Assert.That(intType!.BaseType, Is.Null);
    }

    private static FunctionDecl MakeDecl(string name, List<Parameter> parms, TypeRef? returnType = null)
    {
        return new FunctionDecl(name, parms, returnType,
            new IntrinsicBody(), IsExported: true);
    }
}
