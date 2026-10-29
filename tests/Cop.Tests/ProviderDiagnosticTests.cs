using Cop.Cli.Commands;
using Cop.Core;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
[NonParallelizable]
public class ProviderDiagnosticTests
{
    [SetUp]
    public void SetUp()
    {
        ProviderWarnings.Clear();
        ProviderErrors.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        ProviderWarnings.Clear();
        ProviderErrors.Clear();
    }

    [Test]
    public async Task FallbackWarnings_DeduplicateWithinScope_AcrossConcurrentDeferredWork()
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            ProviderWarnings.ReportOnce("python", "target", "CPython unavailable; using simpler analysis."))));
        Assert.That(ProviderWarnings.Drain(), Is.EqualTo(new[]
        {
            "Warning: python: CPython unavailable; using simpler analysis."
        }));
        ProviderWarnings.ReportOnce("python", "target", "CPython unavailable; using simpler analysis.");
        Assert.That(ProviderWarnings.Drain(), Is.Empty, "Draining must not reset run-scoped deduplication.");
        ProviderWarnings.Clear();
        ProviderWarnings.ReportOnce("python", "target", "CPython unavailable; using simpler analysis.");
        Assert.That(ProviderWarnings.Drain().Count, Is.EqualTo(1), "A new execution must warn again.");
    }

    [Test]
    public async Task IndependentExecutionContexts_DoNotStealWarnings()
    {
        async Task<string[]> Run(string provider)
        {
            ProviderWarnings.Clear();
            await Task.Yield();
            ProviderWarnings.ReportOnce(provider, "scope", "fallback");
            await Task.Yield();
            return ProviderWarnings.Drain().ToArray();
        }
        var results = await Task.WhenAll(Run("python"), Run("javascript"));
        Assert.That(results[0], Is.EqualTo(new[] { "Warning: python: fallback" }));
        Assert.That(results[1], Is.EqualTo(new[] { "Warning: javascript: fallback" }));
        Assert.That(ProviderWarnings.Drain(), Is.Empty);
    }

    [Test]
    public void IncompleteAnalysis_CannotBeOverriddenBySuccessfulCheckExitCode()
    {
        var result = new EngineResult([], [], [], Warnings:
            ["Error: 1 source analysis error(s) — the analysis model may be incomplete:", "source.py(3): invalid syntax"],
            ExitCode: 0);
        var (exit, stderr) = Handle(result);
        Assert.That(exit, Is.EqualTo(1));
        Assert.That(stderr, Does.Contain("source.py(3): invalid syntax"));
    }

    [Test]
    public void MissingOptionalTool_WarningDoesNotFailSuccessfulFallback()
    {
        var result = new EngineResult([], [], [], Warnings:
            ["Warning: python: CPython unavailable; using simpler analysis."], ExitCode: 0);
        var (exit, stderr) = Handle(result);
        Assert.That(exit, Is.EqualTo(0));
        Assert.That(stderr.Trim(), Is.EqualTo(result.Warnings![0]));
    }

    [Test]
    public void WarningColor_IsYellow_AndPlainWhenColorDisabled()
    {
        const string message = "Warning: tool unavailable; using simpler analysis.";
        Assert.That(RunCommand.FormatProviderWarning(message, true), Is.EqualTo("\x1b[33m" + message + "\x1b[0m"));
        Assert.That(RunCommand.FormatProviderWarning(message, false), Is.EqualTo(message));
    }

    private static (int Exit, string Stderr) Handle(EngineResult result)
    {
        var previous = Console.Error;
        var noColor = ConsoleMarkdown.NoColor;
        using var stderr = new StringWriter();
        Console.SetError(stderr);
        ConsoleMarkdown.NoColor = true;
        try { return (RunCommand.HandleResult(result, null, Directory.GetCurrentDirectory()), stderr.ToString()); }
        finally
        {
            Console.SetError(previous);
            ConsoleMarkdown.NoColor = noColor;
        }
    }
}
