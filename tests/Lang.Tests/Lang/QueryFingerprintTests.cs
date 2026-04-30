using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class QueryFingerprintTests
{
    [Test]
    public void EmptyFilters_ReturnsBaseCollection()
    {
        var result = QueryFingerprint.Compute("Types", [], null);
        Assert.That(result, Is.EqualTo("Types"));
    }

    [Test]
    public void EmptyFilters_WithDocPath_IncludesDocPath()
    {
        var result = QueryFingerprint.Compute("Types", [], "src/Foo.cs");
        Assert.That(result, Is.EqualTo("Types@src/Foo.cs"));
    }

    [Test]
    public void SingleFilter_ProducesExpectedKey()
    {
        var filters = new List<Expression> { new IdentifierExpr("Public") };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Is.EqualTo("Types:Public"));
    }

    [Test]
    public void MultipleFilters_AreSortedAlphabetically()
    {
        var filters = new List<Expression>
        {
            new IdentifierExpr("Public"),
            new IdentifierExpr("Abstract")
        };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Is.EqualTo("Types:Abstract:Public"));
    }

    [Test]
    public void ReverseOrder_ProducesSameFingerprint()
    {
        var filtersA = new List<Expression>
        {
            new IdentifierExpr("Public"),
            new IdentifierExpr("Abstract")
        };
        var filtersB = new List<Expression>
        {
            new IdentifierExpr("Abstract"),
            new IdentifierExpr("Public")
        };

        var a = QueryFingerprint.Compute("Types", filtersA, "doc.cs");
        var b = QueryFingerprint.Compute("Types", filtersB, "doc.cs");
        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void ThreeFilters_AllSorted()
    {
        var filters = new List<Expression>
        {
            new IdentifierExpr("csharp"),
            new IdentifierExpr("Abstract"),
            new IdentifierExpr("Public")
        };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Is.EqualTo("Types:Abstract:Public:csharp"));
    }

    [Test]
    public void SelectBarrier_FlushesAndPreservesOrder()
    {
        var filters = new List<Expression>
        {
            new IdentifierExpr("Public"),
            new IdentifierExpr("Abstract"),
            new FunctionCallExpr("Select", [new IdentifierExpr("Name")])
        };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Is.EqualTo("Types:Abstract:Public.Select(Name)"));
    }

    [Test]
    public void SelectBarrier_PredicatesAfterBarrierSortedSeparately()
    {
        var filters = new List<Expression>
        {
            new IdentifierExpr("Public"),
            new FunctionCallExpr("Select", [new IdentifierExpr("Name")]),
            new IdentifierExpr("Zebra"),
            new IdentifierExpr("Alpha")
        };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Is.EqualTo("Types:Public.Select(Name):Alpha:Zebra"));
    }

    [Test]
    public void TextBarrier_IsNonCommutative()
    {
        var filters = new List<Expression>
        {
            new IdentifierExpr("Public"),
            new FunctionCallExpr("Text", [new LiteralExpr("template")])
        };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Is.EqualTo("Types:Public.Text('template')"));
    }

    [Test]
    public void UserFunction_IsBarrierWhenInFunctionNames()
    {
        var functionNames = new HashSet<string> { "myFunc" };
        var filters = new List<Expression>
        {
            new IdentifierExpr("Public"),
            new FunctionCallExpr("myFunc", []),
            new IdentifierExpr("Abstract")
        };
        var result = QueryFingerprint.Compute("Types", filters, null, functionNames);
        Assert.That(result, Is.EqualTo("Types:Public:myFunc():Abstract"));
    }

    [Test]
    public void UnknownFunction_TreatedAsCommutative()
    {
        // FunctionCallExpr not in functionNames and not select/text → commutative
        var filters = new List<Expression>
        {
            new FunctionCallExpr("unknownPred", [new LiteralExpr("x")]),
            new IdentifierExpr("Public")
        };
        var result = QueryFingerprint.Compute("Types", filters, null);
        // Both are commutative, so sorted
        Assert.That(result, Is.EqualTo("Types:Public:unknownPred('x')"));
    }

    [Test]
    public void NegatedPredicate_SerializedWithBang()
    {
        var filters = new List<Expression>
        {
            new UnaryExpr("!", new IdentifierExpr("Public"))
        };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Is.EqualTo("Types:!Public"));
    }

    [Test]
    public void BinaryExpression_SerializedWithParens()
    {
        var filters = new List<Expression>
        {
            new BinaryExpr(
                new MemberAccessExpr(new IdentifierExpr("Name"), "Length"),
                ">",
                new LiteralExpr(5))
        };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Is.EqualTo("Types:(Name.Length>5)"));
    }

    [Test]
    public void MemberAccess_SerializedWithDot()
    {
        var filters = new List<Expression>
        {
            new MemberAccessExpr(new IdentifierExpr("File"), "Language")
        };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Is.EqualTo("Types:File.Language"));
    }

    [Test]
    public void PredicateCall_SerializedWithArgs()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(
                new IdentifierExpr("Name"),
                "startsWith",
                [new LiteralExpr("A")])
        };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Is.EqualTo("Types:Name.startsWith('A')"));
    }

    [Test]
    public void NegatedPredicateCall_SerializedWithBang()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(
                new IdentifierExpr("Name"),
                "startsWith",
                [new LiteralExpr("A")],
                Negated: true)
        };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Is.EqualTo("Types:!Name.startsWith('A')"));
    }

    [Test]
    public void ComplexChain_OrderIndependentBeforeBarrier()
    {
        // Types:Public:csharp:Abstract.Select(Name) should equal
        // Types:csharp:Abstract:Public.Select(Name)
        var filtersA = new List<Expression>
        {
            new IdentifierExpr("Public"),
            new IdentifierExpr("csharp"),
            new IdentifierExpr("Abstract"),
            new FunctionCallExpr("Select", [new IdentifierExpr("Name")])
        };
        var filtersB = new List<Expression>
        {
            new IdentifierExpr("csharp"),
            new IdentifierExpr("Abstract"),
            new IdentifierExpr("Public"),
            new FunctionCallExpr("Select", [new IdentifierExpr("Name")])
        };

        var a = QueryFingerprint.Compute("Types", filtersA, "test.cs");
        var b = QueryFingerprint.Compute("Types", filtersB, "test.cs");
        Assert.That(a, Is.EqualTo(b));
        Assert.That(a, Is.EqualTo("Types:Abstract:Public:csharp.Select(Name)@test.cs"));
    }

    [Test]
    public void DifferentBaseCollections_DifferentFingerprints()
    {
        var filters = new List<Expression> { new IdentifierExpr("Public") };
        var a = QueryFingerprint.Compute("Types", filters, null);
        var b = QueryFingerprint.Compute("Statements", filters, null);
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void DifferentDocPaths_DifferentFingerprints()
    {
        var filters = new List<Expression> { new IdentifierExpr("Public") };
        var a = QueryFingerprint.Compute("Types", filters, "a.cs");
        var b = QueryFingerprint.Compute("Types", filters, "b.cs");
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void ListLiteral_Serialized()
    {
        var filters = new List<Expression>
        {
            new ListLiteralExpr([new LiteralExpr("a"), new LiteralExpr("b")])
        };
        var result = QueryFingerprint.Compute("Types", filters, null);
        Assert.That(result, Is.EqualTo("Types:['a','b']"));
    }

    [Test]
    public void PathOverride_EmptyFilters_IncludesPath()
    {
        var result = QueryFingerprint.Compute("Types", [], null, pathOverride: "../sdk/");
        Assert.That(result, Is.EqualTo("Types#../sdk/"));
    }

    [Test]
    public void PathOverride_WithFilters_IncludesPath()
    {
        var filters = new List<Expression> { new IdentifierExpr("Public") };
        var result = QueryFingerprint.Compute("Types", filters, null, pathOverride: "../sdk/");
        Assert.That(result, Is.EqualTo("Types:Public#../sdk/"));
    }

    [Test]
    public void PathOverride_WithDocPath_IncludesBoth()
    {
        var result = QueryFingerprint.Compute("Types", [], "src/Foo.cs", pathOverride: "../sdk/");
        Assert.That(result, Is.EqualTo("Types@src/Foo.cs#../sdk/"));
    }

    [Test]
    public void DifferentPaths_ProduceDifferentFingerprints()
    {
        var a = QueryFingerprint.Compute("Types", [], null, pathOverride: "../sdk/");
        var b = QueryFingerprint.Compute("Types", [], null, pathOverride: "../other/");
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void NullPathOverride_MatchesDefault()
    {
        var a = QueryFingerprint.Compute("Types", [], null);
        var b = QueryFingerprint.Compute("Types", [], null, pathOverride: null);
        Assert.That(a, Is.EqualTo(b));
    }
}
