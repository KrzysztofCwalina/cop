using System.Diagnostics;
using System.Text.Json;
using Cop.Cli.Commands;
using Cop.Core;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class ChangeReportingTests
{
    private string _root = "";
    private string _scripts = "";
    private static string Packages
    {
        get
        {
            var directory = TestContext.CurrentContext.TestDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "cop.sln")))
                directory = Path.GetDirectoryName(directory);
            return Path.Combine(directory ?? throw new InvalidOperationException("Repository not found."), "packages");
        }
    }

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "change-report-" + Guid.NewGuid().ToString("N"));
        _scripts = Path.Combine(_root, "checks");
        Directory.CreateDirectory(_scripts);
        Git("init", "-b", "main");
        Git("config", "user.name", "Scope tests");
        Git("config", "user.email", "scope@example.invalid");
        File.WriteAllText(Path.Combine(_root, "Changed.cs"), "public class Changed { }\n");
        File.WriteAllText(Path.Combine(_root, "Unchanged.cs"), "public class Unchanged { }\n");
        File.WriteAllText(Path.Combine(_scripts, "main.cop"), $$"""
            feed '{{Packages.Replace('\\', '/')}}'
            import code
            let findings = [
                Violation { File = 'Changed.cs', Line = 1, Message = 'changed', Severity = 1.0, Certainty = 1.0, Source = 'fixture', Check = '' }
                Violation { File = 'Unchanged.cs', Line = 1, Message = 'unchanged', Severity = 1.0, Certainty = 1.0, Source = 'fixture', Check = '' }
                Violation { File = '', Line = 0, Message = 'global', Severity = 1.0, Certainty = 1.0, Source = 'fixture', Check = '' }
            ]
            command MAIN = CHECK(findings)
            command RAW = findings
            command CLEAN = CHECK([])
            """);
        Git("add", ".");
        Git("commit", "-qm", "baseline");
        File.WriteAllText(Path.Combine(_root, "Changed.cs"), "public class Changed { public int Value; }\n");
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    [TestCase("MAIN")]
    [TestCase("RAW")]
    public void ChangedReporting_FiltersBeforeFormattingAndCounting(string command)
    {
        var result = Engine.Run(_scripts, _root, commandName: command,
            additionalFeedPaths: [Packages], reviewOptions: Scoped());
        Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
        Assert.That(result.ParseErrors, Is.Empty);
        Assert.That(result.ExitCode, Is.EqualTo(2));
        Assert.That(result.Outputs.Select(output => output.Message).ToArray(),
            Is.EqualTo(new[] { "Changed.cs(1): error: changed", "(0): error: global" }));
    }

    [Test]
    public void DefaultReporting_DoesNotResolveGitAndRetainsAllFindings()
    {
        var result = Engine.Run(_scripts, _root, additionalFeedPaths: [Packages]);
        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.ExitCode, Is.EqualTo(3));
        Assert.That(result.Outputs.Select(output => output.Message).ToArray(),
            Is.EqualTo(new[] { "Changed.cs(1): error: changed", "Unchanged.cs(1): error: unchanged", "(0): error: global" }));
    }

    [Test]
    public void DefaultReporting_PreservesAbsentExternalResultsBehavior()
    {
        File.WriteAllText(Path.Combine(_scripts, "main.cop"), "import code\ncommand MAIN = CHECK(nic)");
        var result = Engine.Run(_scripts, _root, additionalFeedPaths: [Packages]);
        Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
        Assert.That(result.Outputs, Is.Empty);
        Assert.That(result.ExitCode, Is.Null);
    }

    [Test]
    public void ScopedReporting_PreservesFullSemanticInput()
    {
        File.WriteAllText(Path.Combine(_scripts, "main.cop"), $$"""
            feed '{{Packages.Replace('\\', '/')}}'
            import code
            import csharp
            let cb = csharp.parse()
            predicate fullModel(Type) => cb.Types.Count == 2
            let findings = cb.Types:fullModel:toError('full-model')
            command MAIN = CHECK(findings)
            """);
        var result = Engine.Run(_scripts, _root, additionalFeedPaths: [Packages], reviewOptions: Scoped());
        Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Outputs.Select(output => output.Message).ToArray(),
            Is.EqualTo(new[] { "Changed.cs(1): error: full-model" }));
    }

    [Test]
    public void InvalidComparison_IsFatalEvenWhenThereAreNoFindings()
    {
        var result = Engine.Run(_scripts, _root, additionalFeedPaths: [Packages],
            reviewOptions: new ReviewOptions { Scope = "branch", Base = "missing-ref", ReportScope = "changes" });
        Assert.That(result.HasFatalErrors, Is.True);
        Assert.That(result.Outputs, Is.Empty);
    }

    [Test]
    public void ScopedReporting_UsesAbsoluteProvenanceForParsedSubroots()
    {
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "Changed.cs"), "public class Nested { }\n");
        File.WriteAllText(Path.Combine(_scripts, "main.cop"), $$"""
            feed '{{Packages.Replace('\\', '/')}}'
            import code
            import csharp
            let cb = csharp.parse('nested')
            command MAIN = CHECK(cb.Types:toError('nested finding'))
            """);
        Git("add", ".");
        Git("commit", "-qm", "nested baseline");
        File.WriteAllText(Path.Combine(nested, "Changed.cs"), "public class Nested { public int Value; }\n");
        var result = Engine.Run(_scripts, _root, additionalFeedPaths: [Packages], reviewOptions: Scoped());
        Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(result.Outputs.Select(output => output.Message),
            Is.EqualTo(new[] { "Changed.cs(1): error: nested finding" }));
    }

    [TestCase("MAIN")]
    [TestCase("RAW")]
    public void LaterCleanCommand_DoesNotEraseScopedFailure(string command)
    {
        var result = Engine.Run(_scripts, _root, commandFilter: [command, "CLEAN"],
            additionalFeedPaths: [Packages], reviewOptions: Scoped());
        Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
        Assert.That(result.ExitCode, Is.EqualTo(2));
        Assert.That(result.Outputs.Select(output => output.Message).ToArray(),
            Is.EqualTo(new[] { "Changed.cs(1): error: changed", "(0): error: global" }));
    }

    [TestCase("MAIN")]
    [TestCase("RAW")]
    public async Task Cli_JsonAndExitCodeUseTheFilteredFindings(string command)
    {
        var result = await RunCli(Path.Combine(_scripts, "main.cop"), ["-t", _root, "-c", command,
            "--scope", "branch", "--base", "main", "--report-scope", "changes", "-f", "json"]);
        Assert.That(result.ExitCode, Is.EqualTo(1), result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.That(json.RootElement.GetArrayLength(), Is.EqualTo(2));
        Assert.That(result.Stdout, Does.Contain("changed").And.Contain("global").And.Not.Contain("unchanged"));
    }

    [Test]
    public async Task Cli_NamedCommandsShareScopeAndTargetParsing()
    {
        var result = await RunCli("RAW", ["-t", _root, "--scope=branch", "--base=main",
            "--report-scope=changes", "-f", "json"], workingDirectory: _scripts);
        Assert.That(result.ExitCode, Is.EqualTo(1), result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.That(json.RootElement.EnumerateArray().Select(item => item.GetProperty("message").GetString()),
            Is.EqualTo(new[] { "Changed.cs(1): error: changed", "(0): error: global" }));
    }

    [Test]
    public async Task Cli_OnlyIfModifiedGateSkipsRemoteScriptsOnCleanTarget()
    {
        File.WriteAllText(Path.Combine(_root, "Changed.cs"), "public class Changed { }\n");
        var result = await RunCli("https://127.0.0.1:1/never-download.cop",
            ["-t", _root, "-om", "--scope", "full"], _scripts);
        Assert.That(result.ExitCode, Is.EqualTo(0), result.Stderr);
        Assert.That(result.Stdout, Is.Empty);
    }

    [TestCase("--scope")]
    [TestCase("--scope=invalid")]
    [TestCase("--report-scope=invalid")]
    [TestCase("--scope=full --report-scope=changes")]
    [TestCase("--scope=branch --pr=1")]
    [TestCase("--scope=branch --scope=full")]
    [TestCase("--ai-config")]
    [TestCase("--ai-config=")]
    [TestCase("--ai-config=first.json --ai-config=second.json")]
    public void InvalidScopeOptions_AreRejected(string options)
    {
        Assert.Throws<ArgumentException>(() => ReviewArguments.Parse(options.Split(' ')));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AiConfigurationOption_PreservesPathsAndOtherRunArguments(bool equalsForm)
    {
        var config = Path.Combine(_root, "private config", "ai.json");
        string[] args = equalsForm
            ? ["main.cop", "--scope=full", $"--ai-config={config}", "-t", _root]
            : ["main.cop", "--scope", "full", "--ai-config", config, "-t", _root];

        var parsed = ReviewArguments.Parse(args);

        Assert.That(parsed.Arguments, Is.EqualTo(new[] { "main.cop", "-t", _root }));
        Assert.That(parsed.Options.AiConfigPath, Is.EqualTo(config));
        Assert.That(parsed.Options.Scope, Is.EqualTo("full"));
        Assert.That(ReviewArguments.Parse(["main.cop"]).Options.AiConfigPath, Is.Null);
        Assert.Throws<ArgumentException>(() => new ReviewOptions { AiConfigPath = " " }.Validate());
    }

    private static ReviewOptions Scoped() => new() { Scope = "branch", Base = "main", ReportScope = "changes" };

    private void Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        Assert.That(process.WaitForExit(30_000), Is.True, "Git timed out.");
        Assert.That(process.ExitCode, Is.EqualTo(0), error);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCli(
        string target, string[] arguments, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(target)!,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(RunCommand).Assembly.Location);
        start.ArgumentList.Add(target);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--no-user-checks");
        start.ArgumentList.Add("--no-color");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout, await stderr);
    }
}
