using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using System.Runtime.InteropServices;

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
    public async Task First_DoesNotReadLaterFiles_AndFullEnumerationCanResume(
        string language, string extension, string firstSource, string secondSource)
    {
        File.WriteAllText(Path.Combine(_root, "a" + extension), firstSource);
        var later = Path.Combine(_root, "b" + extension);
        File.WriteAllText(later, secondSource);
        var service = Service(language);
        CopValue result;
        using (var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = (await service.QueryProviderAsync(language, new ProviderQuery { RootPath = _root, Collection = "Types" }, cancellationToken: CancellationToken.None));
            Assert.That((await NameAsync((await Items(result).FirstAsync()), cancellationToken: CancellationToken.None)), Is.EqualTo("Alpha"));
            Assert.That(ProviderErrors.Drain(), Is.Empty, "A later file must not even be opened by First.");
        }

        Assert.That((await Items(result).Select(async (CopValue value, CancellationToken cancellationToken) => await NameAsync(value, cancellationToken)).ToArrayAsync()), Is.EqualTo(new[] { "Alpha", "Beta" }));
        File.Delete(later);
        Assert.That((await Items(result).Select(async (CopValue value, CancellationToken cancellationToken) => await NameAsync(value, cancellationToken)).ToArrayAsync()), Is.EqualTo(new[] { "Alpha", "Beta" }),
            "Repeated enumeration must use the execution snapshot.");
    }

    [Test]
    public async Task CSharp_SyntaxTypeNames_DoNotDemandCompilationOrOtherFiles()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"),
            "class Alpha { void Run() { int count = 1; var client = new Beta(); } }");
        var later = Path.Combine(_root, "b.cs");
        File.WriteAllText(later, "class Beta {}");
        using var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var provider = new CSharpProvider();
        var result = (DeferredProviderResult?)(await provider.QueryDeferredAsync(
            new ProviderQuery { RootPath = _root, Collection = "Files" }, cancellationToken: CancellationToken.None))
            ?? throw new AssertionException("Expected a deferred provider result.");
        var file = (await result.Collections["Files"].Cast<SourceFile>().FirstAsync());
        Assert.That((await AsyncSourceFacts.TypeNameAsync(file.Statements.Single(s => s.MemberName == "count"), CancellationToken.None)).TypeName, Is.EqualTo("int"));
        Assert.That((await AsyncSourceFacts.TypeNameAsync(file.Statements.Single(s => s.Kind == "call"), CancellationToken.None)).TypeName, Is.EqualTo("Beta"));
        Assert.That(file.Types[0].DeferredSemantics!.IsValueCreated, Is.False);
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async Task SharedSnapshot_ReusesUnfilteredLineProjectionAcrossDifferentQueries()
    {
        File.WriteAllText(Path.Combine(_root, "a.fake"), "Alpha\n\nBeta");
        var later = Path.Combine(_root, "b.fake");
        File.WriteAllText(later, "Gamma");
        var parsers = new SourceParserRegistry();
        var parser = new CountingParser();
        parsers.Register(parser);
        var snapshot = new DeferredSourceSnapshot(parsers, new ProviderQuery { RootPath = _root });
        using var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var lines = (await snapshot.Query(new ProviderQuery { Collection = "Lines" }).Collections["Lines"]
            .Cast<LineInfo>().Take(3).ToArrayAsync());
        Assert.That(lines.Select(l => (l.Text, l.Number, l.Kind)),
            Is.EqualTo(new[] { ("Alpha", 1, "code"), ("", 2, "blank"), ("Beta", 3, "code") }));
        var filtered = (await snapshot.Query(new ProviderQuery
        {
            Collection = "Lines", Filter = new StringOpFilter("Kind", StringOp.ExactEquals, "blank")
        }).Collections["Lines"].Cast<LineInfo>().FirstAsync());
        Assert.That(filtered, Is.SameAs(lines[1]));
        var repeated = (await snapshot.Query(new ProviderQuery { Collection = "Lines" }).Collections["Lines"]
            .Cast<LineInfo>().Take(3).ToArrayAsync());
        for (int i = 0; i < lines.Length; i++)
            Assert.That(repeated[i], Is.SameAs(lines[i]));
        Assert.That(parser.Reads, Is.EqualTo(new[] { "a.fake" }));
    }

    [Test]
    public async Task CSharp_SyntaxOnlyFilter_DoesNotConstructWholeCompilation()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "public class AlphaClient {}");
        var later = Path.Combine(_root, "b.cs");
        File.WriteAllText(later, "public interface IMarker {}");
        var service = Service("csharp");
        using var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = (await service.QueryProviderAsync("csharp", new ProviderQuery
        {
            RootPath = _root, Collection = "Types",
            Filter = new AndFilter([
                new PropertyFilter("IsPublic", true),
                new StringOpFilter("Name", StringOp.EndsWith, "Client")])
        }, cancellationToken: CancellationToken.None));

        Assert.That((await NameAsync((await Items(result).FirstAsync()), cancellationToken: CancellationToken.None)), Is.EqualTo("AlphaClient"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async Task CSharp_SemanticField_StillResolvesInterfacesAcrossFiles()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "public class Alpha : Base {}");
        File.WriteAllText(Path.Combine(_root, "b.cs"), "public class Base : IMarker {}\npublic interface IMarker {}");
        var service = Service("csharp");
        var result = (await service.QueryProviderAsync("csharp", new ProviderQuery
        {
            RootPath = _root, Collection = "Types",
            Filter = new StringOpFilter("Name", StringOp.ExactEquals, "Alpha")
        }, cancellationToken: CancellationToken.None));
        var type = (CopDynamicObject)(await Items(result).FirstAsync());
        Assert.That((await NameAsync(type, cancellationToken: CancellationToken.None)), Is.EqualTo("Alpha"));
        Assert.That((await Items((await type.GetFieldAsync("Interfaces", cancellationToken: CancellationToken.None))).Select(async (CopValue v, CancellationToken asyncCancellationToken6751) => await v.DisplayAsync(cancellationToken: asyncCancellationToken6751)).ToArrayAsync()),
            Is.EqualTo(new[] { "IMarker" }));
    }

    [Test]
    public async Task SharedSnapshot_ParsesOnlyDemandedFiles_AndReusesThemAcrossQueries()
    {
        for (int i = 0; i < 100; i++)
            File.WriteAllText(Path.Combine(_root, $"{i:D3}.fake"), $"Type{i:D3}");
        var parser = new CountingParser();
        var parsers = new SourceParserRegistry();
        parsers.Register(parser);
        var snapshot = new DeferredSourceSnapshot(parsers, new ProviderQuery { RootPath = _root });
        var types = snapshot.Query(new ProviderQuery { Collection = "Types" }).Collections["Types"];
        Assert.That(parser.Reads, Is.Empty);
        Assert.That((await types.Take(2).Cast<TypeDeclaration>().Select(t => t.Name).ToArrayAsync()),
            Is.EqualTo(new[] { "Type000", "Type001" }));
        Assert.That(parser.Reads, Is.EqualTo(new[] { "000.fake", "001.fake" }));
        var files = snapshot.Query(new ProviderQuery { Collection = "Files" }).Collections["Files"];
        Assert.That((await files.Take(2).Cast<SourceFile>().Select(f => f.Path).ToArrayAsync()),
            Is.EqualTo(new[] { "000.fake", "001.fake" }));
        Assert.That(parser.Reads, Has.Count.EqualTo(2));
        Assert.That((await types.Cast<TypeDeclaration>().LastAsync()).Name, Is.EqualTo("Type099"));
        Assert.That(parser.Reads, Has.Count.EqualTo(100));
        Assert.That((await types.CountAsync()), Is.EqualTo(100));
        Assert.That(parser.Reads, Has.Count.EqualTo(100));
    }

    [Test]
    public async Task SharedSnapshot_DiskCacheReadsOnlyChangedFilesOnNextExecution()
    {
        File.WriteAllText(Path.Combine(_root, "a.fake"), "Alpha");
        var later = Path.Combine(_root, "b.fake");
        File.WriteAllText(later, "Beta");
        var parser = new CountingParser();
        var parsers = new SourceParserRegistry();
        parsers.Register(parser);
        var query = new ProviderQuery { RootPath = _root, Collection = "Types" };
        Assert.That((await new DeferredSourceSnapshot(parsers, query).Query(query).Collections["Types"]
            .Cast<TypeDeclaration>().Select(t => t.Name).ToArrayAsync()), Is.EqualTo(new[] { "Alpha", "Beta" }));
        Assert.That(parser.Reads, Is.EqualTo(new[] { "a.fake", "b.fake" }));
        parser.Reads.Clear();
        File.WriteAllText(later, "BetaChanged");

        Assert.That((await new DeferredSourceSnapshot(parsers, query).Query(query).Collections["Types"]
            .Cast<TypeDeclaration>().Select(t => t.Name).ToArrayAsync()), Is.EqualTo(new[] { "Alpha", "BetaChanged" }));
        Assert.That(parser.Reads, Is.EqualTo(new[] { "b.fake" }));
    }

    [Test]
    public async Task SharedSnapshot_BundledCache_KeepsUndemandedModelsLazy()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The bundled cache uses Windows change-time validation.");
        File.WriteAllText(Path.Combine(_root, "a.fake"), "Alpha");
        var later = Path.Combine(_root, "b.fake");
        File.WriteAllText(later, "Beta");
        var parser = new CountingParser();
        var parsers = new SourceParserRegistry();
        parsers.Register(parser);
        var query = new ProviderQuery { RootPath = _root, Collection = "Types" };
        Assert.That((await new DeferredSourceSnapshot(parsers, query).Query(query).Collections["Types"]
            .Cast<TypeDeclaration>().Select(t => t.Name).ToArrayAsync()), Is.EqualTo(new[] { "Alpha", "Beta" }));
        parser.Reads.Clear();
        using var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var fresh = new DeferredSourceSnapshot(parsers, query);
        Assert.That((await fresh.Query(query).Collections["Types"].Cast<TypeDeclaration>().FirstAsync()).Name,
            Is.EqualTo("Alpha"));
        Assert.That(parser.Reads, Is.Empty);
    }

    [TestCase("python", ".py", "class Alpha:\n    pass\n", "class Bravo:\n    pass\n")]
    [TestCase("javascript", ".js", "class Alpha {}", "class Bravo {}")]
    [TestCase("go", ".go", "package example\ntype Alpha struct {}", "package example\ntype Bravo struct {}")]
    [TestCase("java", ".java", "class Alpha {}", "class Bravo {}")]
    [TestCase("rust", ".rs", "pub struct Alpha {}", "pub struct Bravo {}")]
    [TestCase("cop", ".cop", "type Alpha = { Name : string }", "type Bravo = { Name : string }")]
    public async Task SourceCache_PreservesResultsAndDetectsSameSizeSameTimestampEdits(
        string language, string extension, string original, string edited)
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The bundled cache uses Windows change-time validation.");
        var path = Path.Combine(_root, "a" + extension);
        File.WriteAllText(path, original);
        async Task<string[]> ReadNamesAsync(CancellationToken cancellationToken = default) => (await Items((await Service(language).QueryProviderAsync(language,
            new ProviderQuery { RootPath = _root, Collection = "Types" }, cancellationToken: cancellationToken))).Select(async (CopValue value, CancellationToken cancellationToken) => await NameAsync(value, cancellationToken)).ToArrayAsync());
        Assert.That((await ReadNamesAsync(cancellationToken: CancellationToken.None)), Is.EqualTo(new[] { "Alpha" }));
        Assert.That((await ReadNamesAsync(cancellationToken: CancellationToken.None)), Is.EqualTo(new[] { "Alpha" }));
        var timestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, edited);
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.That((await ReadNamesAsync(cancellationToken: CancellationToken.None)), Is.EqualTo(new[] { "Bravo" }));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async Task SharedSnapshot_BundledCache_IsolatesScopesAndTracksAddedAndDeletedFiles()
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
        async Task<string[]> ReadNames(params string[] excluded)
        {
            var query = new ProviderQuery
            {
                RootPath = _root, Collection = "Types", ExcludedDirectories = new HashSet<string>(excluded)
            };
            return (await new DeferredSourceSnapshot(parsers, query).Query(query).Collections["Types"]
                .Cast<TypeDeclaration>().Select(t => t.Name).ToArrayAsync());
        }
        Assert.That((await ReadNames()), Is.EqualTo(new[] { "Alpha", "Beta" }));
        Assert.That((await ReadNames("nested")), Is.EqualTo(new[] { "Alpha" }));
        parser.Reads.Clear();
        Assert.That((await ReadNames()), Is.EqualTo(new[] { "Alpha", "Beta" }));
        Assert.That(parser.Reads, Is.Empty);
        File.Delete(later);
        File.WriteAllText(Path.Combine(_root, "c.fake"), "Gamma");
        Assert.That((await ReadNames()), Is.EqualTo(new[] { "Alpha", "Gamma" }));
        Assert.That(parser.Reads, Is.EqualTo(new[] { "c.fake" }));
        parser.Reads.Clear();
        Assert.That((await ReadNames()), Is.EqualTo(new[] { "Alpha", "Gamma" }));
        Assert.That(parser.Reads, Is.Empty);
    }

    [Test]
    public async Task SharedSnapshot_BundledCache_ReportsTruncationAndRebuildsFromSource()
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
            Assert.That((await original.ReadAsync(path, parser, cancellationToken: CancellationToken.None))!.Types.Single().Name, Is.EqualTo("Alpha"));
            original.Save([path]);
            File.WriteAllBytes(cachePath, [2, 0, 0, 0, 1, 0, 0, 0]);
            parser.Reads.Clear();
            var error = Console.Error;
            using var diagnostics = new StringWriter();
            var rebuilt = new DeferredSourceCache(scope);
            try
            {
                Console.SetError(diagnostics);
                Assert.That((await rebuilt.ReadAsync(path, parser, cancellationToken: CancellationToken.None))!.Types.Single().Name, Is.EqualTo("Alpha"));
            }
            finally { Console.SetError(error); }
            Assert.That(diagnostics.ToString(), Does.Contain("Could not read deferred source cache"));
            Assert.That(parser.Reads, Is.EqualTo(new[] { "a.fake" }));
            rebuilt.Save([path]);
            parser.Reads.Clear();
            Assert.That((await new DeferredSourceCache(scope).ReadAsync(path, parser, cancellationToken: CancellationToken.None))!.Types.Single().Name, Is.EqualTo("Alpha"));
            Assert.That(parser.Reads, Is.Empty);
        }
        finally { File.Delete(cachePath); }
    }

    [Test]
    public async Task Files_DeferProjectDiscovery_AndPreserveNestedMembership()
    {
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.fake"), "Alpha");
        var parsers = new SourceParserRegistry();
        parsers.Register(new CountingParser());
        int discoveries = 0;
        var snapshot = new DeferredSourceSnapshot(parsers, new ProviderQuery { RootPath = _root },
            discoverProjects: cancellationToken =>
            {
                discoveries++;
                return ValueTask.FromResult<List<ProjectInfo>>([
                    new("Root", "root.csproj", "fixture", []),
                    new("Nested", Path.Combine("src", "nested.csproj"), "fixture", []),
                    new("Neighbor", Path.Combine("src2", "neighbor.csproj"), "fixture", [])
                ]);
            });
        var file = (await snapshot.Query(new ProviderQuery { Collection = "Files" })
            .Collections["Files"].Cast<SourceFile>().SingleAsync());

        Assert.That(file.Path, Is.EqualTo("src/a.fake"));
        Assert.That(discoveries, Is.EqualTo(0), "Unrelated queries must not discover projects.");
        Assert.That((await AsyncSourceFacts.ProjectsAsync(file, CancellationToken.None)).Projects, Is.EqualTo(new[] { "Root", "Nested" }));
        Assert.That((await AsyncSourceFacts.ProjectsAsync(file, CancellationToken.None)).Projects, Is.EqualTo(new[] { "Root", "Nested" }));
        var projects = (await snapshot.Query(new ProviderQuery { Collection = "Projects" })
            .Collections["Projects"].Cast<ProjectInfo>().Select(p => p.Name).ToArrayAsync());
        Assert.That(projects, Is.EqualTo(new[] { "Root", "Nested", "Neighbor" }));
        Assert.That(discoveries, Is.EqualTo(1));
    }

    [Test]
    public async Task CSharp_TypeNameDemand_DoesNotForceInterfaceEnrichment()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"),
            "class Alpha : IMarker { void Run() { var x = new Beta(); x.Ping(); } }");
        File.WriteAllText(Path.Combine(_root, "b.cs"),
            "interface IMarker {} class Beta { public void Ping() {} }");
        var provider = new CSharpProvider();
        var files = (DeferredProviderResult?)(await provider.QueryDeferredAsync(
            new ProviderQuery { RootPath = _root, Collection = "Files" }, cancellationToken: CancellationToken.None))
            ?? throw new AssertionException("Expected a deferred provider result.");
        var file = (await files.Collections["Files"].Cast<SourceFile>().FirstAsync());
        var type = file.Types.Single();
        var call = file.Statements.Single(s => s.MemberName == "Ping");
        var declaration = file.Statements.Single(s => s.Kind == "declaration");

        Assert.That((await AsyncSourceFacts.TypeNameAsync(call, CancellationToken.None)).TypeName, Is.EqualTo("Beta"));
        Assert.That((await AsyncSourceFacts.TypeNameAsync(declaration, CancellationToken.None)).TypeName, Is.EqualTo("Beta"));
        Assert.That(type.DeferredSemantics!.IsValueCreated, Is.False,
            "Resolving call types must not also resolve every type's interfaces.");
        Assert.That((await AsyncSourceFacts.TypeSemanticAsync(type, CancellationToken.None)).Interfaces, Is.EqualTo(new[] { "IMarker" }));
        Assert.That((await AsyncSourceFacts.TypeNameAsync(call, CancellationToken.None)).TypeName, Is.EqualTo("Beta"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CSharp_TypeNameDemand_PreservesNullableInferenceAndOverloadResolution(bool interfacesFirst)
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
        var result = (DeferredProviderResult?)(await provider.QueryDeferredAsync(
            new ProviderQuery { RootPath = _root, Collection = "Files" }, cancellationToken: CancellationToken.None))
            ?? throw new AssertionException("Expected a deferred provider result.");
        var file = (await result.Collections["Files"].Cast<SourceFile>().SingleAsync());
        if (interfacesFirst)
            Assert.That((await AsyncSourceFacts.TypeSemanticAsync(file.Types[0], CancellationToken.None)).Interfaces, Is.Empty);
        Assert.That((await file.Statements.Where(s => s.Kind == "declaration").ToAsyncEnumerable()
                .Select(async (Cop.Providers.SourceModel.StatementInfo s, CancellationToken cancellationToken) => (s.MemberName, (await AsyncSourceFacts.TypeNameAsync(s, CancellationToken.None)).TypeName)).ToArrayAsync()),
            Is.EqualTo(new[] {
                ("maybe", "Client?"), ("inferred", "Client"), ("present", "Client"),
                ("chained", "Client"), ("identified", "Client"), ("chosen", "Client"),
                ("number", "int"), ("nullableItems", "Client?[]"),
                ("narrowed", "Client"), ("narrowedItems", "Client[]")
            }));
        Assert.That((await file.Statements.Where(s => s.Kind == "call").ToAsyncEnumerable()
                .Select(async (Cop.Providers.SourceModel.StatementInfo s, CancellationToken cancellationToken) => (s.MemberName, (await AsyncSourceFacts.TypeNameAsync(s, CancellationToken.None)).TypeName)).ToArrayAsync()),
            Is.EqualTo(new (string?, string)[] {
                (null, "Client"), (null, "Client"), ("Next", "Client"),
                ("Identity", "Factory"), ("Choose", "Factory"),
                ("Choose", "Factory"), ("Identity", "Factory"), ("Ping", "Client"),
                ("Ping", "Client")
            }));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async Task CSharp_TextCache_ReusesUnchangedSourcesAndInvalidatesEdits()
    {
        var path = Path.Combine(_root, "a.cs");
        File.WriteAllText(path, "public class Alpha {}");
        async Task<SourceFile> ReadAsync(CancellationToken cancellationToken = default)
        {
            var provider = new CSharpProvider();
            var result = (DeferredProviderResult?)(await provider.QueryDeferredAsync(
                new ProviderQuery { RootPath = _root, Collection = "Files" }, cancellationToken: cancellationToken))
                ?? throw new AssertionException("Expected a deferred provider result.");
            return (await result.Collections["Files"].Cast<SourceFile>().SingleAsync());
        }

        Assert.That((await ReadAsync(cancellationToken: CancellationToken.None)).Types.Single().Name, Is.EqualTo("Alpha"));
        Assert.That((await ReadAsync(cancellationToken: CancellationToken.None)).Types.Single().Name, Is.EqualTo("Alpha"));
        File.WriteAllText(path, "public class BetaChanged {}");
        Assert.That((await ReadAsync(cancellationToken: CancellationToken.None)).Types.Single().Name, Is.EqualTo("BetaChanged"));
        File.WriteAllText(path, "public class SameLengthX {}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.That((await ReadAsync(cancellationToken: CancellationToken.None)).Types.Single().Name, Is.EqualTo("SameLengthX"));
        var timestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "public class SameLengthY {}");
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.That((await ReadAsync(cancellationToken: CancellationToken.None)).Types.Single().Name, Is.EqualTo("SameLengthY"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    [Platform("Win")]
    public async Task CSharp_TextCache_InvalidatesEditsEvenWhenAllFileMetadataMatches()
    {
        var path = Path.Combine(_root, "same-metadata.cs");
        File.WriteAllText(path, "public class Alpha {}");
        async Task<SourceFile> ReadAsync(CancellationToken cancellationToken = default)
        {
            var provider = new CSharpProvider();
            var result = (DeferredProviderResult?)(await provider.QueryDeferredAsync(
                new ProviderQuery { RootPath = _root, Collection = "Files" }, cancellationToken: cancellationToken))
                ?? throw new AssertionException("Expected a deferred provider result.");
            return (await result.Collections["Files"].Cast<SourceFile>().SingleAsync());
        }

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.That(GetFileInformationByHandleEx(handle, 0, out var metadata,
            (uint)Marshal.SizeOf<FileBasicInfo>()), Is.True, $"Win32 error {Marshal.GetLastWin32Error()}");
        var originalLength = new FileInfo(path).Length;
        Assert.That((await ReadAsync(cancellationToken: CancellationToken.None)).Types.Single().Name, Is.EqualTo("Alpha"));

        RandomAccess.Write(handle, System.Text.Encoding.UTF8.GetBytes("public class Bravo {}"), 0);
        Assert.That(SetFileInformationByHandle(handle, 0, ref metadata,
            (uint)Marshal.SizeOf<FileBasicInfo>()), Is.True, $"Win32 error {Marshal.GetLastWin32Error()}");
        Assert.That(new FileInfo(path).Length, Is.EqualTo(originalLength));
        Assert.That(File.GetLastWriteTimeUtc(path).ToFileTimeUtc(), Is.EqualTo(metadata.LastWriteTime));
        Assert.That(SourceFileMetadata.ReadChangeTime(path), Is.EqualTo(metadata.ChangeTime));

        Assert.That((await ReadAsync(cancellationToken: CancellationToken.None)).Types.Single().Name, Is.EqualTo("Bravo"),
            "Matching timestamps and length do not prove that cached source contents are current.");
        Assert.That((await ReadAsync(cancellationToken: CancellationToken.None)).Types.Single().Name, Is.EqualTo("Bravo"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    [Platform("Win")]
    public async Task Cop_SourceCache_InvalidatesEditsEvenWhenAllFileMetadataMatches()
    {
        string path = Path.Combine(_root, "same-metadata.cop");
        string original = "type Alpha = { Name : string }";
        string edited = "type Bravo = { Name : string }";
        File.WriteAllText(path, original);
        async Task<SourceFile> ReadAsync(CancellationToken cancellationToken = default)
        {
            CopProvider provider = new CopProvider();
            DeferredProviderResult result = (DeferredProviderResult?)(await provider.QueryDeferredAsync(
                new ProviderQuery { RootPath = _root, Collection = "Files" }, cancellationToken: cancellationToken))
                ?? throw new AssertionException("Expected a deferred provider result.");
            return (await result.Collections["Files"].Cast<SourceFile>().SingleAsync());
        }

        using SafeFileHandle handle = File.OpenHandle(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.That(GetFileInformationByHandleEx(handle, 0, out FileBasicInfo metadata,
            (uint)Marshal.SizeOf<FileBasicInfo>()), Is.True,
            $"Win32 error {Marshal.GetLastWin32Error()}");
        long originalLength = new FileInfo(path).Length;
        Assert.That((await ReadAsync(cancellationToken: CancellationToken.None)).Types.Select(type => type.Name), Is.EqualTo(new[] { "Alpha" }));
        Assert.That((await ReadAsync(cancellationToken: CancellationToken.None)).Types.Select(type => type.Name), Is.EqualTo(new[] { "Alpha" }));

        RandomAccess.Write(handle, System.Text.Encoding.UTF8.GetBytes(edited), 0);
        Assert.That(SetFileInformationByHandle(handle, 0, ref metadata,
            (uint)Marshal.SizeOf<FileBasicInfo>()), Is.True,
            $"Win32 error {Marshal.GetLastWin32Error()}");
        Assert.That(new FileInfo(path).Length, Is.EqualTo(originalLength));
        Assert.That(GetFileInformationByHandleEx(handle, 0, out FileBasicInfo restored,
            (uint)Marshal.SizeOf<FileBasicInfo>()), Is.True,
            $"Win32 error {Marshal.GetLastWin32Error()}");
        Assert.That(
            (restored.CreationTime, restored.LastAccessTime, restored.LastWriteTime,
                restored.ChangeTime, restored.FileAttributes),
            Is.EqualTo(
                (metadata.CreationTime, metadata.LastAccessTime, metadata.LastWriteTime,
                    metadata.ChangeTime, metadata.FileAttributes)));

        Assert.That((await ReadAsync(cancellationToken: CancellationToken.None)).Types.Select(type => type.Name), Is.EqualTo(new[] { "Bravo" }),
            "Matching file metadata must not allow stale Cop syntax to be reused.");
        SourceFile cached = (await ReadAsync(cancellationToken: CancellationToken.None));
        Assert.That(cached.Types.Select(type => type.Name), Is.EqualTo(new[] { "Bravo" }));
        Assert.That(cached.RawText, Is.EqualTo(edited));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileBasicInfo information, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileBasicInfo information, uint size);

    [TestCase("csharp", ".cs", "public class Alpha {}")]
    [TestCase("python", ".py", "class Alpha:\n    pass\n")]
    [TestCase("javascript", ".js", "class Alpha {}")]
    [TestCase("go", ".go", "package example\ntype Alpha struct {}")]
    [TestCase("java", ".java", "class Alpha {}")]
    [TestCase("rust", ".rs", "pub struct Alpha {}")]
    public async Task Projects_DoesNotOpenSourceFiles(string language, string extension, string source)
    {
        var path = Path.Combine(_root, "a" + extension);
        File.WriteAllText(path, source);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var service = Service(language);
        var projects = (await service.QueryProviderAsync(language, new ProviderQuery { RootPath = _root, Collection = "Projects" }, cancellationToken: CancellationToken.None));
        Assert.That((await Items(projects).CountAsync()), Is.EqualTo(0));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async Task DeferredFailure_IsReportedWhenDemanded_AndNotConvertedToEmptySuccess()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "public class Alpha {}");
        var later = Path.Combine(_root, "b.cs");
        File.WriteAllText(later, "public class Beta {}");
        var service = Service("csharp");
        var result = (await service.QueryProviderAsync("csharp", new ProviderQuery { RootPath = _root, Collection = "Types" }, cancellationToken: CancellationToken.None));
        using (var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.That((await NameAsync((await Items(result).FirstAsync()), cancellationToken: CancellationToken.None)), Is.EqualTo("Alpha"));
            await Assert.ThatAsync(async () => await Items(result).ToArrayAsync(),
                Throws.TypeOf<IOException>());
        }
        await Assert.ThatAsync(async () => await Items(result).ToArrayAsync(), Throws.TypeOf<IOException>(),
            "An errored iterator must preserve the failure, not silently terminate on retry.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CSharp_DeferredSemantics_PreservesInferredAndInvocationTypes(bool warmCache)
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"),
            "public class Alpha { public void Run() { var x = new Beta(); x.Ping(); } }");
        File.WriteAllText(Path.Combine(_root, "b.cs"), "public class Beta { public void Ping() {} }");
        var service = Service("csharp");
        if (warmCache)
        {
            Assert.That((await Items((await service.QueryProviderAsync("csharp",
                new ProviderQuery { RootPath = _root, Collection = "Files" }, cancellationToken: CancellationToken.None))).CountAsync()), Is.EqualTo(2));
            service = Service("csharp");
        }
        var result = (await service.QueryProviderAsync("csharp", new ProviderQuery { RootPath = _root, Collection = "Statements" }, cancellationToken: CancellationToken.None));
        var statements = (await Items(result).Cast<CopDynamicObject>().ToArrayAsync());
        var declaration = await statements.ToAsyncEnumerable().SingleAsync(
            async (CopDynamicObject s, CancellationToken cancellationToken) =>
                await (await s.GetFieldAsync("Kind", cancellationToken)).DisplayAsync(cancellationToken) == "declaration");
        var call = await statements.ToAsyncEnumerable().SingleAsync(
            async (CopDynamicObject s, CancellationToken cancellationToken) =>
                await (await s.GetFieldAsync("MemberName", cancellationToken)).DisplayAsync(cancellationToken) == "Ping");
        Assert.That((await (await declaration.GetFieldAsync("TypeName", cancellationToken: CancellationToken.None)).DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("Beta"));
        Assert.That((await (await call.GetFieldAsync("TypeName", cancellationToken: CancellationToken.None)).DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("Beta"));
    }

    [Test]
    public async Task DiskCache_ReconstructsUsingTheRequestingParserLoadContext()
    {
        var context = new System.Runtime.Loader.AssemblyLoadContext("provider-cache-test", isCollectible: true);
        try
        {
            var assembly = context.LoadFromAssemblyPath(typeof(CopSourceParser).Assembly.Location);
            var foreignParser = (ISourceParser)Activator.CreateInstance(
                assembly.GetType(typeof(CopSourceParser).FullName!)!)!;
            var localFile = new CopSourceParser().Parse("sample.cop", "function chosen() => 1")!;
            await foreignParser.ParseAsync("sample.cop", "function chosen() => 1", cancellationToken: CancellationToken.None);
            var path = Path.Combine(_root, "cache.bin");
            SourceCacheSerializer.Save(path, [1], [localFile]);
            Assert.That(SourceCacheSerializer.LoadOrDefault(path, [1])![0].Methods.Single().GetType(),
                Is.EqualTo(typeof(CopFunctionDeclaration)));
            CopFunctionDeclaration.RegisterCacheFactory();
            Assert.That(SourceCacheSerializer.LoadOrDefault(path, [1], assembly)![0].Methods.Single().GetType().Assembly,
                Is.SameAs(assembly));
        }
        finally { context.Unload(); }
    }

    private sealed class CountingParser : ISourceParser
    {
        public List<string> Reads { get; } = [];
        public override IReadOnlyList<string> Extensions => [".fake"];
        public override string Language => "fixture";
        public override ValueTask<SourceFile?> ParseAsync(string filePath, string sourceText,
            CancellationToken cancellationToken = default)
        {
            Reads.Add(Path.GetFileName(filePath));
            return ValueTask.FromResult<SourceFile?>(new SourceFile(filePath, Language,
                [new TypeDeclaration(sourceText, TypeKind.Class, Modifier.Public, [], [], [], [], [], [], 1)],
                [], sourceText));
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

    private static async Task<string> NameAsync(CopValue value, CancellationToken cancellationToken = default) => (await (await ((CopDynamicObject)value).GetFieldAsync("Name", cancellationToken: cancellationToken)).DisplayAsync(cancellationToken: cancellationToken));
    private static IAsyncEnumerable<CopValue> Items(CopValue value) => value switch
    {
        CopList list => list.Items.ToAsyncEnumerable(),
        CopLazyCollection lazy => lazy.EnumerateAsync(cancellationToken: CancellationToken.None),
        CopQueryable query => query.EnumerateAsync(cancellationToken: CancellationToken.None),
        _ => throw new AssertionException($"Expected collection, got {value.GetType().Name}")
    };
}
