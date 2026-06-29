using System.Collections.Generic;
using System.Linq;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using NUnit.Framework;

namespace Lang.Tests.Lang;

/// <summary>
/// The SemanticModel is the single read-only view that editor hover/completion query, backed by the
/// SAME type model + inference as `cop verify` (TypeChecker). These tests lock in that it answers
/// the questions the editor needs — expression types, type members (with inheritance), declared
/// callables, let bindings — so the editor never has to reimplement the compiler.
/// </summary>
[TestFixture]
public class SemanticModelTests
{
    private const string Program = """
        type Animal = { Name : string, Legs : int }
        type Dog : Animal = { Breed : string }
        let greeting = 'hello'
        predicate isBig(Dog) => Dog.Legs > 3
        function describe(Animal) : string => Animal.Name
        """;

    private static SemanticModel Build(string source)
    {
        var module = CopParser.Parse(source, "test.cop");
        return SemanticModel.Build(new[] { module });
    }

    [Test]
    public void InferExpressionType_Literals()
    {
        var m = Build(Program);
        Assert.That(m.InferExpressionType("'hi'")?.Display, Is.EqualTo("string"));
        Assert.That(m.InferExpressionType("42")?.Display, Is.EqualTo("int"));
        Assert.That(m.InferExpressionType("3.5")?.Display, Is.EqualTo("float"));
    }

    [Test]
    public void InferExpressionType_LetBinding_ResolvesToValueType()
    {
        var m = Build(Program);
        Assert.That(m.InferExpressionType("greeting")?.Display, Is.EqualTo("string"));
        Assert.That(m.LetType("greeting")?.Display, Is.EqualTo("string"));
        Assert.That(m.IsLet("greeting"), Is.True);
        Assert.That(m.IsLet("nope"), Is.False);
    }

    [Test]
    public void InferExpressionType_PropertyChainOnLocal()
    {
        var m = Build(Program);
        var locals = new Dictionary<string, TypeInfo> { ["x"] = new TypeInfo("Dog", false) };
        Assert.That(m.InferExpressionType("x.Legs", locals)?.Display, Is.EqualTo("int"));
        Assert.That(m.InferExpressionType("x.Name", locals)?.Display, Is.EqualTo("string"), "inherited from Animal");
        Assert.That(m.InferExpressionType("x.Breed", locals)?.Display, Is.EqualTo("string"));
    }

    [Test]
    public void InferExpressionType_Unknown_ReturnsNull()
    {
        var m = Build(Program);
        Assert.That(m.InferExpressionType("totallyUndefinedThing"), Is.Null);
        Assert.That(m.InferExpressionType(""), Is.Null);
    }

    [Test]
    public void InferExpressionType_PlusUnionOfCollections_KeepsCollectionType()
    {
        // The exact shape that showed "unknown" in the editor: a union of violation lists.
        const string source = """
            type Violation = { Message : string }
            let a : [Violation] = aProvider
            let b : [Violation] = bProvider
            let all = a + b
            """;
        var m = Build(source);
        Assert.That(m.LetType("a")?.Display, Is.EqualTo("[Violation]"));
        Assert.That(m.LetType("all")?.Display, Is.EqualTo("[Violation]"),
            "a union of two [Violation] lists must infer as [Violation], not unknown");
        Assert.That(m.InferExpressionType("a + b")?.Display, Is.EqualTo("[Violation]"));
        Assert.That(m.InferExpressionType("a + b + a")?.Display, Is.EqualTo("[Violation]"),
            "chained unions stay [Violation]");
    }

    [Test]
    public void PropertiesOf_IncludesInheritedProperties()
    {
        var m = Build(Program);
        var props = m.PropertiesOf("Dog");
        var names = props.Select(p => p.Name).ToHashSet();
        Assert.That(names, Does.Contain("Breed"));
        Assert.That(names, Does.Contain("Name"), "inherited");
        Assert.That(names, Does.Contain("Legs"), "inherited");
    }

    [Test]
    public void PropertyOf_ReturnsTypeAndDeclaringType()
    {
        var m = Build(Program);

        var name = m.PropertyOf("Dog", "Name");
        Assert.That(name, Is.Not.Null);
        Assert.That(name!.Type, Is.EqualTo("string"));
        Assert.That(name.DeclaringType, Is.EqualTo("Animal"), "declared on the base type");

        var breed = m.PropertyOf("Dog", "Breed");
        Assert.That(breed!.DeclaringType, Is.EqualTo("Dog"));

        Assert.That(m.PropertyOf("Dog", "Nope"), Is.Null);
    }

    [Test]
    public void Callable_Predicate_And_Function()
    {
        var m = Build(Program);

        var pred = m.Callable("isBig");
        Assert.That(pred, Is.Not.Null);
        Assert.That(pred!.IsPredicate, Is.True);
        Assert.That(pred.ParamTypes, Is.EqualTo(new[] { "Dog" }));

        var fn = m.Callable("describe");
        Assert.That(fn, Is.Not.Null);
        Assert.That(fn!.IsPredicate, Is.False);
        Assert.That(fn.ParamTypes, Is.EqualTo(new[] { "Animal" }));
        Assert.That(fn.ReturnType, Is.EqualTo("string"));

        Assert.That(m.Callable("nope"), Is.Null);
    }

    [Test]
    public void Callables_IncludesEveryOverload()
    {
        // Overloaded predicates/functions (e.g. isPublic(Type)/(Method)/(Field)) must all be offered
        // in completion, not just the first.
        const string src = """
            type A = { X : int }
            type B = { Y : int }
            predicate same(A) => true
            predicate same(B) => true
            """;
        var m = Build(src);

        var overloads = m.Callables().Where(c => c.Name == "same").ToList();
        Assert.That(overloads.Count, Is.EqualTo(2), "both overloads of 'same' must be offered");
        Assert.That(overloads.Select(c => c.ParamTypes[0]).ToHashSet(),
            Is.EquivalentTo(new[] { "A", "B" }));
    }

    [Test]
    public void KnownTypes_And_Enums()
    {
        var m = Build(Program);
        Assert.That(m.IsKnownType("Dog"), Is.True);
        Assert.That(m.IsKnownType("Animal"), Is.True);
        Assert.That(m.IsKnownType("Cat"), Is.False);
        Assert.That(m.TypeNames(), Does.Contain("Dog"));
    }

    [Test]
    public void TypeHelpers_CollectionStringNumeric()
    {
        Assert.That(SemanticModel.IsCollectionType("[Violation]"), Is.True);
        Assert.That(SemanticModel.IsCollectionType("string"), Is.False);
        Assert.That(SemanticModel.ElementType("[Violation]"), Is.EqualTo("Violation"));
        Assert.That(SemanticModel.IsStringType("string"), Is.True);
        Assert.That(SemanticModel.IsStringType("string?"), Is.True);
        Assert.That(SemanticModel.IsNumericType("int"), Is.True);
        Assert.That(SemanticModel.IsNumericType("float"), Is.True);
        Assert.That(SemanticModel.IsNumericType("string"), Is.False);
    }
}
