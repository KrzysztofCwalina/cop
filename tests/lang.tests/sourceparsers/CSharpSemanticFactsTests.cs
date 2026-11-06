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
    public async Task BuildSettingsWarningExplainsTheDetectedSettingsImpactAndAction(string settings, string reason)
    {
        var project = Path.Combine(_root, "App.csproj");
        File.WriteAllText(project, $"<Project>{settings}</Project>");
        File.WriteAllText(Path.Combine(_root, "App.cs"), "class App { int Value() => 1; int Run() => Value(); }");

        var call = (await (await CallsAsync(cancellationToken: CancellationToken.None)).SingleAsync());
        Assert.That((await TextAsync((await ObjectAsync(call, "ValueType", cancellationToken: CancellationToken.None)), "Name", cancellationToken: CancellationToken.None)), Is.EqualTo("Int32"));
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
    public async Task InputStatisticsIncludeSourcesDemandedOnlyForSemanticContext()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "class Caller { string Run() => Target.Value(); }");
        File.WriteAllText(Path.Combine(_root, "z.cs"), "static class Target { public static string Value() => \"ok\"; }");
        for (int run = 0; run < 2; run++)
        {
            using var statistics = new ExecutionStatistics();
            var provider = new CSharpProvider();
            var result = (DeferredProviderResult?)(await provider.QueryDeferredAsync(new() { RootPath = _root, Collection = "Calls" }, cancellationToken: CancellationToken.None))
                ?? throw new AssertionException("Expected a deferred provider result.");
            var call = (await result.Collections["Calls"].Cast<Cop.Providers.SourceModel.StatementInfo>().FirstAsync());
            Assert.That(statistics.Snapshot().InputFiles, Is.EqualTo(1));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType?.OriginalText, Is.EqualTo("string"));
            Assert.That(statistics.Snapshot().InputFiles, Is.EqualTo(2));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
    }

    [Test]
    public async Task SimpleProjectDoesNotReportUnsupportedBuildSettings()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"),
            "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_root, "App.cs"), "class App { int Value() => 1; int Run() => Value(); }");

        Assert.That((await TextAsync((await ObjectAsync((await (await CallsAsync(cancellationToken: CancellationToken.None)).SingleAsync()), "ValueType", cancellationToken: CancellationToken.None)), "Name", cancellationToken: CancellationToken.None)), Is.EqualTo("Int32"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
    }

    [Test]
    public async Task BuildOnlyConditionsDoNotProduceAnalysisWarnings()
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

        Assert.That((await TextAsync((await ObjectAsync((await (await CallsAsync(cancellationToken: CancellationToken.None)).SingleAsync()), "ValueType", cancellationToken: CancellationToken.None)), "Name", cancellationToken: CancellationToken.None)), Is.EqualTo("Int32"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
    }

    [Test]
    public async Task ExplicitCompileInputsBindLinkedSourcesWithoutExcludedDuplicatesOnColdAndWarmRuns()
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
            var call = (await (await CallsAsync(service, cancellationToken: CancellationToken.None)).SingleAsync());
            Assert.That((await TextAsync((await ObjectAsync(call, "ValueType", cancellationToken: CancellationToken.None)), "Name", cancellationToken: CancellationToken.None)), Is.EqualTo("String"));
            Assert.That(Path.GetFullPath((await TextAsync((await ObjectAsync(call, "ReferencedCallable", cancellationToken: CancellationToken.None)), "SourceFile", cancellationToken: CancellationToken.None))), Is.EqualTo(linked));
            var files = new Dictionary<string, CopDynamicObject>();
            await foreach (var file in Items(await service.QueryProviderAsync("csharp",
                new() { RootPath = _root, Collection = "Files" })).Cast<CopDynamicObject>())
                files.Add(await TextAsync(file, "Path"), file);
            Assert.That((await Items((await files["App/Caller.cs"].GetFieldAsync("Projects", cancellationToken: CancellationToken.None))).Select(async (CopValue value, CancellationToken asyncCancellationToken7925) => await value.DisplayAsync(cancellationToken: asyncCancellationToken7925)).ToArrayAsync()), Is.EqualTo(new[] { "App" }));
            Assert.That((await Items((await files["Shared/Linked.cs"].GetFieldAsync("Projects", cancellationToken: CancellationToken.None))).Select(async (CopValue value, CancellationToken asyncCancellationToken8068) => await value.DisplayAsync(cancellationToken: asyncCancellationToken8068)).ToArrayAsync()), Is.EqualTo(new[] { "App" }));
            foreach (var path in new[] { "App/Extra.cs", "Shared/Skipped.cs", "Shared/Removed.cs" })
                Assert.That((await Items((await files[path].GetFieldAsync("Projects", cancellationToken: CancellationToken.None))).ToArrayAsync()), Is.Empty);
            Assert.That(ProviderErrors.Drain(), Is.Empty);
            Assert.That(ProviderWarnings.Drain(), Is.Empty);
        }

        var eager = (Dictionary<string, List<object>>)(await new CSharpProvider().QueryAsync(new()
        { RootPath = _root, Collection = "Files" }, cancellationToken: CancellationToken.None))!;
        var eagerFiles = eager["Files"].Cast<Cop.Providers.SourceModel.SourceFile>().ToDictionary(file => file.Path);
        Assert.That((await AsyncSourceFacts.ProjectsAsync(eagerFiles["App/Caller.cs"], CancellationToken.None)).Projects, Is.EqualTo(new[] { "App" }));
        Assert.That((await AsyncSourceFacts.ProjectsAsync(eagerFiles["Shared/Linked.cs"], CancellationToken.None)).Projects, Is.EqualTo(new[] { "App" }));
        foreach (var path in new[] { "App/Extra.cs", "Shared/Skipped.cs", "Shared/Removed.cs" })
            Assert.That((await AsyncSourceFacts.ProjectsAsync(eagerFiles[path], CancellationToken.None)).Projects, Is.Empty);
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
    }

    [Test]
    public async Task ProjectReferencesCompileALibraryWhoseOnlySourceIsLinked()
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

        Assert.That((await TextAsync((await ObjectAsync((await (await CallsAsync(cancellationToken: CancellationToken.None)).SingleAsync()), "ValueType", cancellationToken: CancellationToken.None)), "Name", cancellationToken: CancellationToken.None)), Is.EqualTo("String"));
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
    public async Task PublicQueries_ResolveFactoryReceiver_SelectedOverloadAndUnrelatedTarget_ColdAndWarm()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), Caller);
        File.WriteAllText(Path.Combine(_root, "b.cs"), Dependency);
        for (int run = 0; run < 2; run++)
        {
            var calls = (await (await CallsAsync(cancellationToken: CancellationToken.None)).Where(async (CopDynamicObject c, CancellationToken asyncCancellationToken11329) => (await TextAsync(c, "MemberName", cancellationToken: asyncCancellationToken11329)) == "Execute").ToArrayAsync());
            Assert.That(calls, Has.Length.EqualTo(3));
            Assert.That((await calls.ToAsyncEnumerable().Select(async (CopDynamicObject c, CancellationToken asyncCancellationToken11475) => await TextAsync((await ObjectAsync(c, "ReferencedCallable", cancellationToken: asyncCancellationToken11475)), "Signature", cancellationToken: asyncCancellationToken11475)).ToArrayAsync()),
                Is.EqualTo(new[] { "Library.Base.Execute(int)", "Library.Other.Execute(int)", "Library.Base.Execute(string)" }));
            Assert.That((await TextAsync((await ObjectAsync(calls[0], "ReceiverType", cancellationToken: CancellationToken.None)), "Name", cancellationToken: CancellationToken.None)), Is.EqualTo("Derived"));
            Assert.That((await TextAsync((await ObjectAsync((await ObjectAsync(calls[0], "ReferencedCallable", cancellationToken: CancellationToken.None)), "ReturnType", cancellationToken: CancellationToken.None)), "Name", cancellationToken: CancellationToken.None)), Is.EqualTo("Int32"));
            Assert.That((await TextAsync((await ObjectAsync(calls[0], "ValueType", cancellationToken: CancellationToken.None)), "Name", cancellationToken: CancellationToken.None)), Is.EqualTo("Int32"));
            Assert.That((await TextAsync((await ObjectAsync(calls[1], "ValueType", cancellationToken: CancellationToken.None)), "Name", cancellationToken: CancellationToken.None)), Is.EqualTo("Boolean"));
            Assert.That((await TextAsync((await ObjectAsync(calls[2], "ValueType", cancellationToken: CancellationToken.None)), "Name", cancellationToken: CancellationToken.None)), Is.EqualTo("String"));
            Assert.That((await calls[1].GetFieldAsync("ReceiverType", cancellationToken: CancellationToken.None)), Is.TypeOf<CopNull>(),
                "A static type qualification is not an expression receiver.");
            var selected = (await ObjectAsync(calls[0], "ReferencedCallable", cancellationToken: CancellationToken.None));
            Assert.That((await TextAsync(selected, "Name", cancellationToken: CancellationToken.None)), Is.EqualTo("Execute"));
            Assert.That((await TextAsync(selected, "DeclaringType", cancellationToken: CancellationToken.None)), Is.EqualTo("Base"));
            Assert.That((await TextAsync(selected, "Namespace", cancellationToken: CancellationToken.None)), Is.EqualTo("Library"));
            Assert.That(Path.GetFullPath((await TextAsync(selected, "SourceFile", cancellationToken: CancellationToken.None))), Is.EqualTo(Path.Combine(_root, "b.cs")));
            Assert.That((await TextAsync(selected, "Line", cancellationToken: CancellationToken.None)), Is.EqualTo("3"));
            Assert.That((await TextAsync(selected, "Column", cancellationToken: CancellationToken.None)), Is.EqualTo("24"));
            Assert.That((await TextAsync(selected, "EndColumn", cancellationToken: CancellationToken.None)), Is.EqualTo("31"));
            Assert.That((await Items((await selected.GetFieldAsync("Parameters", cancellationToken: CancellationToken.None))).Select(async (CopValue p, CancellationToken asyncCancellationToken13019) => await TextAsync((CopDynamicObject)p, "Name", cancellationToken: asyncCancellationToken13019)).ToArrayAsync()),
                Is.EqualTo(new[] { "Int32" }));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
    }

    [Test]
    public async Task DependencyEdit_RebindsUnchangedCallerAfterWarmSyntaxSnapshot()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), Caller);
        var dependency = Path.Combine(_root, "b.cs");
        File.WriteAllText(dependency, Dependency);
        async Task<string[]> ValuesAsync(CancellationToken cancellationToken = default) => (await (await CallsAsync(cancellationToken: cancellationToken)).Where(async (Cop.Lang.Interpreter.CopDynamicObject c, CancellationToken asyncCancellationToken13504) => (await TextAsync(c, "MemberName", cancellationToken: asyncCancellationToken13504)) == "Execute")
            .Select(async (CopDynamicObject c, CancellationToken asyncCancellationToken13566) => await TextAsync((await ObjectAsync(c, "ValueType", cancellationToken: asyncCancellationToken13566)), "Name", cancellationToken: asyncCancellationToken13566)).ToArrayAsync());
        Assert.That((await ValuesAsync(cancellationToken: CancellationToken.None)), Is.EqualTo(new[] { "Int32", "Boolean", "String" }));
        Assert.That((await ValuesAsync(cancellationToken: CancellationToken.None)), Is.EqualTo(new[] { "Int32", "Boolean", "String" }));
        var unchanged = File.GetLastWriteTimeUtc(Path.Combine(_root, "a.cs"));
        File.WriteAllText(dependency, Dependency.Replace("virtual int Execute", "virtual long Execute"));
        Assert.That((await ValuesAsync(cancellationToken: CancellationToken.None)), Is.EqualTo(new[] { "Int64", "Boolean", "String" }));
        Assert.That(File.GetLastWriteTimeUtc(Path.Combine(_root, "a.cs")), Is.EqualTo(unchanged));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async Task CopConsumer_ExecutesTheCommonSemanticSurface()
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
        var result = (await Engine.RunAsync(program, _root, new EngineRunOptions
        {
            AdditionalFeedPaths = [Path.Combine(repository, "packages")],
        }));
        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(new[]
        {
            "Library.Base.Execute(int)|Derived|Int32",
            "Library.Other.Execute(int)|null|Boolean",
            "Library.Base.Execute(string)|Derived|String"
        }));
    }

    [Test]
    public async Task StructuralFirstAndTake_DoNotReadLaterFilesOrBindMissingContext()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "class First { void Run() { ping(); ping(); } }");
        var later = Path.Combine(_root, "b.cs");
        File.WriteAllText(later, "class Later {}");
        using var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var service = Service();
        var types = (await service.QueryProviderAsync("csharp", new() { RootPath = _root, Collection = "Types" }, cancellationToken: CancellationToken.None));
        Assert.That((await TextAsync((CopDynamicObject)(await Items(types).FirstAsync()), "Name", cancellationToken: CancellationToken.None)), Is.EqualTo("First"));
        var calls = (await (await CallsAsync(service, cancellationToken: CancellationToken.None)).Take(1).ToArrayAsync());
        Assert.That((await calls.ToAsyncEnumerable().Select(async (Cop.Lang.Interpreter.CopDynamicObject c, CancellationToken asyncCancellationToken16489) => await TextAsync(c, "MemberName", cancellationToken: asyncCancellationToken16489)).ToArrayAsync()), Is.EqualTo(new[] { "ping" }));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async Task MissingContext_IsReportedOnlyOnSemanticDemand_AndNeverFabricatesSelectedTarget()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "class Caller { void Run() { Missing.Factory().Execute(1); } }");
        var call = (await (await CallsAsync(cancellationToken: CancellationToken.None)).SingleAsync(async (Cop.Lang.Interpreter.CopDynamicObject c, CancellationToken cancellationToken) => (await TextAsync(c, "MemberName", cancellationToken: CancellationToken.None)) == "Execute"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That((await call.GetFieldAsync("ReferencedCallable", cancellationToken: CancellationToken.None)), Is.TypeOf<CopNull>());
        Assert.That((await call.GetFieldAsync("ValueType", cancellationToken: CancellationToken.None)), Is.TypeOf<CopNull>());
        Assert.That((await call.GetFieldAsync("ReceiverType", cancellationToken: CancellationToken.None)), Is.TypeOf<CopNull>());
        var errors = ProviderErrors.Drain();
        Assert.That(errors.Any(e => e.Contains("a.cs") && e.Contains("Missing") && e.Contains("CS0103")), Is.True,
            string.Join(System.Environment.NewLine, errors));
        Assert.That((await call.GetFieldAsync("ReferencedCallable", cancellationToken: CancellationToken.None)), Is.TypeOf<CopNull>());
        Assert.That(ProviderErrors.Drain(), Is.Empty, "One file binding demand must not repeat diagnostics.");
    }

    private ProviderQueryService Service()
    {
        var provider = new CSharpProvider();
        var service = new ProviderQueryService(_root);
        service.RegisterProvider("csharp", provider, ProviderSchema.FromJson(provider.GetSchema()));
        return service;
    }

    private async Task<IAsyncEnumerable<CopDynamicObject>> CallsAsync(ProviderQueryService? service = null, CancellationToken cancellationToken = default) =>
        Items((await (service ?? Service()).QueryProviderAsync("csharp", new() { RootPath = _root, Collection = "Calls" }, cancellationToken: cancellationToken)))
            .Cast<CopDynamicObject>();

    private static async Task<string> TextAsync(CopDynamicObject item, string field, CancellationToken cancellationToken = default) => (await (await item.GetFieldAsync(field, cancellationToken: cancellationToken)).DisplayAsync(cancellationToken: cancellationToken));
    private static async Task<CopDynamicObject> ObjectAsync(CopDynamicObject item, string field, CancellationToken cancellationToken = default)
    {
        var value = (await item.GetFieldAsync(field, cancellationToken: cancellationToken));
        Assert.That(value, Is.TypeOf<CopDynamicObject>(), $"{field} must have an exact bound value.");
        return (CopDynamicObject)value;
    }

    private static IAsyncEnumerable<CopValue> Items(CopValue value) => value switch
    {
        CopList list => list.Items.ToAsyncEnumerable(),
        CopLazyCollection lazy => lazy.EnumerateAsync(cancellationToken: CancellationToken.None),
        CopQueryable query => query.EnumerateAsync(cancellationToken: CancellationToken.None),
        _ => throw new AssertionException($"Expected collection, got {value.GetType().Name}")
    };
}
