using System.Diagnostics;
using System.Text.Json;
using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class ToolchainProcessTests
{
    private string _root = null!;
    private static string Dotnet => ToolchainProcess.FindExecutable("dotnet")
        ?? throw new InvalidOperationException("The .NET test host requires an installed dotnet toolchain.");

    private static string Helper
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            var configuration = directory.Parent!.Name;
            while (!File.Exists(Path.Combine(directory.FullName, "cop.sln"))) directory = directory.Parent!;
            return Path.Combine(directory.FullName, "tests", "toolchain-worker", "bin", configuration, "net10.0", "Toolchain.Worker.dll");
        }
    }

    private static string[] Arguments(string mode, params string[] arguments) => [Helper, mode, .. arguments];

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop toolchain " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

    [Test]
    public void Discovery_EmptyAndRelativeSearchPath_DoNotSearchTheWorkingDirectory()
    {
        Assert.That(ToolchainProcess.FindExecutable("dotnet", ""), Is.Null);
        Assert.That(ToolchainProcess.FindExecutable("dotnet", "."), Is.Null);
        Assert.That(ToolchainProcess.FindExecutable(Dotnet, ""), Is.EqualTo(Dotnet));
        Assert.That(() => ToolchainProcess.FindExecutable(Path.Combine("relative", "dotnet")), Throws.ArgumentException);
    }

    [Test]
    public async Task MissingExecutable_IsUnavailable_NotAnEmptySuccessfulResult()
    {
        var missing = Path.Combine(_root, "not-installed.exe");
        var result = await ToolchainProcess.RunAsync(missing, [], _root);
        Assert.That(result.Status, Is.EqualTo(ToolchainStatus.Unavailable));
        Assert.That(result.ExitCode, Is.Null);
        Assert.That(result.Diagnostic, Does.Contain("not installed"));
    }

    [Test]
    public async Task Arguments_PreserveExactBoundaries_WithoutShellInterpretation()
    {
        string[] values = ["one two", "quote\"here", @"trailing\", "'quoted'", "; & | $(echo injected)", "\u03bb"];
        var result = await ToolchainProcess.RunAsync(Dotnet, Arguments("arguments", values), _root);
        Assert.That(result.Status, Is.EqualTo(ToolchainStatus.Success), result.Diagnostic);
        Assert.That(JsonSerializer.Deserialize<string[]>(result.StandardOutput), Is.EqualTo(values));
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    [Test]
    public async Task StdinAndBothOutputStreams_AreDrainedConcurrently()
    {
        var input = string.Concat(Enumerable.Repeat("exact stdin \u03bb\n", 50_000));
        var result = await ToolchainProcess.RunAsync(Dotnet, Arguments("echo"), _root, input);
        Assert.That(result.Status, Is.EqualTo(ToolchainStatus.Success), result.Diagnostic);
        Assert.That(result.StandardOutput, Is.EqualTo(input));
        Assert.That(result.StandardError, Is.EqualTo("stderr"));
    }

    [Test]
    public async Task NonzeroSyntaxFailure_IsNotUnavailableOrSuccessful()
    {
        var result = await ToolchainProcess.RunAsync(Dotnet, Arguments("fail"), _root);
        Assert.That(result.Status, Is.EqualTo(ToolchainStatus.Failed));
        Assert.That(result.ExitCode, Is.EqualTo(17));
        Assert.That(result.StandardError, Is.EqualTo("source.py(3): invalid syntax"));
    }

    [Test]
    public async Task Deadline_IncludesBlockedStdin_NotJustProcessExit()
    {
        var timer = Stopwatch.StartNew();
        var result = await ToolchainProcess.RunAsync(Dotnet, Arguments("sleep"), _root,
            new string('x', 2_000_000), TimeSpan.FromMilliseconds(500));
        Assert.That(result.Status, Is.EqualTo(ToolchainStatus.TimedOut), result.Diagnostic);
        Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(8)));
    }

    [Test]
    public async Task OutputLimit_IsAggregateBounded_AndNotAUsablePartialResult()
    {
        var result = await ToolchainProcess.RunAsync(Dotnet, Arguments("flood"), _root,
            maxOutputCharacters: 12345);
        Assert.That(result.Status, Is.EqualTo(ToolchainStatus.OutputLimitExceeded), result.Diagnostic);
        Assert.That(result.StandardOutput.Length + result.StandardError.Length, Is.EqualTo(12345));
    }

    [Test]
    public async Task Timeout_TerminatesTheChildProcessTree()
    {
        var pidPath = Path.Combine(_root, "child.pid");
        var result = await ToolchainProcess.RunAsync(Dotnet, Arguments("tree", pidPath), _root,
            timeout: TimeSpan.FromSeconds(2));
        Assert.That(result.Status, Is.EqualTo(ToolchainStatus.TimedOut), result.Diagnostic);
        Assert.That(File.Exists(pidPath), Is.True, "The helper must have started its child before the deadline.");
        AssertStopped(int.Parse(File.ReadAllText(pidPath)));
    }

    [Test]
    [Platform("Win")]
    public async Task ExitedLauncher_DescendantHoldingPipes_IsStillTerminated()
    {
        var pidPath = Path.Combine(_root, "exited-child.pid");
        try
        {
            var result = await ToolchainProcess.RunAsync(Dotnet, Arguments("exited-tree", pidPath), _root,
                timeout: TimeSpan.FromSeconds(2));
            Assert.That(result.Status, Is.EqualTo(ToolchainStatus.TimedOut), result.Diagnostic);
            Assert.That(File.Exists(pidPath), Is.True);
            AssertStopped(int.Parse(File.ReadAllText(pidPath)));
        }
        finally
        {
            // A failing containment regression must not leave the test's known child behind.
            if (File.Exists(pidPath))
            {
                try
                {
                    using var child = Process.GetProcessById(int.Parse(File.ReadAllText(pidPath)));
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
            }
        }
    }

    [Test]
    public async Task EnvironmentOverrides_AreChildLocal()
    {
        var result = await ToolchainProcess.RunAsync(Dotnet, Arguments("environment", "COP_TEST_TOOLCHAIN"), _root,
            environment: new Dictionary<string, string?> { ["COP_TEST_TOOLCHAIN"] = "exact-child-value" });
        Assert.That(result.Status, Is.EqualTo(ToolchainStatus.Success), result.Diagnostic);
        Assert.That(result.StandardOutput, Is.EqualTo("exact-child-value"));
        Assert.That(Environment.GetEnvironmentVariable("COP_TEST_TOOLCHAIN"), Is.Null);
    }

    [Test]
    public async Task Cancellation_PropagatesRatherThanReturningFallback()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ToolchainProcess.RunAsync(Dotnet, Arguments("sleep"), _root, cancellationToken: cancel.Token));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Scope_ReusesOneSerializedWorker_AndDisposesOnExit()
    {
        int pid;
        using (var scope = new ToolchainExecutionScope())
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
                ToolchainExecutionScope.RequestAsync(Dotnet, Arguments("worker"), _root, $"{{\"value\":{i}}}")));
            var values = responses.Select(ParseResponse).ToArray();
            pid = values[0].Pid;
            Assert.That(values.Select(v => v.Pid), Is.EqualTo(Enumerable.Repeat(pid, 5)));
            Assert.That(values.Select(v => v.Count).Order(), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
        }
        AssertStopped(pid);
    }

    [Test]
    public async Task OutsideScope_DoesNotLeaveTheWorkerRunning()
    {
        var first = ParseResponse(await ToolchainExecutionScope.RequestAsync(Dotnet, Arguments("worker"), _root, "{}"));
        AssertStopped(first.Pid);
        var second = ParseResponse(await ToolchainExecutionScope.RequestAsync(Dotnet, Arguments("worker"), _root, "{}"));
        Assert.That(second.Count, Is.EqualTo(1));
        AssertStopped(second.Pid);
    }

    [TestCase("invalid-worker", ToolchainStatus.Failed, 4096)]
    [TestCase("oversized-worker", ToolchainStatus.OutputLimitExceeded, 1024)]
    [TestCase("stderr-worker", ToolchainStatus.OutputLimitExceeded, 4096)]
    [TestCase("slow-worker", ToolchainStatus.TimedOut, 4096)]
    public async Task WorkerFailure_IsSticky_NotSilentlyRestarted(string mode, ToolchainStatus expected, int limit)
    {
        using var worker = new ToolchainJsonWorker(Dotnet, Arguments(mode), _root);
        var first = await worker.RequestAsync("{}", TimeSpan.FromSeconds(2), limit);
        Assert.That(first.Status, Is.EqualTo(expected), first.Diagnostic);
        Assert.That(first.StandardError.Length, Is.LessThanOrEqualTo(65536));
        var second = await worker.RequestAsync("{}", TimeSpan.FromSeconds(2), limit);
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public async Task Scope_ExceptionStillCleansUpWorker()
    {
        int pid = 0;
        try
        {
            using var scope = new ToolchainExecutionScope();
            pid = ParseResponse(await ToolchainExecutionScope.RequestAsync(Dotnet, Arguments("worker"), _root, "{}")).Pid;
            throw new InvalidOperationException("test failure");
        }
        catch (InvalidOperationException ex) when (ex.Message == "test failure") { }
        AssertStopped(pid);
    }

    private static (int Pid, int Count) ParseResponse(ToolchainResult result)
    {
        Assert.That(result.Status, Is.EqualTo(ToolchainStatus.Success), result.Diagnostic);
        using var json = JsonDocument.Parse(result.StandardOutput);
        return (json.RootElement.GetProperty("pid").GetInt32(), json.RootElement.GetProperty("count").GetInt32());
    }

    private static void AssertStopped(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.That(process.HasExited || process.WaitForExit(2000), Is.True, $"Toolchain process {pid} survived cleanup.");
        }
        catch (ArgumentException) { }
    }
}
