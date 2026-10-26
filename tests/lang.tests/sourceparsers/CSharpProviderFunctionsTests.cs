using Cop.Core;
using Cop.Lang;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class CSharpProviderFunctionsTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-csharp-provider-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Test]
    public void ToCodebase_MaterializesPathScopedProviderResultAsCodebase()
    {
        var subDir = Path.Combine(_tempDir, "cs");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "Sample.cs"), "public class Alpha {}");

        var provider = new CSharpProvider();
        var queryResult = (Dictionary<string, List<object>>)provider.Query(new ProviderQuery { RootPath = subDir })!;
        var rawCodebase = new DataObject("Object", queryResult.ToDictionary(
            pair => pair.Key,
            pair => (object?)pair.Value,
            StringComparer.OrdinalIgnoreCase));

        var toCodebase = provider.GetFunctions()!["toCodebase"];
        var codebase = (DataObject)toCodebase([new List<object?> { rawCodebase }]).GetAwaiter().GetResult()!;

        Assert.That(codebase.TypeName, Is.EqualTo("Codebase"));
        var types = (List<object?>)codebase.GetField("Types")!;
        Assert.That(types, Has.Count.EqualTo(1));
        var type = (DataObject)types[0]!;
        Assert.That(type.GetField("Name"), Is.EqualTo("Alpha"));
        var file = (DataObject)type.GetField("File")!;
        Assert.That(file.GetField("Path"), Is.EqualTo("Sample.cs"));
    }

    [Test]
    public void CSharpPackage_ParsePath_PreservesDeferredProviderValues()
    {
        var packageSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "packages",
            "languages",
            "csharp",
            "src",
            "csharp.cop"));

        Assert.That(packageSource, Does.Contain("export function parse(path : string) : Codebase => provider('csharp', path)"));
        Assert.That(packageSource, Does.Contain("export function parse() : Codebase => provider('csharp')"));
    }

    [TestCase(".")]
    [TestCase("cs")]
    public async Task ToCodebase_EmptyInputPreservesExplicitParserScope(string path)
    {
        var sourceRoot = Path.GetFullPath(path, _tempDir);
        Directory.CreateDirectory(sourceRoot);
        var provider = new CSharpProvider();
        var context = new AnalysisContext(_tempDir, new ReviewOptions());
        var toCodebase = provider.GetFunctions(context)!["toCodebase"];

        var codebase = (DataObject)(await toCodebase([new List<object?>(), path]))!;

        Assert.That(codebase.TypeName, Is.EqualTo("Codebase"));
        Assert.That(codebase.GetField("Files"), Is.Empty);
        var scopes = (List<object>)codebase.GetField("SourceScopes")!;
        Assert.That(scopes, Has.Count.EqualTo(1));
        var scope = (DataObject)scopes[0];
        Assert.That(scope.TypeName, Is.EqualTo("SourceScope"));
        Assert.That(scope.GetField("RootPath"), Is.EqualTo(sourceRoot));
        Assert.That(scope.GetField("Extensions"), Is.EqualTo(new[] { ".cs" }));
    }

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException("Could not find repo root containing cop.sln.");
    }
}
