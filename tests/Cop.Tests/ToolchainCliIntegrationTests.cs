using System.Diagnostics;
using System.Text.Json;
using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public partial class ToolchainCliIntegrationTests
{
    private string _root = null!;
    private string _script = null!;
    private string _helper = null!;
    private string _cli = null!;
    private static string Dotnet => ToolchainProcess.FindExecutable("dotnet")!;

    [SetUp]
    public void SetUp()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        var configuration = directory.Parent!.Name;
        while (!File.Exists(Path.Combine(directory.FullName, "cop.sln"))) directory = directory.Parent!;
        _helper = Path.Combine(directory.FullName, "tests", "toolchain-worker", "bin", configuration, "net10.0", "Toolchain.Worker.dll");
        _cli = Path.Combine(directory.FullName, "cop", "cli", "bin", configuration, "net10.0", "cop.dll");
        _root = Path.Combine(Path.GetTempPath(), "cop-cli-toolchain-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(_root, "packages", "fixture");
        Directory.CreateDirectory(Path.Combine(package, "lib"));
        Directory.CreateDirectory(Path.Combine(package, "src"));
        File.Copy(_helper, Path.Combine(package, "lib", "Toolchain.Worker.dll"));
        File.WriteAllText(Path.Combine(package, "cop.json"), JsonSerializer.Serialize(new
        {
            name = "fixture", version = "1.0.0", provider = "clr",
            title = "Toolchain fixture", description = "Deferred process lifetime fixture", authors = "cop-tests",
            providerEntry = "Cop.Tests.ToolchainFixtureProvider", providerAssembly = "Toolchain.Worker.dll"
        }));
        Assert.That(PackageMetadata.TryLoadFromDirectory(package)?.IsProvider, Is.True);
        File.WriteAllText(Path.Combine(package, "src", "fixture.cop"), """
            type FixtureItem = { Name : string }
            type FixtureData = { Items : [FixtureItem] }
            let db : FixtureData = provider('fixture')
            export let Items = db.Items
            """);
        File.WriteAllText(Path.Combine(_root, "helper.txt"), _helper);
        Directory.CreateDirectory(Path.Combine(_root, "cop-checks"));
        _script = Path.Combine(_root, "cop-checks", "main.cop");
    }

    [TearDown]
    public async Task TearDown()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(_root, true); return; }
            catch (IOException) when (attempt < 20) { await Task.Delay(50); }
        }
    }

    [TestCase("fallback", 0)]
    [TestCase("syntax", 1)]
    public async Task DeferredDiagnostics_ReachTheRealCli_AndWorkersStopAtEngineReturn(string mode, int expectedExit)
    {
        File.WriteAllText(Path.Combine(_root, "mode.txt"), mode);
        WriteScript("command MAIN = print(Items.Count) & 0");
        var verify = await Run("verify", _script);
        Assert.That(verify.Status, Is.EqualTo(ToolchainStatus.Success), verify.StandardError);
        var result = await Run(_script, "-t", _root, "--no-color");
        Assert.That(result.ExitCode, Is.EqualTo(expectedExit), result.StandardError);
        Assert.That(result.StandardOutput.Trim(), Is.EqualTo("2"));
        Assert.That(result.StandardError.Split('\n').Count(line => line.Contains("using simpler fixture analysis")),
            Is.EqualTo(1), "Warn once even though two deferred records requested the fallback.");
        Assert.That(result.StandardError, Does.Not.Contain("\x1b["));
        if (mode == "syntax")
            Assert.That(result.StandardError, Does.Contain("sample.fixture(3): invalid syntax")
                .And.Contain("analysis model may be incomplete"));
        var pids = File.ReadAllLines(Path.Combine(_root, "pids.txt")).Select(int.Parse).ToArray();
        Assert.That(pids, Has.Length.EqualTo(2));
        Assert.That(pids[0], Is.EqualTo(pids[1]), "One engine execution must amortize helper startup.");
        AssertStopped(pids[0]);
    }

    [Test]
    public async Task UnusedProvider_DoesNotStartWorkerOrEmitFallbackWarning()
    {
        WriteScript("command MAIN = print('unused')");
        var result = await Run(_script, "-t", _root, "--no-color");
        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput.Trim(), Is.EqualTo("unused"));
        Assert.That(result.StandardError, Is.Empty);
        Assert.That(File.Exists(Path.Combine(_root, "pids.txt")), Is.False);
    }

    [Test]
    public async Task CommandFailure_DisposesWorkersEvenWhenDeferredIterationWasStarted()
    {
        File.WriteAllText(Path.Combine(_root, "mode.txt"), "throw");
        WriteScript("command MAIN = print(Items.Count)");
        var result = await Run(_script, "-t", _root, "--no-color");
        Assert.That(result.ExitCode, Is.EqualTo(2), result.StandardError);
        var pids = File.ReadAllLines(Path.Combine(_root, "pids.txt")).Select(int.Parse).Distinct().ToArray();
        Assert.That(pids, Has.Length.EqualTo(1));
        AssertStopped(pids[0]);
    }

    [TestCase("fallback", false)]
    [TestCase("throw", true)]
    public async Task EngineReturn_DisposesWorker_WhileCallingHostRemainsAlive(string mode, bool hasFatalErrors)
    {
        File.WriteAllText(Path.Combine(_root, "mode.txt"), mode);
        WriteScript("command MAIN = print(Items.Count)");
        var result = await ToolchainProcess.RunAsync(Dotnet,
            [_helper, "engine-probe", Path.GetDirectoryName(_script)!, _root], _root);
        Assert.That(result.Status, Is.EqualTo(ToolchainStatus.Success), result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var probe = document.RootElement;
        Assert.That(probe.GetProperty("HasFatalErrors").GetBoolean(), Is.EqualTo(hasFatalErrors), result.StandardOutput);
        Assert.That(probe.GetProperty("Warnings").EnumerateArray().Count(message =>
            message.GetString()!.Contains("using simpler fixture analysis")), Is.EqualTo(1));
        Assert.That(probe.GetProperty("Pids").GetArrayLength(), Is.EqualTo(1));
        Assert.That(probe.GetProperty("Stopped").GetBoolean(), Is.True,
            "The helper checks this immediately after Engine.Run, before its host process exits.");
    }

    private void WriteScript(string program)
    {
        var feed = Path.Combine(_root, "packages").Replace('\\', '/').Replace("'", "\\'");
        File.WriteAllText(_script, $"feed '{feed}'\nimport fixture\n{program}");
    }

    private Task<ToolchainResult> Run(params string[] args) =>
        ToolchainProcess.RunAsync(Dotnet, [_cli, .. args], _root, timeout: TimeSpan.FromSeconds(30));

    private static void AssertStopped(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.That(process.HasExited || process.WaitForExit(2000), Is.True, $"Worker {pid} outlived its engine execution.");
        }
        catch (ArgumentException) { }
    }
}
