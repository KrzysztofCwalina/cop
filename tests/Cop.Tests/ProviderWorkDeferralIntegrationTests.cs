using System.Diagnostics;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class ProviderWorkDeferralIntegrationTests
{
    private string _root = null!;
    private static string RepoRoot => FindRepoRoot();
    private string SourceRoot => Path.Combine(_root, "target", "src");

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-provider-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "cop-checks"));
        Directory.CreateDirectory(SourceRoot);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    [TestCase("csharp", ".cs", "public class AlphaClient {}", "public class BetaClient {}")]
    [TestCase("python", ".py", "class AlphaClient:\n    pass\n", "class BetaClient:\n    pass\n")]
    [TestCase("javascript", ".js", "class AlphaClient {}", "class BetaClient {}")]
    [TestCase("go", ".go", "package example\ntype AlphaClient struct {}", "package example\ntype BetaClient struct {}")]
    [TestCase("java", ".java", "class AlphaClient {}", "class BetaClient {}")]
    [TestCase("rust", ".rs", "pub struct AlphaClient {}", "pub struct BetaClient {}")]
    [TestCase("cop", ".cop", "type AlphaClient = { Name : string }", "type BetaClient = { Name : string }")]
    public void PublishedCli_FirstAndTake_DoNotReadLockedLaterFile(
        string language, string extension, string firstSource, string secondSource)
    {
        File.WriteAllText(Path.Combine(SourceRoot, "a" + extension), firstSource);
        var later = Path.Combine(SourceRoot, "b" + extension);
        File.WriteAllText(later, secondSource);
        WriteScript($$"""
            import code
            import {{language}}
            let cb = {{language}}.parse('src')
            predicate selected(Type) => Type.Name:endsWith('Client')
            let chosen = cb.Types:selected
            command FIRST = print(chosen.First.Name)
            command TAKE = foreach chosen.take(1) => '{item.Name}'
            command COUNT = print(chosen.Count)
            """);
        using (var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (var command in new[] { "FIRST", "TAKE" })
            {
                var result = Run(command);
                Assert.That(result.ExitCode, Is.EqualTo(0), result.Stderr);
                Assert.That(result.Stdout.Trim(), Is.EqualTo("AlphaClient"));
                Assert.That(ModelCount(result.Stderr), Is.EqualTo(1), result.Stderr);
                Assert.That(result.Stderr, Does.Not.Contain("[provider-semantics]"));
                Assert.That(result.Stderr, Does.Not.Contain("b" + extension));
                Assert.That(result.Stderr, Does.Not.Contain("parse error"));
            }
        }
        var count = Run("COUNT");
        Assert.That(count.ExitCode, Is.EqualTo(0), count.Stderr);
        Assert.That(count.Stdout.Trim(), Is.EqualTo("2"));
        Assert.That(ModelCount(count.Stderr), Is.EqualTo(2), count.Stderr);
        Assert.That(count.Stderr, Does.Contain($"[query-result] {language}.Types returned=2"));
    }

    [Test]
    public void PublishedCli_CSharpSemanticDemand_ParsesDependenciesWithoutExtractingTheirModels()
    {
        File.WriteAllText(Path.Combine(SourceRoot, "a.cs"), "public class Alpha : Base {}");
        File.WriteAllText(Path.Combine(SourceRoot, "b.cs"),
            "public class Base : IMarker {}\npublic interface IMarker {}");
        WriteScript("""
            import code
            import csharp
            let cb = csharp.parse('src')
            command MAIN = print(cb.Types.Where(item.Name == 'Alpha').First.Interfaces.Count)
            """);
        var result = Run("MAIN");
        Assert.That(result.ExitCode, Is.EqualTo(0), result.Stderr);
        Assert.That(result.Stdout.Trim(), Is.EqualTo("1"));
        Assert.That(ModelCount(result.Stderr), Is.EqualTo(1), result.Stderr);
        Assert.That(result.Stderr, Does.Contain("[provider-semantics] csharp compilation files=2"));
        Assert.That(result.Stderr.Split('\n').Count(line => line.StartsWith("[provider-parse]")),
            Is.EqualTo(2), result.Stderr);
    }

    [TestCase("csharp", ".cs")]
    [TestCase("python", ".py")]
    [TestCase("javascript", ".js")]
    [TestCase("go", ".go")]
    [TestCase("java", ".java")]
    [TestCase("rust", ".rs")]
    [TestCase("cop", ".cop")]
    public void PublishedCli_ThousandDistinctChecks_ReuseTheCommonPrefix(string language, string extension)
    {
        var source = string.Join("\n", Enumerable.Range(0, 100).Select(i =>
        {
            var name = (i % 20 == 0 ? "Public" : "Internal") + i.ToString("D3");
            return language switch
            {
                "csharp" => $"public class {name} {{}}",
                "python" => $"class {name}:\n    pass",
                "javascript" or "java" => $"class {name} {{}}",
                "go" => $"type {name} struct {{}}",
                "rust" => $"pub struct {name} {{}}",
                "cop" => $"type {name} = {{ Name : string }}",
                _ => throw new ArgumentOutOfRangeException(nameof(language))
            };
        }));
        File.WriteAllText(Path.Combine(SourceRoot, "types" + extension),
            language == "go" ? "package example\n" + source : source);
        var branches = string.Join("\n", Enumerable.Range(0, 1000).Select(i =>
            $"let check-{i:D3} = visible.Where(item.Name == 'Public{i:D3}')"));
        var counts = string.Join(", ", Enumerable.Range(0, 1000).Select(i => $"check-{i:D3}.Count"));
        WriteScript($$"""
            import code
            import {{language}}
            let cb = {{language}}.parse('src')
            predicate selected(Type) => Type.Name:startsWith('Public')
            let visible = cb.Types:selected
            {{branches}}
            command MAIN = print([{{counts}}])
            """);
        var result = Run("MAIN");
        Assert.That(result.ExitCode, Is.EqualTo(0), result.Stderr);
        var expected = "[" + string.Join(", ", Enumerable.Range(0, 1000)
            .Select(i => i < 100 && i % 20 == 0 ? "1" : "0")) + "]";
        Assert.That(result.Stdout.Trim(), Is.EqualTo(expected));
        var diagnostics = TestTextParsing.StripAnsi(result.Stderr);
        var queries = diagnostics.Split('\n')
            .Where(line => line.StartsWith($"[query] Query {language}.Types ")).ToArray();
        Assert.That(queries, Has.Length.EqualTo(2), string.Join("\n", queries));
        Assert.That(queries[0].Trim(), Does.EndWith("filter=Name:startsWith('Public'), Name:equalsExact('Public000')"));
        Assert.That(queries[1].Trim(), Does.EndWith("filter=Name:startsWith('Public')"));
        Assert.That(diagnostics.Split('\n').Count(line =>
            line.StartsWith("[query-local]") && line.Contains("shared prefix")), Is.EqualTo(999));
        Assert.That(ModelCount(diagnostics), Is.EqualTo(1));
        Assert.That(diagnostics, Does.Not.Contain("[provider-semantics]"));
    }

    private void WriteScript(string program)
    {
        var feed = Path.Combine(RepoRoot, "packages").Replace("\\", "\\\\").Replace("'", "\\'");
        File.WriteAllText(Path.Combine(_root, "cop-checks", "main.cop"), $"feed '{feed}'\n{program}");
        var verify = RunCop("verify", Path.Combine(_root, "cop-checks", "main.cop"));
        Assert.That(verify.ExitCode, Is.EqualTo(0), verify.Stderr);
    }

    private (int ExitCode, string Stdout, string Stderr) Run(string command) =>
        RunCop(Path.Combine(_root, "cop-checks", "main.cop"), "-t", Path.Combine(_root, "target"), "-c", command, "-dd", "--no-color");

    private (int ExitCode, string Stdout, string Stderr) RunCop(params string[] args)
    {
        var executable = Path.Combine(RepoRoot, "install", "win-x64", "cop.exe");
        Assert.That(File.Exists(executable), Is.True, "Publish win-x64 before running CLI integration tests.");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Published CLI did not finish within 60 seconds.");
        }
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    private static int ModelCount(string diagnostics) =>
        diagnostics.Split('\n').Count(line => line.StartsWith("[provider-model]"));

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "cop.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
