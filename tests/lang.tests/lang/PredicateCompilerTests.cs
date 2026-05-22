using Cop.Core;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using NUnit.Framework;
using AstExpr = Cop.Lang.Ast.Expression;

namespace Cop.Tests.Lang;

[TestFixture]
public class PredicateCompilerTests
{
    [Test]
    public void BareIdentifier_CompilesTo_PropertyFilter()
    {
        // :enabled → PropertyFilter("enabled", true)
        AstExpr pred = new IdentifierExpr("enabled", 1);
        var result = PredicateCompiler.TryCompile(pred);

        Assert.That(result, Is.InstanceOf<PropertyFilter>());
        var pf = (PropertyFilter)result!;
        Assert.That(pf.Property, Is.EqualTo("enabled"));
        Assert.That(pf.Value, Is.True);
    }

    [Test]
    public void BareIdentifier_Negated_CompilesTo_NotPropertyFilter()
    {
        AstExpr pred = new IdentifierExpr("enabled", 1);
        var result = PredicateCompiler.TryCompile(pred, negated: true);

        Assert.That(result, Is.InstanceOf<NotFilter>());
        var nf = (NotFilter)result!;
        Assert.That(nf.Inner, Is.InstanceOf<PropertyFilter>());
    }

    [Test]
    public void StringOp_WithPropertyContext_Compiles()
    {
        // Name:startsWith('A')
        AstExpr pred = new CallExpr(
            new IdentifierExpr("startsWith", 1),
            [new LiteralExpr("A", 1)],
            1);
        var result = PredicateCompiler.TryCompile(pred, propertyContext: "Name");

        Assert.That(result, Is.InstanceOf<StringOpFilter>());
        var sf = (StringOpFilter)result!;
        Assert.That(sf.Property, Is.EqualTo("Name"));
        Assert.That(sf.Op, Is.EqualTo(StringOp.StartsWith));
        Assert.That(sf.Value, Is.EqualTo("A"));
    }

    [Test]
    public void StringOp_ShortForm_Compiles()
    {
        // Name:sw('A')
        AstExpr pred = new CallExpr(
            new IdentifierExpr("sw", 1),
            [new LiteralExpr("A", 1)],
            1);
        var result = PredicateCompiler.TryCompile(pred, propertyContext: "Name");

        Assert.That(result, Is.InstanceOf<StringOpFilter>());
        var sf = (StringOpFilter)result!;
        Assert.That(sf.Op, Is.EqualTo(StringOp.StartsWith));
    }

    [Test]
    public void ComparisonOp_Compiles()
    {
        // Size:gt(100)
        AstExpr pred = new CallExpr(
            new IdentifierExpr("gt", 1),
            [new LiteralExpr(100, 1)],
            1);
        var result = PredicateCompiler.TryCompile(pred, propertyContext: "Size");

        Assert.That(result, Is.InstanceOf<ComparisonFilter>());
        var cf = (ComparisonFilter)result!;
        Assert.That(cf.Property, Is.EqualTo("Size"));
        Assert.That(cf.Op, Is.EqualTo(CompareOp.GreaterThan));
        Assert.That(cf.Value, Is.EqualTo(100.0));
    }

    [Test]
    public void And_Compiles()
    {
        // enabled && active
        AstExpr pred = new BinaryExpr(
            new IdentifierExpr("enabled", 1),
            BinaryOp.And,
            new IdentifierExpr("active", 1),
            1);
        var result = PredicateCompiler.TryCompile(pred);

        Assert.That(result, Is.InstanceOf<AndFilter>());
        var af = (AndFilter)result!;
        Assert.That(af.Conditions, Has.Count.EqualTo(2));
    }

    [Test]
    public void Or_Compiles()
    {
        AstExpr pred = new BinaryExpr(
            new IdentifierExpr("enabled", 1),
            BinaryOp.Or,
            new IdentifierExpr("active", 1),
            1);
        var result = PredicateCompiler.TryCompile(pred);

        Assert.That(result, Is.InstanceOf<OrFilter>());
    }

    [Test]
    public void Not_Compiles()
    {
        AstExpr pred = new UnaryExpr(UnaryOp.Not, new IdentifierExpr("enabled", 1), 1);
        var result = PredicateCompiler.TryCompile(pred);

        Assert.That(result, Is.InstanceOf<NotFilter>());
    }

    [Test]
    public void NestedFilter_PropertyWithOp_Compiles()
    {
        // Name:startsWith('A') as a nested FilterExpr
        AstExpr pred = new FilterExpr(
            new IdentifierExpr("Name", 1),
            new CallExpr(new IdentifierExpr("startsWith", 1), [new LiteralExpr("A", 1)], 1),
            false, 1);
        var result = PredicateCompiler.TryCompile(pred);

        Assert.That(result, Is.InstanceOf<StringOpFilter>());
        var sf = (StringOpFilter)result!;
        Assert.That(sf.Property, Is.EqualTo("Name"));
        Assert.That(sf.Op, Is.EqualTo(StringOp.StartsWith));
    }

    [Test]
    public void CollectionFunction_DoesNotCompileAsProperty()
    {
        // 'any' is a collection function, not a property
        AstExpr pred = new IdentifierExpr("any", 1);
        var propName = PredicateCompiler.TryExtractPropertyAccess(pred);

        Assert.That(propName, Is.Null);
    }

    [Test]
    public void UnknownPredicate_ReturnsNull()
    {
        // User-defined predicates can't be compiled
        AstExpr pred = new CallExpr(
            new IdentifierExpr("myCustomPredicate", 1),
            [new LiteralExpr("arg", 1)],
            1);
        var result = PredicateCompiler.TryCompile(pred);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void CallWithoutPropertyContext_ReturnsNull()
    {
        // startsWith('A') with no property context → not compilable
        AstExpr pred = new CallExpr(
            new IdentifierExpr("startsWith", 1),
            [new LiteralExpr("A", 1)],
            1);
        var result = PredicateCompiler.TryCompile(pred);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Equals_WithString_CompilesTo_StringOp()
    {
        AstExpr pred = new CallExpr(
            new IdentifierExpr("equals", 1),
            [new LiteralExpr("hello", 1)],
            1);
        var result = PredicateCompiler.TryCompile(pred, propertyContext: "Name");

        Assert.That(result, Is.InstanceOf<StringOpFilter>());
        var sf = (StringOpFilter)result!;
        Assert.That(sf.Op, Is.EqualTo(StringOp.Equals));
        Assert.That(sf.Value, Is.EqualTo("hello"));
    }

    [Test]
    public void Equals_WithNumber_CompilesTo_Comparison()
    {
        AstExpr pred = new CallExpr(
            new IdentifierExpr("eq", 1),
            [new LiteralExpr(42, 1)],
            1);
        var result = PredicateCompiler.TryCompile(pred, propertyContext: "Count");

        Assert.That(result, Is.InstanceOf<ComparisonFilter>());
        var cf = (ComparisonFilter)result!;
        Assert.That(cf.Op, Is.EqualTo(CompareOp.Equals));
        Assert.That(cf.Value, Is.EqualTo(42.0));
    }
}
