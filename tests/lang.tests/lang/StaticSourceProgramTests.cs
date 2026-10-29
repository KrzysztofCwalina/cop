using Cop.Lang;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using NUnit.Framework;

namespace Lang.Tests.Lang;

[TestFixture]
public class StaticSourceProgramTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-static-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    private void Package(string name, string text)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "cop.json"), $$"""{"name":"{{name}}"}""");
        File.WriteAllText(Path.Combine(directory, "src", "source.cop"), text);
    }

    [Test]
    public void ParsedFeedMetadataIsDecodedAndDoesNotConsumeTheNextDeclaration()
    {
        var module = CopParser.Parse("""
            feed 'C:\\local\\packages'
            function known(value : string) : string => value
            """);
        Assert.That(module.FeedPaths, Is.EqualTo(new[] { @"C:\local\packages" }));
        Assert.That(module.Declarations.Single(), Is.TypeOf<Cop.Lang.Ast.FunctionDecl>());
        var recovery = CopParser.ParseWithRecovery("feed 'local'\nlet value = 1", "source.cop", out var errors);
        Assert.That(errors, Is.Empty);
        Assert.That(recovery.FeedPaths, Is.EqualTo(new[] { "local" }));
        Assert.That(recovery.Declarations.Single(), Is.TypeOf<Cop.Lang.Ast.LetDecl>());
        Assert.That(CopParser.Parse("feed\n'not a feed'").FeedPaths, Is.Empty);
    }

    [Test]
    public void MissingRemoteFeedStaysDeclarationOnlyAndReportsLocalContextGap()
    {
        var source = "feed 'https://127.0.0.1:1/not-a-local-feed'\nimport unavailable";
        var path = Path.Combine(_root, "source.cop");
        var program = new StaticSourceProgram([new(CopParser.Parse(source, path), path, source)], []);
        Assert.That(program.Diagnostics, Is.EqualTo(new[]
        {
            "Import 'unavailable' is unavailable to declaration-only analysis; restore it to a local source feed."
        }));
        Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty, "No restore/cache setup may be created in the source directory.");
    }

    [Test]
    public void InvalidLocalFeedMetadataProducesAnExplicitDiagnostic()
    {
        var path = Path.Combine(_root, "source.cop");
        var module = new Cop.Lang.Ast.ModuleNode([]) { FeedPaths = ["\0"] };
        var program = new StaticSourceProgram([new(module, path, "")], []);
        Assert.That(program.Diagnostics, Has.Count.EqualTo(1));
        Assert.That(program.Diagnostics[0], Does.StartWith($"Feed in '{path}' cannot be used for local source analysis: "));
    }

    [Test]
    public void ImportCyclesTerminateAndKeepExportedSignatures()
    {
        Package("first", "import second\nexport function known(value : string) : string => value");
        Package("second", "import first\nexport function other(value : int) : int => value");
        var source = "import first\nlet result = known('hello')";
        var path = Path.Combine(_root, "caller.cop");
        var module = CopParser.Parse(source, path);
        var program = new StaticSourceProgram([new(module, path, source)], [_root]);
        var call = (Cop.Lang.Ast.CallExpr)module.Declarations.OfType<Cop.Lang.Ast.LetDecl>().Single().Value;
        var selected = program.Model.ResolveCall(call);
        Assert.That(selected.Declaration?.Name, Is.EqualTo("known"));
        Assert.That(selected.ResultType?.Display, Is.EqualTo("string"));
        Assert.That(program.Diagnostics, Is.Empty);
    }

    [Test]
    public void LiteralLocalFeedUsesParsedMetadataWithoutSourceTextScanning()
    {
        Package("library", "export function known(value : string) : string => value");
        var feed = _root.Replace("\\", "\\\\", StringComparison.Ordinal);
        var source = $"feed '{feed}'\nimport library\nlet result = library.known('hello')";
        var path = Path.Combine(_root, "caller.cop");
        var module = CopParser.Parse(source, path);
        var program = new StaticSourceProgram([new(module, path, source)], []);
        var call = (Cop.Lang.Ast.CallExpr)module.Declarations.OfType<Cop.Lang.Ast.LetDecl>().Single().Value;
        Assert.That(program.Model.ResolveCall(call).ResultType?.Display, Is.EqualTo("string"));
        Assert.That(program.Diagnostics, Is.Empty);
    }

    [Test]
    public void TransitivePrivateImportsDoNotInventCallerVisibleFunctions()
    {
        Package("second", "export function hidden(value : int) : int => value");
        Package("first", "import second\nexport function known(value : string) : string => value");
        var source = "import first\nlet visible = known('hello')\nlet unavailable = hidden(1)";
        var path = Path.Combine(_root, "caller.cop");
        var module = CopParser.Parse(source, path);
        var program = new StaticSourceProgram([new(module, path, source)], [_root]);
        var calls = module.Declarations.OfType<Cop.Lang.Ast.LetDecl>().Select(let => (Cop.Lang.Ast.CallExpr)let.Value).ToList();
        Assert.That(program.Model.ResolveCall(calls[0]).ResultType?.Display, Is.EqualTo("string"));
        var unavailable = program.Model.ResolveCall(calls[1]);
        Assert.That(unavailable.Declaration, Is.Null);
        Assert.That(unavailable.ResultType, Is.Null);
        Assert.That(unavailable.Diagnostics, Is.EqualTo(new[] { "Call target is not a statically available source function." }));
    }

    [Test]
    public void ImportDepthHasAnExactVisibleSafetyBoundary()
    {
        for (int i = 0; i <= 64; i++)
            Package($"p{i}", i < 64 ? $"import p{i + 1}" : "export let end = 1");
        var source = "import p0";
        var path = Path.Combine(_root, "caller.cop");
        var program = new StaticSourceProgram([new(CopParser.Parse(source, path), path, source)], [_root]);
        Assert.That(program.Diagnostics, Is.EqualTo(new[]
        {
            "Import 'p64' exceeds the declaration-only analysis limit (64 levels / 256 packages)."
        }));
    }

    [Test]
    public void LocalFeedTraversalHasAnExactVisibleSafetyBoundary()
    {
        var directory = _root;
        for (int i = 0; i < 65; i++)
        {
            directory = Path.Combine(directory, "g");
            Directory.CreateDirectory(directory);
        }
        var error = Assert.Throws<IOException>(() => ImportResolver.FindPackageDir(_root, "missing"));
        Assert.That(error!.Message, Is.EqualTo("Local package discovery exceeded its 4096-directory / 64-level safety limit."));
    }
}
