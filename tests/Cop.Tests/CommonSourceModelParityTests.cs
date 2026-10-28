using System.IO.Compression;
using System.Reflection;
using System.Text;
using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class CommonSourceModelParityTests
{
    [Test]
    public void CacheRoundTrip_PreservesNeutralFactsAndExplicitUnknownSpans()
    {
        var parameter = new ParameterDeclaration("value", null, false, false, false, 2)
        {
            IsOptional = true, Column = 9, EndLine = 2, EndColumn = 15
        };
        var method = new MethodDeclaration("Map", Modifier.Public, [], null, [parameter], 2)
        {
            TypeParameters = ["T", "U"], ReceiverType = "Value", Column = 1, EndLine = 4, EndColumn = 2
        };
        var target = new TypeReference("List", "example", [new("T", null, [], "T")], "List<T>");
        var alias = new TypeDeclaration("Values", TypeKind.Alias, Modifier.Public, [], [], [], [], [], [], 1)
        {
            AliasTarget = target, TypeParameters = ["T"], Namespace = "example", Column = 1, EndLine = 1, EndColumn = 24
        };
        var value = new ExpressionInfo("stringLiteral") { StringValue = "decoded\nvalue" };
        var statement = new StatementInfo("return", [], null, null, [], 3, true)
        {
            ValueExpression = value, CallTarget = value, ArgumentExpressions = [value],
            Column = 3, EndLine = 3, EndColumn = 20, Method = method
        };
        method.Statements = [statement];
        var import = new ImportInfo("pkg", 1)
        {
            Name = "thing", Alias = "local", RelativeLevel = 2,
            IsWildcard = true, IsStatic = true, Column = 1, EndLine = 1, EndColumn = 35
        };
        var source = new SourceFile("facts.test", "test", [alias], [statement], "")
        {
            Imports = [import], Methods = [method], ParseErrors = ["facts.test(5): malformed declaration"]
        };
        var file = SourceGraphIdentityTests.RoundTrip(source);
        var type = file.Types.Single();
        var actual = file.Methods.Single();
        Assert.Multiple(() =>
        {
            Assert.That(type.Kind, Is.EqualTo(TypeKind.Alias));
            Assert.That(type.Namespace, Is.EqualTo("example"));
            Assert.That(type.AliasTarget?.OriginalText, Is.EqualTo("List<T>"));
            Assert.That(type.AliasTarget?.GenericArguments.Single().Name, Is.EqualTo("T"));
            Assert.That(type.TypeParameters, Is.EqualTo(new[] { "T" }));
            Assert.That((type.Column, type.EndLine, type.EndColumn), Is.EqualTo((1, 1, 24)));
            Assert.That(actual.ReceiverType, Is.EqualTo("Value"));
            Assert.That(actual.TypeParameters, Is.EqualTo(new[] { "T", "U" }));
            Assert.That((actual.Column, actual.EndLine, actual.EndColumn), Is.EqualTo((1, 4, 2)));
            Assert.That(actual.Parameters.Single().IsOptional, Is.True);
            Assert.That(actual.Parameters.Single().HasDefaultValue, Is.False);
            Assert.That((actual.Parameters[0].Column, actual.Parameters[0].EndLine, actual.Parameters[0].EndColumn),
                Is.EqualTo((9, 2, 15)));
            Assert.That(file.Imports.Single(), Is.EqualTo(import));
            Assert.That(file.ParseErrors, Is.EqualTo(source.ParseErrors));
            Assert.That(file.Statements.Single().ValueExpression?.StringValue, Is.EqualTo("decoded\nvalue"));
            Assert.That(file.Statements[0].ValueExpression, Is.SameAs(file.Statements[0].CallTarget));
            Assert.That(file.Statements[0].ArgumentExpressions.Single(), Is.SameAs(file.Statements[0].ValueExpression));
            Assert.That((file.Statements[0].Column, file.Statements[0].EndLine, file.Statements[0].EndColumn),
                Is.EqualTo((3, 3, 20)));
        });
        var unknown = SourceGraphIdentityTests.RoundTrip(new("unknown.test", "test",
            [new("Unknown", TypeKind.Class, Modifier.Public, [], [], [], [], [], [], 1)], [], ""));
        Assert.That(unknown.Types[0].Column, Is.Null);
        Assert.That(unknown.Types[0].EndLine, Is.Null);
        Assert.That(unknown.Types[0].EndColumn, Is.Null);
    }

    [Test]
    public void DeferredReceiverMembers_AreDemandedOnce_PreserveFile_AndNeverPolluteCache()
    {
        WithRoot(root =>
        {
            File.WriteAllText(Path.Combine(root, "a.fake"), "owner");
            File.WriteAllText(Path.Combine(root, "b.fake"), "member");
            File.WriteAllText(Path.Combine(root, "c.fake"), "different-namespace");
            var parser = new ModelParser();
            var registry = new SourceParserRegistry();
            registry.Register(parser);
            var snapshot = new DeferredSourceSnapshot(registry, new() { RootPath = root });
            var type = snapshot.Query(new() { Collection = "Types" }).Collections["Types"].Cast<TypeDeclaration>().First();
            Assert.That(type.Name, Is.EqualTo("Value"));
            Assert.That(parser.Reads, Is.EqualTo(new[] { "a.fake" }));
            Assert.That(type.Methods.Select(m => m.Name), Is.EqualTo(new[] { "Run" }));
            Assert.That(parser.Reads, Is.EqualTo(new[] { "a.fake", "b.fake", "c.fake" }));
            var method = type.Methods.Single();
            Assert.That(method.File?.Path, Is.EqualTo("b.fake"));
            var files = snapshot.Query(new() { Collection = "Files" }).Collections["Files"].Cast<SourceFile>().ToList();
            Assert.That(files[0].Methods, Is.Empty);
            Assert.That(files[1].Methods.Single(), Is.SameAs(method));
            var flattened = snapshot.Query(new() { Collection = "Methods" }).Collections["Methods"].Cast<MethodDeclaration>().ToList();
            Assert.That(flattened.Select(m => m.Name), Is.EqualTo(new[] { "Run", "WrongScope" }));
            Assert.That(flattened[0], Is.SameAs(method));
            var intrinsic = SourceGraphIdentityTests.RoundTrip(files[0]);
            Assert.That(intrinsic.Types.Single().Methods, Is.Empty);
            Assert.That(intrinsic.Methods, Is.Empty);
            Assert.That(type.Methods.Single(), Is.SameAs(method));
            Assert.That(parser.Reads, Has.Count.EqualTo(3));
        });
    }

    [TestCase(null, new[] { "a.fake", "b.fake", "c.fake" })]
    [TestCase("b.fake", new[] { "b.fake" })]
    [TestCase("", new string[0])]
    public void ExplicitProjectMembership_DistinguishesUnspecifiedAndEmpty(string? member, string[] expected)
    {
        WithRoot(root =>
        {
            foreach (var name in new[] { "a.fake", "b.fake", "c.fake" })
                File.WriteAllText(Path.Combine(root, name), "owner");
            var registry = new SourceParserRegistry();
            registry.Register(new ModelParser());
            var snapshot = new DeferredSourceSnapshot(registry, new() { RootPath = root }, discoverProjects: () =>
                [new ProjectInfo("project", Path.Combine(root, "project.json"), "test", [], [], [])
                {
                    SourceFiles = member is null ? null : member.Length == 0 ? [] : [Path.Combine(root, member)]
                }]);
            var files = snapshot.Query(new() { Collection = "Files" }).Collections["Files"].Cast<SourceFile>();
            Assert.That(files.Where(file => file.Projects.Contains("project")).Select(file => file.Path), Is.EqualTo(expected));
        });
    }

    [Test]
    public void ParserCacheIdentity_IsDemandedBeforeLookup_AndInvalidatesBackendChanges()
    {
        WithRoot(root =>
        {
            File.WriteAllText(Path.Combine(root, "a.fake"), "owner");
            var parser = new ModelParser();
            SourceFile Read()
            {
                var registry = new SourceParserRegistry();
                registry.Register(parser);
                var snapshot = new DeferredSourceSnapshot(registry, new() { RootPath = root });
                Assert.That(parser.IdentityReads, Is.EqualTo(parser.ExpectedIdentityReads));
                parser.ExpectedIdentityReads++;
                return snapshot.Query(new() { Collection = "Files" }).Collections["Files"].Cast<SourceFile>().Single();
            }
            Assert.That(Read().Namespace, Is.EqualTo("first"));
            Assert.That(Read().Namespace, Is.EqualTo("first"));
            Assert.That(parser.Reads, Has.Count.EqualTo(1), "Stable backend must use the persisted cache.");
            parser.Identity = "second";
            Assert.That(Read().Namespace, Is.EqualTo("second"));
            Assert.That(parser.Reads, Has.Count.EqualTo(2), "Changed backend must not reuse old facts.");
            parser.Cacheable = false;
            Assert.That(Read().Namespace, Is.EqualTo("second"));
            Assert.That(Read().Namespace, Is.EqualTo("second"));
            Assert.That(parser.Reads, Has.Count.EqualTo(4), "Non-cacheable semantic projection must parse every execution.");
            Assert.That(parser.IdentityReads, Is.EqualTo(5), "Cache hits must still execute the provider preflight.");
        });
    }

    [Test]
    public void LegacyCacheFormat_IsRejected()
    {
        var serializer = typeof(CodeCollectionBuilder).Assembly.GetType("Cop.Providers.SourceCacheSerializer", true)!;
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true))
        using (var writer = new BinaryWriter(gzip, Encoding.UTF8, true))
        {
            writer.Write(new byte[] { (byte)'C', (byte)'O', (byte)'P', (byte)'C' });
            writer.Write(5);
        }
        buffer.Position = 0;
        var result = serializer.GetMethod("TryLoad", BindingFlags.Static | BindingFlags.NonPublic, null,
            [typeof(Stream), typeof(byte[]), typeof(Assembly)], null)!.Invoke(null, [buffer, new byte[] { 1 }, typeof(CodeCollectionBuilder).Assembly]);
        Assert.That(result, Is.Null);
    }

    private static void WithRoot(Action<string> run)
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "model-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { run(root); }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ModelParser : ISourceParser
    {
        public override IReadOnlyList<string> Extensions => [".fake"];
        public override string Language => "test";
        public override bool SupportsCrossFileMembers => true;
        public List<string> Reads { get; } = [];
        public string Identity { get; set; } = "first";
        public bool Cacheable { get; set; } = true;
        public int IdentityReads { get; private set; }
        public int ExpectedIdentityReads { get; set; }
        public override bool CanCache(string filePath) => Cacheable;
        public override string GetCacheIdentity(string filePath) { IdentityReads++; return Identity; }
        public override SourceFile Parse(string filePath, string sourceText)
        {
            Reads.Add(Path.GetFileName(filePath));
            var member = sourceText != "owner";
            var wrongScope = sourceText == "different-namespace";
            return new(filePath, Language,
                member ? [] : [new("Value", TypeKind.Struct, Modifier.Public, [], [], [], [], [], [], 1)],
                [], sourceText)
            {
                Namespace = wrongScope ? "other" : Identity,
                Methods = member ? [new(wrongScope ? "WrongScope" : "Run", Modifier.Public, [], null, [], 1) { ReceiverType = "Value" }] : []
            };
        }
    }
}
