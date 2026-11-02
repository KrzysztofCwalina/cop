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
        ProviderWarnings.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        ProviderErrors.Clear();
        ProviderWarnings.Clear();
        Directory.Delete(_root, recursive: true);
    }

    [TestCase("<PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup>",
        "multiple target frameworks (<TargetFrameworks>)")]
    [TestCase("<Import Project=\"custom.props\" />", "imported build files (<Import>)")]
    [TestCase("<PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\"><DefineConstants>DEBUG</DefineConstants></PropertyGroup>",
        "conditional build settings (Condition attributes)")]
    [TestCase("<ItemGroup><Compile Include=\"$(GeneratedDirectory)\\*.cs\" /></ItemGroup>",
        "source-file rules with unevaluated expressions (<Compile>)")]
    [TestCase("<Target Name=\"Generate\"><ItemGroup><Compile Include=\"Generated.cs\" /></ItemGroup></Target>",
        "build-generated source-file rules (<Target>/<Compile>)")]
    [TestCase("<Target Name=\"Configure\"><PropertyGroup><DefineConstants>FEATURE</DefineConstants></PropertyGroup></Target>",
        "build-generated analysis settings (<Target>)")]
    [TestCase("<PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup>" +
        "<Import Project=\"custom.props\" Condition=\"'$(Configuration)' == 'Debug'\" />" +
        "<ItemGroup><Compile Remove=\"Excluded.cs\" /></ItemGroup>",
        "multiple target frameworks (<TargetFrameworks>), imported build files (<Import>), " +
        "conditional build settings (Condition attributes)")]
    public void BuildSettingsWarningExplainsTheDetectedSettingsImpactAndAction(string settings, string reason)
    {
        var project = Path.Combine(_root, "App.csproj");
        File.WriteAllText(project, $"<Project>{settings}</Project>");
        File.WriteAllText(Path.Combine(_root, "App.cs"), "class App { int Value() => 1; int Run() => Value(); }");

        var call = Calls().Single();
        Assert.That(Text(Object(call, "ValueType"), "Name"), Is.EqualTo("Int32"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(ProviderWarnings.Drain(), Is.EqualTo(new[]
        {
            $"Warning: csharp: Limited C# analysis for '{project}': found {reason}. " +
            "Cop cannot fully evaluate these settings. Source-file membership may fall back to project folders, " +
            "and only the first restored target framework is used. File inclusion or resolved symbols may differ from your actual build. " +
            "If a check depends on these settings, compare its results with the files and target framework selected by your normal build. " +
            "This is a Cop limitation, not a project error; installing the .NET SDK or running restore will not remove it."
        }));
    }

    [Test]
    public void InputStatisticsIncludeSourcesDemandedOnlyForSemanticContext()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "class Caller { string Run() => Target.Value(); }");
        File.WriteAllText(Path.Combine(_root, "z.cs"), "static class Target { public static string Value() => \"ok\"; }");
        for (int run = 0; run < 2; run++)
        {
            using var statistics = new ExecutionStatistics();
            var provider = new CSharpProvider();
            var result = (DeferredProviderResult)provider.QueryDeferred(new() { RootPath = _root, Collection = "Calls" });
            var call = result.Collections["Calls"].Cast<Cop.Providers.SourceModel.StatementInfo>().First();
            Assert.That(statistics.Snapshot().InputFiles, Is.EqualTo(1));
            Assert.That(call.ValueType?.OriginalText, Is.EqualTo("string"));
            Assert.That(statistics.Snapshot().InputFiles, Is.EqualTo(2));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
    }

    [Test]
    public void SimpleProjectDoesNotReportUnsupportedBuildSettings()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"),
            "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_root, "App.cs"), "class App { int Value() => 1; int Run() => Value(); }");

        Assert.That(Text(Object(Calls().Single(), "ValueType"), "Name"), Is.EqualTo("Int32"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
    }

    [Test]
    public void BuildOnlyConditionsDoNotProduceAnalysisWarnings()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <Target Name="Package" Condition="'$(Configuration)' == 'Release'">
                <ItemGroup>
                  <FrameworkMetadata Include="@(ReferencePath)" Condition="'%(ReferencePath.FrameworkReferenceName)' == 'Microsoft.NETCore.App'" />
                </ItemGroup>
                <Copy SourceFiles="@(FrameworkMetadata)" DestinationFolder="lib" Condition="Exists('lib')" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_root, "App.cs"), "class App { int Value() => 1; int Run() => Value(); }");

        Assert.That(Text(Object(Calls().Single(), "ValueType"), "Name"), Is.EqualTo("Int32"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
    }

    [Test]
    public void ExplicitCompileInputsBindLinkedSourcesWithoutExcludedDuplicatesOnColdAndWarmRuns()
    {
        var app = Path.Combine(_root, "App");
        var shared = Path.Combine(_root, "Shared");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(app, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
              <ItemGroup>
                <Compile Include="Caller.cs;..\Shared\**\*.cs" Exclude="..\Shared\Skip*.cs" />
                <Compile Remove="..\Shared\Removed.cs" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(app, "Caller.cs"), "class Caller { string Run() => Linked.Value(); }");
        var linked = Path.Combine(shared, "Linked.cs");
        File.WriteAllText(linked, "static class Linked { public static string Value() => \"ok\"; }");
        foreach (var path in new[] { Path.Combine(app, "Extra.cs"), Path.Combine(shared, "Skipped.cs"), Path.Combine(shared, "Removed.cs") })
            File.WriteAllText(path, "static class Linked { public static int Value() => 1; }");

        for (int run = 0; run < 2; run++)
        {
            var service = Service();
            var call = Calls(service).Single();
            Assert.That(Text(Object(call, "ValueType"), "Name"), Is.EqualTo("String"));
            Assert.That(Path.GetFullPath(Text(Object(call, "ReferencedCallable"), "SourceFile")), Is.EqualTo(linked));
            var files = Items(service.QueryProvider("csharp", new() { RootPath = _root, Collection = "Files" }))
                .Cast<CopDynamicObject>().ToDictionary(file => Text(file, "Path"));
            Assert.That(Items(files["App/Caller.cs"].GetField("Projects")).Select(value => value.Display()), Is.EqualTo(new[] { "App" }));
            Assert.That(Items(files["Shared/Linked.cs"].GetField("Projects")).Select(value => value.Display()), Is.EqualTo(new[] { "App" }));
            foreach (var path in new[] { "App/Extra.cs", "Shared/Skipped.cs", "Shared/Removed.cs" })
                Assert.That(Items(files[path].GetField("Projects")), Is.Empty);
            Assert.That(ProviderErrors.Drain(), Is.Empty);
            Assert.That(ProviderWarnings.Drain(), Is.Empty);
        }

        var eager = (Dictionary<string, List<object>>)new CSharpProvider().Query(new()
            { RootPath = _root, Collection = "Files" })!;
        var eagerFiles = eager["Files"].Cast<Cop.Providers.SourceModel.SourceFile>().ToDictionary(file => file.Path);
        Assert.That(eagerFiles["App/Caller.cs"].Projects, Is.EqualTo(new[] { "App" }));
        Assert.That(eagerFiles["Shared/Linked.cs"].Projects, Is.EqualTo(new[] { "App" }));
        foreach (var path in new[] { "App/Extra.cs", "Shared/Skipped.cs", "Shared/Removed.cs" })
            Assert.That(eagerFiles[path].Projects, Is.Empty);
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
    }

    [Test]
    public void ProjectReferencesCompileALibraryWhoseOnlySourceIsLinked()
    {
        foreach (var directory in new[] { "App", "Library", "Shared" })
            Directory.CreateDirectory(Path.Combine(_root, directory));
        File.WriteAllText(Path.Combine(_root, "App", "App.csproj"),
            "<Project><ItemGroup><ProjectReference Include=\"..\\Library\\Library.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(_root, "Library", "Library.csproj"),
            "<Project><ItemGroup><Compile Include=\"..\\Shared\\Library.cs\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(_root, "App", "Caller.cs"), "class Caller { string Run() => Library.Value(); }");
        var linked = Path.Combine(_root, "Shared", "Library.cs");
        File.WriteAllText(linked, "public static class Library { public static string Value() => \"ok\"; }");

        Assert.That(Text(Object(Calls().Single(), "ValueType"), "Name"), Is.EqualTo("String"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
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
