using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class FilterEvaluatorTests
{
    // --- Bool (PropertyFilter) ---

    [Test]
    public void PropertyFilter_True_MatchesTrueValue()
    {
        var filter = new PropertyFilter("Public", true);
        Assert.That(FilterEvaluator.Matches(filter, p => p == "Public" ? (object)true : throw new ArgumentException(p)), Is.True);
    }

    [Test]
    public void PropertyFilter_True_RejectsFalseValue()
    {
        var filter = new PropertyFilter("Public", true);
        Assert.That(FilterEvaluator.Matches(filter, p => p == "Public" ? (object)false : throw new ArgumentException(p)), Is.False);
    }

    [Test]
    public void PropertyFilter_False_MatchesFalseValue()
    {
        var filter = new PropertyFilter("Empty", false);
        Assert.That(FilterEvaluator.Matches(filter, p => p == "Empty" ? (object)false : throw new ArgumentException(p)), Is.True);
    }

    [Test]
    public void PropertyFilter_ThrowsOnNonBool()
    {
        var filter = new PropertyFilter("Name", true);
        Assert.Throws<ArgumentException>(() =>
            FilterEvaluator.Matches(filter, p => (object)"hello"));
    }

    // --- String operations ---

    [TestCase(StringOp.Equals, "Client", "Client", true)]
    [TestCase(StringOp.Equals, "Client", "client", true)] // case insensitive
    [TestCase(StringOp.Equals, "Client", "Server", false)]
    [TestCase(StringOp.StartsWith, "Cli", "Client", true)]
    [TestCase(StringOp.StartsWith, "Ser", "Client", false)]
    [TestCase(StringOp.EndsWith, ".cs", "main.cs", true)]
    [TestCase(StringOp.EndsWith, ".py", "main.cs", false)]
    [TestCase(StringOp.Contains, "ain", "main.cs", true)]
    [TestCase(StringOp.Contains, "xyz", "main.cs", false)]
    public void StringOpFilter_Evaluates(StringOp op, string filterVal, string actualVal, bool expected)
    {
        var filter = new StringOpFilter("Name", op, filterVal);
        Assert.That(FilterEvaluator.Matches(filter, _ => (object)actualVal), Is.EqualTo(expected));
    }

    [Test]
    public void StringOpFilter_Matches_Regex()
    {
        var filter = new StringOpFilter("Name", StringOp.Matches, @"^test\d+\.cs$");
        Assert.That(FilterEvaluator.Matches(filter, _ => (object)"test42.cs"), Is.True);
        Assert.That(FilterEvaluator.Matches(filter, _ => (object)"main.cs"), Is.False);
    }

    [Test]
    public void StringOpFilter_ThrowsOnNonString()
    {
        var filter = new StringOpFilter("Size", StringOp.Contains, "abc");
        Assert.Throws<ArgumentException>(() =>
            FilterEvaluator.Matches(filter, _ => (object)42));
    }

    // --- Numeric comparisons ---

    [TestCase(CompareOp.Equals, 100, 100, true)]
    [TestCase(CompareOp.Equals, 100, 99, false)]
    [TestCase(CompareOp.GreaterThan, 50, 100, true)]
    [TestCase(CompareOp.GreaterThan, 50, 50, false)]
    [TestCase(CompareOp.LessThan, 50, 49, true)]
    [TestCase(CompareOp.LessThan, 50, 50, false)]
    [TestCase(CompareOp.GreaterOrEqual, 50, 50, true)]
    [TestCase(CompareOp.GreaterOrEqual, 50, 49, false)]
    [TestCase(CompareOp.LessOrEqual, 50, 50, true)]
    [TestCase(CompareOp.LessOrEqual, 50, 51, false)]
    public void ComparisonFilter_Evaluates(CompareOp op, double filterVal, int actualVal, bool expected)
    {
        var filter = new ComparisonFilter("Size", op, filterVal);
        Assert.That(FilterEvaluator.Matches(filter, _ => (object)actualVal), Is.EqualTo(expected));
    }

    [Test]
    public void ComparisonFilter_WorksWithLong()
    {
        var filter = new ComparisonFilter("Size", CompareOp.GreaterThan, 1000);
        Assert.That(FilterEvaluator.Matches(filter, _ => (object)2000L), Is.True);
    }

    [Test]
    public void ComparisonFilter_ThrowsOnString()
    {
        var filter = new ComparisonFilter("Size", CompareOp.GreaterThan, 100);
        Assert.Throws<ArgumentException>(() =>
            FilterEvaluator.Matches(filter, _ => (object)"hello"));
    }

    // --- Combinators ---

    [Test]
    public void AndFilter_AllTrue_Matches()
    {
        var filter = new AndFilter([
            new PropertyFilter("Public", true),
            new ComparisonFilter("Size", CompareOp.GreaterThan, 10)
        ]);
        Assert.That(FilterEvaluator.Matches(filter, p => p switch
        {
            "Public" => (object)true,
            "Size" => 100,
            _ => throw new ArgumentException(p)
        }), Is.True);
    }

    [Test]
    public void AndFilter_OneFalse_Rejects()
    {
        var filter = new AndFilter([
            new PropertyFilter("Public", true),
            new ComparisonFilter("Size", CompareOp.GreaterThan, 1000)
        ]);
        Assert.That(FilterEvaluator.Matches(filter, p => p switch
        {
            "Public" => (object)true,
            "Size" => 100, // fails
            _ => throw new ArgumentException(p)
        }), Is.False);
    }

    [Test]
    public void NotFilter_InvertsBool()
    {
        var filter = new NotFilter(new PropertyFilter("Empty", true));
        Assert.That(FilterEvaluator.Matches(filter, _ => (object)false), Is.True);
        Assert.That(FilterEvaluator.Matches(filter, _ => (object)true), Is.False);
    }

    [Test]
    public void NullFilter_MatchesAll()
    {
        Assert.That(FilterEvaluator.Matches(null, _ => throw new InvalidOperationException("should not be called")), Is.True);
    }
}
