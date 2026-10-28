using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class CSharpSemanticFactsTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-semantic-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        ProviderErrors.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        ProviderErrors.Clear();
        Directory.Delete(_root, recursive: true);
    }

    private const string Caller = """
        using Library;
        class Caller {
            void Run() { Factory.Make().Execute(1); Other.Execute(1); Factory.Make().Execute("x"); }
        }
        """;

    private const string Dependency = """
        namespace Library;
        public class Base {
            public virtual int Execute(int value) => value;
            public string Execute(string value) => value;
        }
        public class Derived : Base {}
        public static class Factory { public static Derived Make() => new Derived(); }
        public static class Other { public static bool Execute(int value) => true; }
        """;

    [Test]
    public void PublicQueries_ResolveFactoryReceiver_SelectedOverloadAndUnrelatedTarget_ColdAndWarm()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), Caller);
        File.WriteAllText(Path.Combine(_root, "b.cs"), Dependency);
        for (int run = 0; run < 2; run++)
        {
            var calls = Calls().Where(c => Text(c, "MemberName") == "Execute").ToArray();
            Assert.That(calls, Has.Length.EqualTo(3));
            Assert.That(calls.Select(c => Text(Object(c, "ReferencedCallable"), "Signature")),
                Is.EqualTo(new[] { "Library.Base.Execute(int)", "Library.Other.Execute(int)", "Library.Base.Execute(string)" }));
            Assert.That(Text(Object(calls[0], "ReceiverType"), "Name"), Is.EqualTo("Derived"));
            Assert.That(Text(Object(Object(calls[0], "ReferencedCallable"), "ReturnType"), "Name"), Is.EqualTo("Int32"));
            Assert.That(Text(Object(calls[0], "ValueType"), "Name"), Is.EqualTo("Int32"));
            Assert.That(Text(Object(calls[1], "ValueType"), "Name"), Is.EqualTo("Boolean"));
            Assert.That(Text(Object(calls[2], "ValueType"), "Name"), Is.EqualTo("String"));
            Assert.That(calls[1].GetField("ReceiverType"), Is.TypeOf<CopNull>(),
                "A static type qualification is not an expression receiver.");
            var selected = Object(calls[0], "ReferencedCallable");
            Assert.That(Text(selected, "Name"), Is.EqualTo("Execute"));
            Assert.That(Text(selected, "DeclaringType"), Is.EqualTo("Base"));
            Assert.That(Text(selected, "Namespace"), Is.EqualTo("Library"));
            Assert.That(Path.GetFullPath(Text(selected, "SourceFile")), Is.EqualTo(Path.Combine(_root, "b.cs")));
            Assert.That(Text(selected, "Line"), Is.EqualTo("3"));
            Assert.That(Text(selected, "Column"), Is.EqualTo("24"));
            Assert.That(Text(selected, "EndColumn"), Is.EqualTo("31"));
            Assert.That(Items(selected.GetField("Parameters")).Select(p => Text((CopDynamicObject)p, "Name")),
                Is.EqualTo(new[] { "Int32" }));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
    }

    [Test]
    public void DependencyEdit_RebindsUnchangedCallerAfterWarmSyntaxSnapshot()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), Caller);
        var dependency = Path.Combine(_root, "b.cs");
        File.WriteAllText(dependency, Dependency);
        string[] Values() => Calls().Where(c => Text(c, "MemberName") == "Execute")
            .Select(c => Text(Object(c, "ValueType"), "Name")).ToArray();
        Assert.That(Values(), Is.EqualTo(new[] { "Int32", "Boolean", "String" }));
        Assert.That(Values(), Is.EqualTo(new[] { "Int32", "Boolean", "String" }));
        var unchanged = File.GetLastWriteTimeUtc(Path.Combine(_root, "a.cs"));
        File.WriteAllText(dependency, Dependency.Replace("virtual int Execute", "virtual long Execute"));
        Assert.That(Values(), Is.EqualTo(new[] { "Int64", "Boolean", "String" }));
        Assert.That(File.GetLastWriteTimeUtc(Path.Combine(_root, "a.cs")), Is.EqualTo(unchanged));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void CopConsumer_ExecutesTheCommonSemanticSurface()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), Caller);
        File.WriteAllText(Path.Combine(_root, "b.cs"), Dependency);
        var program = Path.Combine(_root, "program");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "main.cop"), """
            import code
            import csharp
            predicate isExecute(Statement) => Statement.MemberName == 'Execute'
            let cb = csharp.parse()
            command MAIN = foreach cb.Calls:isExecute => '{item.ReferencedCallable.Signature}|{item.ReceiverType.Name}|{item.ValueType.Name}'
            """);
        var repository = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(repository, "cop.sln")))
            repository = Path.GetDirectoryName(repository) ?? throw new AssertionException("Cannot locate package source.");
        var result = Engine.Run(program, _root, additionalFeedPaths: [Path.Combine(repository, "packages")]);
        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(new[]
        {
            "Library.Base.Execute(int)|Derived|Int32",
            "Library.Other.Execute(int)|null|Boolean",
            "Library.Base.Execute(string)|Derived|String"
        }));
    }

    [Test]
    public void StructuralFirstAndTake_DoNotReadLaterFilesOrBindMissingContext()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "class First { void Run() { ping(); ping(); } }");
        var later = Path.Combine(_root, "b.cs");
        File.WriteAllText(later, "class Later {}");
        using var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var service = Service();
        var types = service.QueryProvider("csharp", new() { RootPath = _root, Collection = "Types" });
        Assert.That(Text((CopDynamicObject)Items(types).First(), "Name"), Is.EqualTo("First"));
        var calls = Calls(service).Take(1).ToArray();
        Assert.That(calls.Select(c => Text(c, "MemberName")), Is.EqualTo(new[] { "ping" }));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void MissingContext_IsReportedOnlyOnSemanticDemand_AndNeverFabricatesSelectedTarget()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "class Caller { void Run() { Missing.Factory().Execute(1); } }");
        var call = Calls().Single(c => Text(c, "MemberName") == "Execute");
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(call.GetField("ReferencedCallable"), Is.TypeOf<CopNull>());
        Assert.That(call.GetField("ValueType"), Is.TypeOf<CopNull>());
        Assert.That(call.GetField("ReceiverType"), Is.TypeOf<CopNull>());
        var errors = ProviderErrors.Drain();
        Assert.That(errors.Any(e => e.Contains("a.cs") && e.Contains("Missing") && e.Contains("CS0103")), Is.True,
            string.Join(System.Environment.NewLine, errors));
        Assert.That(call.GetField("ReferencedCallable"), Is.TypeOf<CopNull>());
        Assert.That(ProviderErrors.Drain(), Is.Empty, "One file binding demand must not repeat diagnostics.");
    }

    private ProviderQueryService Service()
    {
        var provider = new CSharpProvider();
        var service = new ProviderQueryService(_root);
        service.RegisterProvider("csharp", provider, ProviderSchema.FromJson(provider.GetSchema()));
        return service;
    }

    private IEnumerable<CopDynamicObject> Calls(ProviderQueryService? service = null) =>
        Items((service ?? Service()).QueryProvider("csharp", new() { RootPath = _root, Collection = "Calls" }))
            .Cast<CopDynamicObject>();

    private static string Text(CopDynamicObject item, string field) => item.GetField(field).Display();
    private static CopDynamicObject Object(CopDynamicObject item, string field)
    {
        var value = item.GetField(field);
        Assert.That(value, Is.TypeOf<CopDynamicObject>(), $"{field} must have an exact bound value.");
        return (CopDynamicObject)value;
    }

    private static IEnumerable<CopValue> Items(CopValue value) => value switch
    {
        CopList list => list.Items,
        CopLazyCollection lazy => lazy.Enumerate(),
        CopQueryable query => query.Enumerate(),
        _ => throw new AssertionException($"Expected collection, got {value.GetType().Name}")
    };
}
