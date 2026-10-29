using System.Diagnostics;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class OptionalLanguageDeploymentTests
{
    private string _root = null!;
    private string _repository = null!;
    private string _executable = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(_repository, "cop.sln")))
            _repository = Path.GetDirectoryName(_repository) ?? throw new AssertionException("Cannot locate repository.");
        _root = Path.Combine(Path.GetTempPath(), "cop-optional-language-" + Guid.NewGuid().ToString("N"));
        var portable = Path.Combine(_root, "portable");
        Directory.CreateDirectory(portable);
        var published = Environment.GetEnvironmentVariable("COP_TEST_PUBLISHED_EXE")
            ?? Path.Combine(_repository, "install", "win-x64", "cop.exe");
        _executable = Path.Combine(portable, Path.GetFileName(published));
        File.Copy(published, _executable);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [TestCase("csharp", ".cs", "class Alpha { void Run(string value) { ping(value); } }")]
    [TestCase("python", ".py", "class Alpha:\n    def Run(self, value):\n        ping(value)\n")]
    [TestCase("javascript", ".js", "class Alpha { Run(value) { ping(value); } }")]
    [TestCase("go", ".go", "package fixture\ntype Alpha struct {}\nfunc (a Alpha) Run(value string) { ping(value) }")]
    [TestCase("java", ".java", "class Alpha { void Run(String value) { ping(value); } }")]
    [TestCase("rust", ".rs", "pub struct Alpha; impl Alpha { fn Run(&self, value: &str) { ping(value); } }")]
    [TestCase("cop", ".cop", "type Alpha = { Value : string }\nfunction Run(value : string) => ping(value)")]
    public void BaseExecutable_AnalyzesEveryLanguageWithoutInstalledRuntimes(string language, string extension, string source)
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "sample" + extension), source);
        var program = WriteProgram($$"""
            import {{language}}
            let cb = {{language}}.parse()
            let alpha = cb.Types.Where(item.Name == 'Alpha')
            let runs = cb.Methods.Where(item.Name == 'Run')
            let calls = cb.Calls.Where(item.MemberName == 'ping')
            command MAIN = print('BASE {{language}} {alpha.Count} {runs.Count} {calls.Count}')
            """);
        var verification = Run("verify", program, withoutRuntimes: true);
        Assert.That(verification.ExitCode, Is.Zero, verification.Output + verification.Error);
        for (int pass = 0; pass < 2; pass++)
        {
            var result = Run(program, "-t", target, withoutRuntimes: true);
            Assert.That(result.ExitCode, Is.Zero, result.Error);
            Assert.That(result.Output.Trim(), Is.EqualTo($"BASE {language} 1 1 1"));
            if (language == "rust")
                Assert.That(result.Error, Does.Contain("syntax-only").And.Contain("optional cop-rust-<rid>.zip"));
        }
        Assert.That(Directory.GetFiles(Path.GetDirectoryName(_executable)!).Select(Path.GetFileName),
            Is.EqualTo(new[] { Path.GetFileName(_executable) }));
    }

    [Test]
    public void OptionalRustBundle_IsUsedOnlyWhileExplicitlyInstalledBesideExecutable()
    {
        var bundle = Environment.GetEnvironmentVariable("COP_TEST_RUST_BUNDLE")
            ?? Path.Combine(_repository, "install", "native-bundles", "win-x64", "bundle.zip");
        if (!File.Exists(bundle))
            Assert.Ignore("Build the optional Rust distribution to exercise its deployment contract.");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "sample.rs"),
            "pub struct Alpha; impl Alpha { pub fn get(&self, value: u32) -> u32 { value } } pub fn run() { let a = Alpha; a.get(1); }");
        File.WriteAllText(Path.Combine(target, "Cargo.toml"),
            "[package]\nname='optional-fixture'\nversion='1.0.0'\nedition='2024'\n[lib]\npath='sample.rs'");
        var program = WriteProgram("""
            import rust
            let cb = rust.parse()
            let calls = cb.Calls.Where(item.MemberName == 'get')
            let resolved = calls.Where(item.ReceiverType != nic && item.ValueType != nic && item.ReferencedCallable != nic
                && item.ReceiverType.Name == 'Alpha' && item.ValueType.Name == 'u32'
                && item.ReferencedCallable.Signature == 'pub fn get(&self, value: u32) -> u32')
            command MAIN = print('OPTIONAL {calls.Count} {resolved.Count}')
            """);
        var installed = Path.Combine(Path.GetDirectoryName(_executable)!, "Cop.RustFrontend.Bundle.zip");
        File.Copy(bundle, installed);
        var verification = Run("verify", program);
        Assert.That(verification.ExitCode, Is.Zero, verification.Output + verification.Error);
        for (int pass = 0; pass < 2; pass++)
        {
            var result = Run(program, "-t", target, withoutRuntimes: true);
            Assert.That(result.ExitCode, Is.Zero, result.Error);
            Assert.That(result.Output.Trim(), Is.EqualTo("OPTIONAL 1 1"), result.Error);
            Assert.That(result.Error, Does.Not.Contain("syntax-only"));
        }
        File.Delete(installed);
        var removed = Run(program, "-t", target, withoutRuntimes: true);
        Assert.That(removed.ExitCode, Is.Zero, removed.Error);
        Assert.That(removed.Output.Trim(), Is.EqualTo("OPTIONAL 1 0"));
        Assert.That(removed.Error, Does.Contain("syntax-only"),
            "A previously extracted cache must never enable an uninstalled language dependency.");
    }

    private string WriteProgram(string source)
    {
        var program = Path.Combine(_root, "main.cop");
        var feed = Path.Combine(_repository, "packages").Replace('\\', '/').Replace("'", "\\'");
        File.WriteAllText(program, $"feed '{feed}'\n{source}");
        return program;
    }

    private (int ExitCode, string Output, string Error) Run(string first, string second,
        string? third = null, bool withoutRuntimes = false)
    {
        var start = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { first, second, third }.OfType<string>())
            start.ArgumentList.Add(argument);
        if (first != "verify") start.ArgumentList.Add("--no-color");
        if (withoutRuntimes)
        {
            start.Environment["PATH"] = "";
            start.Environment["DOTNET_ROOT"] = Path.Combine(_root, "no-dotnet");
            start.Environment["DOTNET_ROOT_X64"] = start.Environment["DOTNET_ROOT"];
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Optional language deployment query timed out.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
