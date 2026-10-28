using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class ProviderWorkDeferralTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-provider-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        ProviderErrors.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        ProviderErrors.Clear();
        Directory.Delete(_root, true);
    }

    [TestCase("csharp", ".cs", "public class Alpha {}", "public class Beta {}")]
    [TestCase("python", ".py", "class Alpha:\n    pass\n", "class Beta:\n    pass\n")]
    [TestCase("javascript", ".js", "class Alpha {}", "class Beta {}")]
    [TestCase("go", ".go", "package example\ntype Alpha struct {}", "package example\ntype Beta struct {}")]
    [TestCase("java", ".java", "class Alpha {}", "class Beta {}")]
    [TestCase("rust", ".rs", "pub struct Alpha {}", "pub struct Beta {}")]
    [TestCase("cop", ".cop", "type Alpha = { Name : string }", "type Beta = { Name : string }")]
    public void First_DoesNotReadLaterFiles_AndFullEnumerationCanResume(
        string language, string extension, string firstSource, string secondSource)
    {
        File.WriteAllText(Path.Combine(_root, "a" + extension), firstSource);
        var later = Path.Combine(_root, "b" + extension);
        File.WriteAllText(later, secondSource);
        var service = Service(language);
        CopValue result;
        using (var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = service.QueryProvider(language, new ProviderQuery { RootPath = _root, Collection = "Types" });
            Assert.That(Name(Items(result).First()), Is.EqualTo("Alpha"));
            Assert.That(ProviderErrors.Drain(), Is.Empty, "A later file must not even be opened by First.");
        }

        Assert.That(Items(result).Select(Name), Is.EqualTo(new[] { "Alpha", "Beta" }));
        File.Delete(later);
        Assert.That(Items(result).Select(Name), Is.EqualTo(new[] { "Alpha", "Beta" }),
            "Repeated enumeration must use the execution snapshot.");
    }

    [Test]
    public void CSharp_SyntaxTypeNames_DoNotDemandCompilationOrOtherFiles()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"),
            "class Alpha { void Run() { int count = 1; var client = new Beta(); } }");
        var later = Path.Combine(_root, "b.cs");
        File.WriteAllText(later, "class Beta {}");
        using var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var provider = new CSharpProvider();
        var result = (DeferredProviderResult)provider.QueryDeferred(
            new ProviderQuery { RootPath = _root, Collection = "Files" });
        var file = result.Collections["Files"].Cast<SourceFile>().First();
        Assert.That(file.Statements.Single(s => s.MemberName == "count").TypeName, Is.EqualTo("int"));
        Assert.That(file.Statements.Single(s => s.Kind == "call").TypeName, Is.EqualTo("Beta"));
        Assert.That(file.Types[0].DeferredSemantics!.IsValueCreated, Is.False);
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void SharedSnapshot_ReusesUnfilteredLineProjectionAcrossDifferentQueries()
    {
        File.WriteAllText(Path.Combine(_root, "a.fake"), "Alpha\n\nBeta");
        var later = Path.Combine(_root, "b.fake");
        File.WriteAllText(later, "Gamma");
        var parsers = new SourceParserRegistry();
        var parser = new CountingParser();
        parsers.Register(parser);
        var snapshot = new DeferredSourceSnapshot(parsers, new ProviderQuery { RootPath = _root });
        using var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var lines = snapshot.Query(new ProviderQuery { Collection = "Lines" }).Collections["Lines"]
            .Cast<LineInfo>().Take(3).ToArray();
        Assert.That(lines.Select(l => (l.Text, l.Number, l.Kind)),
            Is.EqualTo(new[] { ("Alpha", 1, "code"), ("", 2, "blank"), ("Beta", 3, "code") }));
        var filtered = snapshot.Query(new ProviderQuery
        {
            Collection = "Lines", Filter = new StringOpFilter("Kind", StringOp.ExactEquals, "blank")
        }).Collections["Lines"].Cast<LineInfo>().First();
        Assert.That(filtered, Is.SameAs(lines[1]));
        var repeated = snapshot.Query(new ProviderQuery { Collection = "Lines" }).Collections["Lines"]
            .Cast<LineInfo>().Take(3).ToArray();
        for (int i = 0; i < lines.Length; i++)
            Assert.That(repeated[i], Is.SameAs(lines[i]));
        Assert.That(parser.Reads, Is.EqualTo(new[] { "a.fake" }));
    }

    [Test]
    public void CSharp_SyntaxOnlyFilter_DoesNotConstructWholeCompilation()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "public class AlphaClient {}");
        var later = Path.Combine(_root, "b.cs");
        File.WriteAllText(later, "public interface IMarker {}");
        var service = Service("csharp");
        using var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = service.QueryProvider("csharp", new ProviderQuery
        {
            RootPath = _root, Collection = "Types",
            Filter = new AndFilter([
                new PropertyFilter("IsPublic", true),
                new StringOpFilter("Name", StringOp.EndsWith, "Client")])
        });

        Assert.That(Name(Items(result).First()), Is.EqualTo("AlphaClient"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void CSharp_SemanticField_StillResolvesInterfacesAcrossFiles()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "public class Alpha : Base {}");
        File.WriteAllText(Path.Combine(_root, "b.cs"), "public class Base : IMarker {}\npublic interface IMarker {}");
        var service = Service("csharp");
        var result = service.QueryProvider("csharp", new ProviderQuery
        {
            RootPath = _root, Collection = "Types",
            Filter = new StringOpFilter("Name", StringOp.ExactEquals, "Alpha")
        });
        var type = (CopDynamicObject)Items(result).First();
        Assert.That(Name(type), Is.EqualTo("Alpha"));
        Assert.That(Items(type.GetField("Interfaces")).Select(v => v.Display()),
            Is.EqualTo(new[] { "IMarker" }));
    }

    [Test]
    public void SharedSnapshot_ParsesOnlyDemandedFiles_AndReusesThemAcrossQueries()
    {
        for (int i = 0; i < 100; i++)
            File.WriteAllText(Path.Combine(_root, $"{i:D3}.fake"), $"Type{i:D3}");
        var parser = new CountingParser();
        var parsers = new SourceParserRegistry();
        parsers.Register(parser);
        var snapshot = new DeferredSourceSnapshot(parsers, new ProviderQuery { RootPath = _root });
        var types = snapshot.Query(new ProviderQuery { Collection = "Types" }).Collections["Types"];
        Assert.That(parser.Reads, Is.Empty);
        Assert.That(types.Take(2).Cast<TypeDeclaration>().Select(t => t.Name),
            Is.EqualTo(new[] { "Type000", "Type001" }));
        Assert.That(parser.Reads, Is.EqualTo(new[] { "000.fake", "001.fake" }));
        var files = snapshot.Query(new ProviderQuery { Collection = "Files" }).Collections["Files"];
        Assert.That(files.Take(2).Cast<SourceFile>().Select(f => f.Path),
            Is.EqualTo(new[] { "000.fake", "001.fake" }));
        Assert.That(parser.Reads, Has.Count.EqualTo(2));
        Assert.That(types.Cast<TypeDeclaration>().Last().Name, Is.EqualTo("Type099"));
        Assert.That(parser.Reads, Has.Count.EqualTo(100));
        Assert.That(types.Count(), Is.EqualTo(100));
        Assert.That(parser.Reads, Has.Count.EqualTo(100));
    }

    [Test]
    public void SharedSnapshot_DiskCacheReadsOnlyChangedFilesOnNextExecution()
    {
        File.WriteAllText(Path.Combine(_root, "a.fake"), "Alpha");
        var later = Path.Combine(_root, "b.fake");
        File.WriteAllText(later, "Beta");
        var parser = new CountingParser();
        var parsers = new SourceParserRegistry();
        parsers.Register(parser);
        var query = new ProviderQuery { RootPath = _root, Collection = "Types" };
        Assert.That(new DeferredSourceSnapshot(parsers, query).Query(query).Collections["Types"]
            .Cast<TypeDeclaration>().Select(t => t.Name), Is.EqualTo(new[] { "Alpha", "Beta" }));
        Assert.That(parser.Reads, Is.EqualTo(new[] { "a.fake", "b.fake" }));
        parser.Reads.Clear();
        File.WriteAllText(later, "BetaChanged");

        Assert.That(new DeferredSourceSnapshot(parsers, query).Query(query).Collections["Types"]
            .Cast<TypeDeclaration>().Select(t => t.Name), Is.EqualTo(new[] { "Alpha", "BetaChanged" }));
        Assert.That(parser.Reads, Is.EqualTo(new[] { "b.fake" }));
    }

    [Test]
    public void SharedSnapshot_BundledCache_KeepsUndemandedModelsLazy()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The bundled cache uses Windows change-time validation.");
        File.WriteAllText(Path.Combine(_root, "a.fake"), "Alpha");
        var later = Path.Combine(_root, "b.fake");
        File.WriteAllText(later, "Beta");
        var parser = new CountingParser();
        var parsers = new SourceParserRegistry();
        parsers.Register(parser);
        var query = new ProviderQuery { RootPath = _root, Collection = "Types" };
        Assert.That(new DeferredSourceSnapshot(parsers, query).Query(query).Collections["Types"]
            .Cast<TypeDeclaration>().Select(t => t.Name), Is.EqualTo(new[] { "Alpha", "Beta" }));
        parser.Reads.Clear();
        using var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var fresh = new DeferredSourceSnapshot(parsers, query);
        Assert.That(fresh.Query(query).Collections["Types"].Cast<TypeDeclaration>().First().Name,
            Is.EqualTo("Alpha"));
        Assert.That(parser.Reads, Is.Empty);
    }

    [TestCase("python", ".py", "class Alpha:\n    pass\n", "class Bravo:\n    pass\n")]
    [TestCase("javascript", ".js", "class Alpha {}", "class Bravo {}")]
    [TestCase("go", ".go", "package example\ntype Alpha struct {}", "package example\ntype Bravo struct {}")]
    [TestCase("java", ".java", "class Alpha {}", "class Bravo {}")]
    [TestCase("rust", ".rs", "pub struct Alpha {}", "pub struct Bravo {}")]
    [TestCase("cop", ".cop", "type Alpha = { Name : string }", "type Bravo = { Name : string }")]
    public void SourceCache_PreservesResultsAndDetectsSameSizeSameTimestampEdits(
        string language, string extension, string original, string edited)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The bundled cache uses Windows change-time validation.");
        var path = Path.Combine(_root, "a" + extension);
        File.WriteAllText(path, original);
        string[] ReadNames() => Items(Service(language).QueryProvider(language,
            new ProviderQuery { RootPath = _root, Collection = "Types" })).Select(Name).ToArray();
        Assert.That(ReadNames(), Is.EqualTo(new[] { "Alpha" }));
        Assert.That(ReadNames(), Is.EqualTo(new[] { "Alpha" }));
        var timestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, edited);
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.That(ReadNames(), Is.EqualTo(new[] { "Bravo" }));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void SharedSnapshot_BundledCache_IsolatesScopesAndTracksAddedAndDeletedFiles()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The bundled cache uses Windows change-time validation.");
        File.WriteAllText(Path.Combine(_root, "a.fake"), "Alpha");
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        var later = Path.Combine(nested, "b.fake");
        File.WriteAllText(later, "Beta");
        var parser = new CountingParser();
        var parsers = new SourceParserRegistry();
        parsers.Register(parser);
        string[] ReadNames(params string[] excluded)
        {
            var query = new ProviderQuery
            {
                RootPath = _root, Collection = "Types", ExcludedDirectories = new HashSet<string>(excluded)
            };
            return new DeferredSourceSnapshot(parsers, query).Query(query).Collections["Types"]
                .Cast<TypeDeclaration>().Select(t => t.Name).ToArray();
        }
        Assert.That(ReadNames(), Is.EqualTo(new[] { "Alpha", "Beta" }));
        Assert.That(ReadNames("nested"), Is.EqualTo(new[] { "Alpha" }));
        parser.Reads.Clear();
        Assert.That(ReadNames(), Is.EqualTo(new[] { "Alpha", "Beta" }));
        Assert.That(parser.Reads, Is.Empty);
        File.Delete(later);
        File.WriteAllText(Path.Combine(_root, "c.fake"), "Gamma");
        Assert.That(ReadNames(), Is.EqualTo(new[] { "Alpha", "Gamma" }));
        Assert.That(parser.Reads, Is.EqualTo(new[] { "c.fake" }));
        parser.Reads.Clear();
        Assert.That(ReadNames(), Is.EqualTo(new[] { "Alpha", "Gamma" }));
        Assert.That(parser.Reads, Is.Empty);
    }

    [Test]
    public void SharedSnapshot_BundledCache_ReportsTruncationAndRebuildsFromSource()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The bundled cache uses Windows change-time validation.");
        var path = Path.Combine(_root, "a.fake");
        File.WriteAllText(path, "Alpha");
        var scope = _root + "\nrecovery";
        var cachePath = CodeCollectionBuilder.GetCachePath(scope + "\ndeferred-bundle");
        var parser = new CountingParser();
        try
        {
            var original = new DeferredSourceCache(scope);
            Assert.That(original.Read(path, parser)!.Types.Single().Name, Is.EqualTo("Alpha"));
            original.Save([path]);
            File.WriteAllBytes(cachePath, [1, 0, 0, 0, 1, 0, 0, 0]);
            parser.Reads.Clear();
            var error = Console.Error;
            using var diagnostics = new StringWriter();
            var rebuilt = new DeferredSourceCache(scope);
            try
            {
                Console.SetError(diagnostics);
                Assert.That(rebuilt.Read(path, parser)!.Types.Single().Name, Is.EqualTo("Alpha"));
            }
            finally { Console.SetError(error); }
            Assert.That(diagnostics.ToString(), Does.Contain("Could not read deferred source cache"));
            Assert.That(parser.Reads, Is.EqualTo(new[] { "a.fake" }));
            rebuilt.Save([path]);
            parser.Reads.Clear();
            Assert.That(new DeferredSourceCache(scope).Read(path, parser)!.Types.Single().Name, Is.EqualTo("Alpha"));
            Assert.That(parser.Reads, Is.Empty);
        }
        finally { File.Delete(cachePath); }
    }

    [Test]
    public void Files_DeferProjectDiscovery_AndPreserveNestedMembership()
    {
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.fake"), "Alpha");
        var parsers = new SourceParserRegistry();
        parsers.Register(new CountingParser());
        int discoveries = 0;
        var snapshot = new DeferredSourceSnapshot(parsers, new ProviderQuery { RootPath = _root },
            discoverProjects: () =>
            {
                discoveries++;
                return [
                    new("Root", "root.csproj", "fixture", []),
                    new("Nested", Path.Combine("src", "nested.csproj"), "fixture", []),
                    new("Neighbor", Path.Combine("src2", "neighbor.csproj"), "fixture", [])
                ];
            });
        var file = snapshot.Query(new ProviderQuery { Collection = "Files" })
            .Collections["Files"].Cast<SourceFile>().Single();

        Assert.That(file.Path, Is.EqualTo("src/a.fake"));
        Assert.That(discoveries, Is.EqualTo(0), "Unrelated queries must not discover projects.");
        Assert.That(file.Projects, Is.EqualTo(new[] { "Root", "Nested" }));
        Assert.That(file.Projects, Is.EqualTo(new[] { "Root", "Nested" }));
        var projects = snapshot.Query(new ProviderQuery { Collection = "Projects" })
            .Collections["Projects"].Cast<ProjectInfo>().Select(p => p.Name).ToArray();
        Assert.That(projects, Is.EqualTo(new[] { "Root", "Nested", "Neighbor" }));
        Assert.That(discoveries, Is.EqualTo(1));
    }

    [Test]
    public void CSharp_TypeNameDemand_DoesNotForceInterfaceEnrichment()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"),
            "class Alpha : IMarker { void Run() { var x = new Beta(); x.Ping(); } }");
        File.WriteAllText(Path.Combine(_root, "b.cs"),
            "interface IMarker {} class Beta { public void Ping() {} }");
        var provider = new CSharpProvider();
        var files = (DeferredProviderResult)provider.QueryDeferred(
            new ProviderQuery { RootPath = _root, Collection = "Files" });
        var file = files.Collections["Files"].Cast<SourceFile>().First();
        var type = file.Types.Single();
        var call = file.Statements.Single(s => s.MemberName == "Ping");
        var declaration = file.Statements.Single(s => s.Kind == "declaration");

        Assert.That(call.TypeName, Is.EqualTo("Beta"));
        Assert.That(declaration.TypeName, Is.EqualTo("Beta"));
        Assert.That(type.DeferredSemantics!.IsValueCreated, Is.False,
            "Resolving call types must not also resolve every type's interfaces.");
        Assert.That(type.Interfaces, Is.EqualTo(new[] { "IMarker" }));
        Assert.That(call.TypeName, Is.EqualTo("Beta"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CSharp_TypeNameDemand_PreservesNullableInferenceAndOverloadResolution(bool interfacesFirst)
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), """
            #nullable enable
            class Client {
                public Client? Next() => null;
                public void Ping() {}
            }
            class Factory {
                public static T Identity<T>(T value) => value;
                public static Client? Choose(Client? value) => value;
                public static int Choose(int value) => value;
            }
            class Example {
                void Run(bool flag) {
                    Client? maybe = flag ? new Client() : null;
                    var inferred = maybe;
                    var present = maybe ?? new Client();
                    var chained = maybe?.Next();
                    var identified = Factory.Identity(maybe);
                    var chosen = Factory.Choose(maybe);
                    var number = Factory.Choose(1);
                    var nullableItems = new[] { maybe };
                    if (maybe != null) {
                        var narrowed = Factory.Identity(maybe);
                        var narrowedItems = new[] { maybe };
                        narrowed.Ping();
                    }
                    maybe?.Ping();
                }
            }
            """);
        var provider = new CSharpProvider();
        var result = (DeferredProviderResult)provider.QueryDeferred(
            new ProviderQuery { RootPath = _root, Collection = "Files" });
        var file = result.Collections["Files"].Cast<SourceFile>().Single();
        if (interfacesFirst)
            Assert.That(file.Types[0].Interfaces, Is.Empty);
        Assert.That(file.Statements.Where(s => s.Kind == "declaration")
                .Select(s => (s.MemberName, s.TypeName)),
            Is.EqualTo(new[] {
                ("maybe", "Client?"), ("inferred", "Client"), ("present", "Client"),
                ("chained", "Client"), ("identified", "Client"), ("chosen", "Client"),
                ("number", "int"), ("nullableItems", "Client?[]"),
                ("narrowed", "Client"), ("narrowedItems", "Client[]")
            }));
        Assert.That(file.Statements.Where(s => s.Kind == "call")
                .Select(s => (s.MemberName, s.TypeName)),
            Is.EqualTo(new (string?, string)[] {
                (null, "Client"), ("Identity", "Factory"), ("Choose", "Factory"),
                ("Choose", "Factory"), ("Identity", "Factory"), ("Ping", "Client"),
                (null, "Client")
            }));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void CSharp_TextCache_ReusesUnchangedSourcesAndInvalidatesEdits()
    {
        var path = Path.Combine(_root, "a.cs");
        File.WriteAllText(path, "public class Alpha {}");
        SourceFile Read()
        {
            var provider = new CSharpProvider();
            var result = (DeferredProviderResult)provider.QueryDeferred(
                new ProviderQuery { RootPath = _root, Collection = "Files" });
            return result.Collections["Files"].Cast<SourceFile>().Single();
        }

        Assert.That(Read().Types.Single().Name, Is.EqualTo("Alpha"));
        Assert.That(Read().Types.Single().Name, Is.EqualTo("Alpha"));
        File.WriteAllText(path, "public class BetaChanged {}");
        Assert.That(Read().Types.Single().Name, Is.EqualTo("BetaChanged"));
        File.WriteAllText(path, "public class SameLengthX {}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.That(Read().Types.Single().Name, Is.EqualTo("SameLengthX"));
        var timestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "public class SameLengthY {}");
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.That(Read().Types.Single().Name, Is.EqualTo("SameLengthY"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [TestCase("csharp", ".cs", "public class Alpha {}")]
    [TestCase("python", ".py", "class Alpha:\n    pass\n")]
    [TestCase("javascript", ".js", "class Alpha {}")]
    [TestCase("go", ".go", "package example\ntype Alpha struct {}")]
    [TestCase("java", ".java", "class Alpha {}")]
    [TestCase("rust", ".rs", "pub struct Alpha {}")]
    public void Projects_DoesNotOpenSourceFiles(string language, string extension, string source)
    {
        var path = Path.Combine(_root, "a" + extension);
        File.WriteAllText(path, source);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var service = Service(language);
        var projects = service.QueryProvider(language, new ProviderQuery { RootPath = _root, Collection = "Projects" });
        Assert.That(Items(projects).Count(), Is.EqualTo(0));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void DeferredFailure_IsReportedWhenDemanded_AndNotConvertedToEmptySuccess()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "public class Alpha {}");
        var later = Path.Combine(_root, "b.cs");
        File.WriteAllText(later, "public class Beta {}");
        var service = Service("csharp");
        var result = service.QueryProvider("csharp", new ProviderQuery { RootPath = _root, Collection = "Types" });
        using (var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.That(Name(Items(result).First()), Is.EqualTo("Alpha"));
            Assert.Throws<IOException>(() => Items(result).ToArray());
        }
        Assert.Throws<IOException>(() => Items(result).ToArray(),
            "An errored iterator must preserve the failure, not silently terminate on retry.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CSharp_DeferredSemantics_PreservesInferredAndInvocationTypes(bool warmCache)
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"),
            "public class Alpha { public void Run() { var x = new Beta(); x.Ping(); } }");
        File.WriteAllText(Path.Combine(_root, "b.cs"), "public class Beta { public void Ping() {} }");
        var service = Service("csharp");
        if (warmCache)
        {
            Assert.That(Items(service.QueryProvider("csharp",
                new ProviderQuery { RootPath = _root, Collection = "Files" })).Count(), Is.EqualTo(2));
            service = Service("csharp");
        }
        var result = service.QueryProvider("csharp", new ProviderQuery { RootPath = _root, Collection = "Statements" });
        var statements = Items(result).Cast<CopDynamicObject>().ToArray();
        var declaration = statements.Single(s => s.GetField("Kind").Display() == "declaration");
        var call = statements.Single(s => s.GetField("MemberName").Display() == "Ping");
        Assert.That(declaration.GetField("TypeName").Display(), Is.EqualTo("Beta"));
        Assert.That(call.GetField("TypeName").Display(), Is.EqualTo("Beta"));
    }

    [Test]
    public void DiskCache_ReconstructsUsingTheRequestingParserLoadContext()
    {
        var context = new System.Runtime.Loader.AssemblyLoadContext("provider-cache-test", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(CopSourceParser).Assembly.Location);
            var foreignParser = (ISourceParser)Activator.CreateInstance(
                assembly.GetType(typeof(CopSourceParser).FullName!)!)!;
            var localFile = new CopSourceParser().Parse("sample.cop", "function chosen() => 1")!;
            foreignParser.Parse("sample.cop", "function chosen() => 1");
            var path = Path.Combine(_root, "cache.bin");
            SourceCacheSerializer.Save(path, [1], [localFile]);
            Assert.That(SourceCacheSerializer.TryLoad(path, [1])![0].Methods.Single().GetType(),
                Is.EqualTo(typeof(CopFunctionDeclaration)));
            CopFunctionDeclaration.RegisterCacheFactory();
            Assert.That(SourceCacheSerializer.TryLoad(path, [1], assembly)![0].Methods.Single().GetType().Assembly,
                Is.SameAs(assembly));
        }
        finally { context.Unload(); }
    }

    private sealed class CountingParser : ISourceParser
    {
        public List<string> Reads { get; } = [];
        public override IReadOnlyList<string> Extensions => [".fake"];
        public override string Language => "fixture";
        public override SourceFile Parse(string filePath, string sourceText)
        {
            Reads.Add(Path.GetFileName(filePath));
            return new SourceFile(filePath, Language,
                [new TypeDeclaration(sourceText, TypeKind.Class, Modifier.Public, [], [], [], [], [], [], 1)],
                [], sourceText);
        }
    }

    private ProviderQueryService Service(string language)
    {
        DataProvider provider = language switch
        {
            "csharp" => new CSharpProvider(),
            "python" => new PythonProvider(),
            "javascript" => new JavaScriptProvider(),
            "go" => new GoProvider(),
            "java" => new JavaProvider(),
            "rust" => new RustProvider(),
            "cop" => new CopProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(language))
        };
        var service = new ProviderQueryService(_root);
        service.RegisterProvider(language, provider, ProviderSchema.FromJson(provider.GetSchema()));
        return service;
    }

    private static string Name(CopValue value) => ((CopDynamicObject)value).GetField("Name").Display();
    private static IEnumerable<CopValue> Items(CopValue value) => value switch
    {
        CopList list => list.Items,
        CopLazyCollection lazy => lazy.Enumerate(),
        CopQueryable query => query.Enumerate(),
        _ => throw new AssertionException($"Expected collection, got {value.GetType().Name}")
    };
}
