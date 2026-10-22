using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class ObjectLookupTests
{
    private static CopValue Evaluate(string expression, CopValue value)
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var evaluator = new Evaluator(ffi);
        evaluator.GlobalEnvironment.Define("value", value);
        evaluator.EvalModule(CopParser.Parse($"command MAIN = {expression}", "test.cop"));
        return evaluator.RunCommand("MAIN");
    }

    private static CopValue CreateObject(string kind) => kind switch
    {
        "literal" => new CopObject(new Dictionary<string, CopValue>
        {
            ["Name"] = new CopString("Ada"),
            ["Empty"] = CopNull.Instance,
            ["Count"] = new CopInt(0),
            ["Enabled"] = CopBool.False
        }),
        "data" => new CopDynamicObject(new DataObject("Fixture", new Dictionary<string, object?>
        {
            ["Name"] = "Ada", ["Empty"] = null, ["Count"] = 0, ["Enabled"] = false
        }), DataObjectAdapter.Instance),
        "record" => new CopDynamicObject(
            new RecordView(new DataTable([3, 0, 0, 0], 4, 1, "Fixture", "Ada"u8.ToArray()), 0),
            new RecordViewAdapter([
                new() { Name = "Name", Type = "string" },
                new() { Name = "Empty", Type = "string" },
                new() { Name = "Count", Type = "int" },
                new() { Name = "Enabled", Type = "bool" }
            ])),
        "clr" => new CopDynamicObject(new object(), new ClrObjectAdapter(
            new Dictionary<string, Func<object, object?>>
            {
                ["Name"] = _ => "Ada", ["Empty"] = _ => null,
                ["Count"] = _ => 0, ["Enabled"] = _ => false
            })),
        _ => throw new ArgumentException($"Unknown fixture kind '{kind}'.", nameof(kind))
    };

    [TestCase("literal")]
    [TestCase("data")]
    [TestCase("record")]
    [TestCase("clr")]
    public void Get_RespectsExplicitCaseSensitivity(string kind)
    {
        var value = CreateObject(kind);
        Assert.That(Evaluate("value.Get('name')", value).Display(), Is.EqualTo("Ada"));
        Assert.That(Evaluate("value.Get('name', false)", value).Display(), Is.EqualTo("Ada"));
        Assert.That(Evaluate("value.Get('Name', true)", value).Display(), Is.EqualTo("Ada"));
        Assert.That(Evaluate("value.Get('name', true)", value), Is.SameAs(CopNull.Instance));
        Assert.That(Evaluate("value.Get('Missing', false)", value), Is.SameAs(CopNull.Instance));
    }

    [TestCase("literal")]
    [TestCase("data")]
    [TestCase("record")]
    [TestCase("clr")]
    public void ContainsKey_UsesExistenceRegardlessOfValue(string kind)
    {
        var value = CreateObject(kind);
        foreach (var key in new[] { "Name", "Empty", "Count", "Enabled", "empty" })
            Assert.That(Evaluate($"value:containsKey('{key}')", value), Is.EqualTo(CopBool.True), key);
        Assert.That(Evaluate("value:containsKey('Missing')", value), Is.EqualTo(CopBool.False));
    }

    [Test]
    public void ContainsKey_DoesNotEvaluatePropertyGetter()
    {
        var value = new CopDynamicObject(new object(), new ClrObjectAdapter(
            new Dictionary<string, Func<object, object?>>
            {
                ["Value"] = _ => throw new InvalidOperationException("Getter must not run.")
            }));

        Assert.That(Evaluate("value:containsKey('Value')", value), Is.EqualTo(CopBool.True));
    }

    [Test]
    public void Get_RejectsNonBooleanCaseSensitivity()
    {
        Assert.Throws<CopEvaluationException>(() =>
            Evaluate("value.Get('Name', 'true')", CreateObject("literal")));
    }
}
