using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using NUnit.Framework;

namespace Cop.Tests.Lang;

/// <summary>
/// Unit tests for the static type-checking pass (<see cref="TypeChecker"/>). They use small,
/// self-contained programs so no package loading is required. The checker is conservative —
/// only confident, concrete type mismatches are reported; anything unknown is left alone.
/// </summary>
[TestFixture]
public class TypeCheckerTests
{
    private static IReadOnlyList<CopDiagnostic> Check(string source)
    {
        var module = CopParser.Parse(source, "test.cop");
        return TypeChecker.Check([module], [(module, "test.cop", source)]);
    }

    [Test]
    public void ArgumentTypeMismatch_IsReported()
    {
        var diags = Check("""
            type Position = { line : int }
            type Widget = { name : string }
            function mark(p : Position, msg : string) : int => 1
            let w : Widget = { name = 'x' }
            let r = mark(w, 'hello')
            """);
        Assert.That(diags, Has.Count.EqualTo(1));
        Assert.That(diags[0].Message, Does.Contain("mark"));
        Assert.That(diags[0].Message, Does.Contain("Position"));
        Assert.That(diags[0].Message, Does.Contain("Widget"));
    }

    [Test]
    public void MatchingArgument_NoError()
    {
        var diags = Check("""
            type Position = { line : int }
            function mark(p : Position, msg : string) : int => 1
            let p : Position = { line = 1 }
            let r = mark(p, 'hello')
            """);
        Assert.That(diags, Is.Empty);
    }

    [Test]
    public void Subtype_IsAccepted()
    {
        // A declared subtype satisfies a base-typed parameter.
        var diags = Check("""
            type Animal = { name : string }
            type Dog = Animal & { breed : string }
            function pet(a : Animal) : int => 1
            let d : Dog = { name = 'rex', breed = 'lab' }
            let r = pet(d)
            """);
        Assert.That(diags, Is.Empty);
    }

    [Test]
    public void PrimitiveWhereNamedExpected_IsReported()
    {
        var diags = Check("""
            type Position = { line : int }
            function mark(p : Position) : int => 1
            let r = mark(42)
            """);
        Assert.That(diags, Has.Count.EqualTo(1));
        Assert.That(diags[0].Message, Does.Contain("Position"));
    }

    [Test]
    public void UnknownArgumentType_NoError()
    {
        // `whatever` is a runtime-provided name the checker cannot resolve — it must NOT flag it.
        var diags = Check("""
            type Position = { line : int }
            function mark(p : Position) : int => 1
            let r = mark(whatever)
            """);
        Assert.That(diags, Is.Empty);
    }

    [Test]
    public void ObjectParameter_AcceptsAnything()
    {
        var diags = Check("""
            function log(x : object) : int => 1
            let r = log('a string')
            """);
        Assert.That(diags, Is.Empty);
    }
}
