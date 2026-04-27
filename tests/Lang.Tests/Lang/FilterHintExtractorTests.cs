using Cop.Core;
using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class FilterHintExtractorTests
{
    private static TypeDescriptor MakeType(params (string Name, string Type)[] props)
    {
        var td = new TypeDescriptor("TestItem");
        foreach (var (name, type) in props)
            td.Properties.Add(name, new PropertyDescriptor(name, type));
        return td;
    }

    private static readonly TypeDescriptor FileType = MakeType(
        ("Path", "string"), ("Name", "string"), ("Extension", "string"),
        ("Size", "int"), ("Depth", "int"), ("Empty", "bool"),
        ("Public", "bool"), ("MinutesSinceModified", "int"));

    // --- Bool property extraction ---

    [Test]
    public void BareIdentifier_BoolProperty_ExtractsPropertyFilter()
    {
        var filters = new List<Expression> { new IdentifierExpr("Empty") };
        var (hints, idx) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<PropertyFilter>());
        var pf = (PropertyFilter)hints!;
        Assert.That(pf.Property, Is.EqualTo("Empty"));
        Assert.That(pf.Value, Is.True);
        Assert.That(idx, Is.EqualTo(1));
    }

    [Test]
    public void NegatedIdentifier_BoolProperty_ExtractsNegatedFilter()
    {
        var filters = new List<Expression> { new UnaryExpr("!", new IdentifierExpr("Public")) };
        var (hints, idx) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<PropertyFilter>());
        var pf = (PropertyFilter)hints!;
        Assert.That(pf.Property, Is.EqualTo("Public"));
        Assert.That(pf.Value, Is.False);
        Assert.That(idx, Is.EqualTo(1));
    }

    // --- String operation extraction ---

    [Test]
    public void PredicateCall_StartsWith_ExtractsStringOpFilter()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Name"), "startsWith", [new LiteralExpr("Client")])
        };
        var (hints, idx) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<StringOpFilter>());
        var sf = (StringOpFilter)hints!;
        Assert.That(sf.Property, Is.EqualTo("Name"));
        Assert.That(sf.Op, Is.EqualTo(StringOp.StartsWith));
        Assert.That(sf.Value, Is.EqualTo("Client"));
        Assert.That(idx, Is.EqualTo(1));
    }

    [Test]
    public void PredicateCall_Equals_ExtractsStringOpFilter()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Extension"), "equals", [new LiteralExpr(".cs")])
        };
        var (hints, idx) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<StringOpFilter>());
        var sf = (StringOpFilter)hints!;
        Assert.That(sf.Property, Is.EqualTo("Extension"));
        Assert.That(sf.Op, Is.EqualTo(StringOp.Equals));
        Assert.That(sf.Value, Is.EqualTo(".cs"));
    }

    [TestCase("endsWith", StringOp.EndsWith)]
    [TestCase("contains", StringOp.Contains)]
    [TestCase("matches", StringOp.Matches)]
    public void PredicateCall_AllStringOps(string opName, StringOp expectedOp)
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Path"), opName, [new LiteralExpr("test")])
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<StringOpFilter>());
        Assert.That(((StringOpFilter)hints!).Op, Is.EqualTo(expectedOp));
    }

    // --- Binary comparison extraction (numeric) ---

    [Test]
    public void Binary_DepthLessThan_ExtractsComparisonFilter()
    {
        // Depth < 3
        var filters = new List<Expression>
        {
            new BinaryExpr(new IdentifierExpr("Depth"), "<", new LiteralExpr(3))
        };
        var (hints, idx) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<ComparisonFilter>());
        var cf = (ComparisonFilter)hints!;
        Assert.That(cf.Property, Is.EqualTo("Depth"));
        Assert.That(cf.Op, Is.EqualTo(CompareOp.LessThan));
        Assert.That(cf.Value, Is.EqualTo(3));
        Assert.That(idx, Is.EqualTo(1));
    }

    [Test]
    public void Binary_SizeGreaterThan_ExtractsComparisonFilter()
    {
        // Size > 1000
        var filters = new List<Expression>
        {
            new BinaryExpr(new IdentifierExpr("Size"), ">", new LiteralExpr(1000))
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<ComparisonFilter>());
        var cf = (ComparisonFilter)hints!;
        Assert.That(cf.Property, Is.EqualTo("Size"));
        Assert.That(cf.Op, Is.EqualTo(CompareOp.GreaterThan));
        Assert.That(cf.Value, Is.EqualTo(1000));
    }

    [TestCase("==", CompareOp.Equals)]
    [TestCase(">=", CompareOp.GreaterOrEqual)]
    [TestCase("<=", CompareOp.LessOrEqual)]
    public void Binary_AllNumericOps(string op, CompareOp expectedOp)
    {
        var filters = new List<Expression>
        {
            new BinaryExpr(new IdentifierExpr("Size"), op, new LiteralExpr(100))
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<ComparisonFilter>());
        Assert.That(((ComparisonFilter)hints!).Op, Is.EqualTo(expectedOp));
    }

    [Test]
    public void Binary_NotEquals_Numeric_ExtractsNotFilter()
    {
        // Size != 0
        var filters = new List<Expression>
        {
            new BinaryExpr(new IdentifierExpr("Size"), "!=", new LiteralExpr(0))
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<NotFilter>());
        var inner = ((NotFilter)hints!).Inner;
        Assert.That(inner, Is.InstanceOf<ComparisonFilter>());
        Assert.That(((ComparisonFilter)inner).Op, Is.EqualTo(CompareOp.Equals));
    }

    // --- Binary string equality ---

    [Test]
    public void Binary_StringEquals_ExtractsStringOpFilter()
    {
        // Extension == '.cs'
        var filters = new List<Expression>
        {
            new BinaryExpr(new IdentifierExpr("Extension"), "==", new LiteralExpr(".cs"))
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<StringOpFilter>());
        var sf = (StringOpFilter)hints!;
        Assert.That(sf.Property, Is.EqualTo("Extension"));
        Assert.That(sf.Op, Is.EqualTo(StringOp.Equals));
        Assert.That(sf.Value, Is.EqualTo(".cs"));
    }

    [Test]
    public void Binary_StringNotEquals_ExtractsNotFilter()
    {
        // Extension != '.cs'
        var filters = new List<Expression>
        {
            new BinaryExpr(new IdentifierExpr("Extension"), "!=", new LiteralExpr(".cs"))
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<NotFilter>());
        var inner = ((NotFilter)hints!).Inner;
        Assert.That(inner, Is.InstanceOf<StringOpFilter>());
        Assert.That(((StringOpFilter)inner).Property, Is.EqualTo("Extension"));
    }

    // --- Binary bool equality ---

    [Test]
    public void Binary_BoolEquals_ExtractsPropertyFilter()
    {
        // Empty == true
        var filters = new List<Expression>
        {
            new BinaryExpr(new IdentifierExpr("Empty"), "==", new LiteralExpr(true))
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<PropertyFilter>());
        var pf = (PropertyFilter)hints!;
        Assert.That(pf.Property, Is.EqualTo("Empty"));
        Assert.That(pf.Value, Is.True);
    }

    [Test]
    public void Binary_BoolNotEquals_ExtractsPropertyFilter()
    {
        // Public != true → PropertyFilter(Public, false)
        var filters = new List<Expression>
        {
            new BinaryExpr(new IdentifierExpr("Public"), "!=", new LiteralExpr(true))
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<PropertyFilter>());
        Assert.That(((PropertyFilter)hints!).Value, Is.False);
    }

    // --- Reversed operand order ---

    [Test]
    public void Binary_ReversedOrder_LiteralOnLeft()
    {
        // 3 > Depth  →  Depth < 3
        var filters = new List<Expression>
        {
            new BinaryExpr(new LiteralExpr(3), ">", new IdentifierExpr("Depth"))
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<ComparisonFilter>());
        var cf = (ComparisonFilter)hints!;
        Assert.That(cf.Property, Is.EqualTo("Depth"));
        Assert.That(cf.Op, Is.EqualTo(CompareOp.LessThan));
        Assert.That(cf.Value, Is.EqualTo(3));
    }

    // --- Multiple filters (AND chain) ---

    [Test]
    public void MultipleFilters_CombinedIntoAndFilter()
    {
        var filters = new List<Expression>
        {
            new IdentifierExpr("Public"),
            new BinaryExpr(new IdentifierExpr("Size"), ">", new LiteralExpr(100)),
            new PredicateCallExpr(new IdentifierExpr("Extension"), "endsWith", [new LiteralExpr(".cs")])
        };
        var (hints, idx) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<AndFilter>());
        var af = (AndFilter)hints!;
        Assert.That(af.Conditions, Has.Count.EqualTo(3));
        Assert.That(af.Conditions[0], Is.InstanceOf<PropertyFilter>());
        Assert.That(af.Conditions[1], Is.InstanceOf<ComparisonFilter>());
        Assert.That(af.Conditions[2], Is.InstanceOf<StringOpFilter>());
        Assert.That(idx, Is.EqualTo(3));
    }

    // --- Barrier stops extraction ---

    [Test]
    public void BarrierFilter_StopsExtraction()
    {
        var filters = new List<Expression>
        {
            new IdentifierExpr("Public"),
            new IdentifierExpr("Select"), // barrier — not a property
            new BinaryExpr(new IdentifierExpr("Size"), ">", new LiteralExpr(100))
        };
        var (hints, idx) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<PropertyFilter>());
        Assert.That(idx, Is.EqualTo(1)); // Only first extracted
    }

    // --- Predicate inlining ---

    [Test]
    public void InlinePredicate_BoolBody()
    {
        // predicate isEmpty(TestItem) => TestItem.Empty
        var preds = new Dictionary<string, List<PredicateDefinition>>
        {
            ["isEmpty"] = [new PredicateDefinition("isEmpty", "TestItem", null,
                new MemberAccessExpr(new IdentifierExpr("TestItem"), "Empty"), 1)]
        };
        var predNames = new HashSet<string> { "isEmpty" };

        var filters = new List<Expression> { new IdentifierExpr("isEmpty") };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType, predNames, preds);
        Assert.That(hints, Is.InstanceOf<PropertyFilter>());
        Assert.That(((PropertyFilter)hints!).Property, Is.EqualTo("Empty"));
    }

    [Test]
    public void InlinePredicate_ComparisonBody()
    {
        // predicate shallow(TestItem) => TestItem.Depth < 3
        var preds = new Dictionary<string, List<PredicateDefinition>>
        {
            ["shallow"] = [new PredicateDefinition("shallow", "TestItem", null,
                new BinaryExpr(
                    new MemberAccessExpr(new IdentifierExpr("TestItem"), "Depth"),
                    "<", new LiteralExpr(3)), 1)]
        };
        var predNames = new HashSet<string> { "shallow" };

        var filters = new List<Expression> { new IdentifierExpr("shallow") };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType, predNames, preds);
        Assert.That(hints, Is.InstanceOf<ComparisonFilter>());
        var cf = (ComparisonFilter)hints!;
        Assert.That(cf.Property, Is.EqualTo("Depth"));
        Assert.That(cf.Op, Is.EqualTo(CompareOp.LessThan));
        Assert.That(cf.Value, Is.EqualTo(3));
    }

    [Test]
    public void InlinePredicate_StringOpBody()
    {
        // predicate csharp(TestItem) => TestItem.Extension:eq('.cs')
        var preds = new Dictionary<string, List<PredicateDefinition>>
        {
            ["csharp"] = [new PredicateDefinition("csharp", "TestItem", null,
                new PredicateCallExpr(
                    new MemberAccessExpr(new IdentifierExpr("TestItem"), "Extension"),
                    "equals", [new LiteralExpr(".cs")]), 1)]
        };
        var predNames = new HashSet<string> { "csharp" };

        var filters = new List<Expression> { new IdentifierExpr("csharp") };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType, predNames, preds);
        Assert.That(hints, Is.InstanceOf<StringOpFilter>());
        Assert.That(((StringOpFilter)hints!).Value, Is.EqualTo(".cs"));
    }

    [Test]
    public void InlinePredicate_NegatedComparisonBody()
    {
        // !shallow  where  predicate shallow(TestItem) => TestItem.Depth < 3
        var preds = new Dictionary<string, List<PredicateDefinition>>
        {
            ["shallow"] = [new PredicateDefinition("shallow", "TestItem", null,
                new BinaryExpr(
                    new MemberAccessExpr(new IdentifierExpr("TestItem"), "Depth"),
                    "<", new LiteralExpr(3)), 1)]
        };
        var predNames = new HashSet<string> { "shallow" };

        var filters = new List<Expression>
        {
            new UnaryExpr("!", new IdentifierExpr("shallow"))
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType, predNames, preds);
        Assert.That(hints, Is.InstanceOf<NotFilter>());
        Assert.That(((NotFilter)hints!).Inner, Is.InstanceOf<ComparisonFilter>());
    }

    [Test]
    public void InlinePredicate_AndBody()
    {
        // predicate smallShallow(TestItem) => TestItem.Size < 100 && TestItem.Depth < 3
        var preds = new Dictionary<string, List<PredicateDefinition>>
        {
            ["smallShallow"] = [new PredicateDefinition("smallShallow", "TestItem", null,
                new BinaryExpr(
                    new BinaryExpr(
                        new MemberAccessExpr(new IdentifierExpr("TestItem"), "Size"),
                        "<", new LiteralExpr(100)),
                    "&&",
                    new BinaryExpr(
                        new MemberAccessExpr(new IdentifierExpr("TestItem"), "Depth"),
                        "<", new LiteralExpr(3))), 1)]
        };
        var predNames = new HashSet<string> { "smallShallow" };

        var filters = new List<Expression> { new IdentifierExpr("smallShallow") };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType, predNames, preds);
        Assert.That(hints, Is.InstanceOf<AndFilter>());
        var af = (AndFilter)hints!;
        Assert.That(af.Conditions, Has.Count.EqualTo(2));
        Assert.That(af.Conditions[0], Is.InstanceOf<ComparisonFilter>());
        Assert.That(af.Conditions[1], Is.InstanceOf<ComparisonFilter>());
    }

    // --- Edge cases ---

    [Test]
    public void NullItemType_ReturnsNull()
    {
        var filters = new List<Expression> { new IdentifierExpr("Public") };
        var (hints, idx) = FilterHintExtractor.Extract(filters, null);
        Assert.That(hints, Is.Null);
        Assert.That(idx, Is.EqualTo(0));
    }

    [Test]
    public void EmptyFilters_ReturnsNull()
    {
        var (hints, idx) = FilterHintExtractor.Extract([], FileType);
        Assert.That(hints, Is.Null);
        Assert.That(idx, Is.EqualTo(0));
    }

    [Test]
    public void StringProperty_AsBareIdentifier_ReturnsNull()
    {
        // Name used as bool filter — not pushdown-able for string property
        var filters = new List<Expression> { new IdentifierExpr("Name") };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.Null);
    }

    [Test]
    public void UnknownProperty_ReturnsNull()
    {
        var filters = new List<Expression>
        {
            new BinaryExpr(new IdentifierExpr("NonExistent"), "==", new LiteralExpr(1))
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.Null);
    }

    [Test]
    public void StringGreaterThan_NotPushdownable()
    {
        // Extension > '.cs' — comparison on string not supported
        var filters = new List<Expression>
        {
            new BinaryExpr(new IdentifierExpr("Extension"), ">", new LiteralExpr(".cs"))
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.Null);
    }

    // --- Method-style numeric predicates ---

    [TestCase("greaterThan", CompareOp.GreaterThan)]
    [TestCase("lessThan", CompareOp.LessThan)]
    [TestCase("greaterOrEqual", CompareOp.GreaterOrEqual)]
    [TestCase("lessOrEqual", CompareOp.LessOrEqual)]
    public void NumericPredicate_AllOps(string predName, CompareOp expectedOp)
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Size"), predName, [new LiteralExpr(100)])
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<ComparisonFilter>());
        var cf = (ComparisonFilter)hints!;
        Assert.That(cf.Property, Is.EqualTo("Size"));
        Assert.That(cf.Op, Is.EqualTo(expectedOp));
        Assert.That(cf.Value, Is.EqualTo(100));
    }

    // --- equals/eq on int ---

    [Test]
    public void NumericPredicate_Eq()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Depth"), "equals", [new LiteralExpr(0)])
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<ComparisonFilter>());
        Assert.That(((ComparisonFilter)hints!).Op, Is.EqualTo(CompareOp.Equals));
    }

    // --- ne on int ---

    [Test]
    public void NumericPredicate_Ne()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Size"), "notEquals", [new LiteralExpr(0)])
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<NotFilter>());
        Assert.That(((NotFilter)hints!).Inner, Is.InstanceOf<ComparisonFilter>());
    }

    // --- String predicates with short aliases ---

    [Test]
    public void StringPredicate_Eq()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Extension"), "equals", [new LiteralExpr(".cs")])
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<StringOpFilter>());
        var sf = (StringOpFilter)hints!;
        Assert.That(sf.Op, Is.EqualTo(StringOp.Equals));
        Assert.That(sf.Value, Is.EqualTo(".cs"));
    }

    [Test]
    public void StringPredicate_Ne()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Extension"), "notEquals", [new LiteralExpr(".cs")])
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<NotFilter>());
        Assert.That(((NotFilter)hints!).Inner, Is.InstanceOf<StringOpFilter>());
    }

    [Test]
    public void StringPredicate_Sw()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Name"), "startsWith", [new LiteralExpr("Client")])
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<StringOpFilter>());
        Assert.That(((StringOpFilter)hints!).Op, Is.EqualTo(StringOp.StartsWith));
    }

    [Test]
    public void StringPredicate_Ew()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Name"), "endsWith", [new LiteralExpr("Async")])
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<StringOpFilter>());
        Assert.That(((StringOpFilter)hints!).Op, Is.EqualTo(StringOp.EndsWith));
    }

    // --- Numeric predicate on string property → null (type mismatch) ---

    [Test]
    public void NumericPredicateOnString_ReturnsNull()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Extension"), "greaterThan", [new LiteralExpr(100)])
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.Null);
    }

    // --- String predicate on int property → null (type mismatch) ---

    [Test]
    public void StringPredicateOnInt_ReturnsNull()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Size"), "startsWith", [new LiteralExpr("abc")])
        };
        var (hints, _) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.Null);
    }

    // --- Combined chain: DiskFiles:Depth:lt(3):Size:gt(100):Extension:eq('.cs') ---

    [Test]
    public void CombinedChain_MultiplePredicateTypes()
    {
        var filters = new List<Expression>
        {
            new PredicateCallExpr(new IdentifierExpr("Depth"), "lessThan", [new LiteralExpr(3)]),
            new PredicateCallExpr(new IdentifierExpr("Size"), "greaterThan", [new LiteralExpr(100)]),
            new PredicateCallExpr(new IdentifierExpr("Extension"), "equals", [new LiteralExpr(".cs")])
        };
        var (hints, idx) = FilterHintExtractor.Extract(filters, FileType);
        Assert.That(hints, Is.InstanceOf<AndFilter>());
        var af = (AndFilter)hints!;
        Assert.That(af.Conditions, Has.Count.EqualTo(3));
        Assert.That(af.Conditions[0], Is.InstanceOf<ComparisonFilter>());  // Depth < 3
        Assert.That(af.Conditions[1], Is.InstanceOf<ComparisonFilter>());  // Size > 100
        Assert.That(af.Conditions[2], Is.InstanceOf<StringOpFilter>());    // Extension == '.cs'
        Assert.That(idx, Is.EqualTo(3));
    }
}
