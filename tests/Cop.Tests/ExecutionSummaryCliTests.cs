using System.Globalization;
using System.Text.Json;
using NUnit.Framework;

namespace Cop.Tests;

public partial class ToolchainCliIntegrationTests
{
    [Test]
    public async Task SummaryCountsIndividualNamedComputationsIncludingEmptyResultsOnce()
    {
        File.WriteAllText(_script, """
            predicate positive(int) => item > 0
            let first = [-1]:positive
            let second = [-2]:positive
            let combined = first + second
            let unused = [99]:positive
            command MAIN = print(combined.Count) & print(combined.Count)
            """);

        var result = await Run(_script, "-t", _root, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput.Replace("\r\n", "\n").Trim(), Is.EqualTo("0\n0"));
        AssertExecutionSummary(result.StandardError, 0, 4);
    }

    [TestCase("selected")]
    [TestCase("selected,source")]
    public async Task RunningANamedBindingDoesNotCountItAgainAsAnEntryWrapper(string commands)
    {
        File.WriteAllText(_script, """
            let source = [1, 2]
            let selected = source + [3]
            let unused = [99]
            """);

        var result = await Run(_script, "-t", _root, "-c", commands, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        AssertExecutionSummary(result.StandardError, 0, 2);
    }

    [Test]
    public async Task GenericSummaryExcludesUnusedDeclarationsAndPreservesStdout()
    {
        File.WriteAllText(_script, """
            let unused = 42
            let anotherUnused = 99
            command ONE = print('one')
            command TWO = print('two')
            command UNUSED = print('not executed')
            """);

        var result = await Run(_script, "-t", _root, "-c", "ONE,TWO,ONE", "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput.Replace("\r\n", "\n").Trim(), Is.EqualTo("one\ntwo"));
        Assert.That(result.StandardError.Trim().Split('\n'), Has.Length.EqualTo(1));
        AssertExecutionSummary(result.StandardError, 0, 2);
    }

    [Test]
    public async Task SummaryCountsCallableShadowingBindingsOnce()
    {
        File.WriteAllText(_script, """
            function source() => [1, 2]
            let source = source()
            let selected = source + [3]
            command MAIN = print(selected.Count) & print(source.Count)
            """);

        var result = await Run(_script, "-t", _root, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput.Replace("\r\n", "\n").Trim(), Is.EqualTo("3\n2"));
        AssertExecutionSummary(result.StandardError, 0, 3);
    }

    [Test]
    public async Task SummaryCountsImportedBindingsOnceAcrossEntryCommands()
    {
        File.WriteAllText(Path.Combine(_root, "mode.txt"), "fallback");
        WriteScript("""
            function countItems() => Items.Count
            command ONE = print(countItems())
            command TWO = print(countItems())
            """);

        var result = await Run(_script, "-t", _root, "-c", "ONE,TWO", "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput.Replace("\r\n", "\n").Trim(), Is.EqualTo("2\n2"));
        AssertExecutionSummary(result.StandardError, 0, 4);
    }

    [Test]
    public async Task GenericSummaryIsOmittedFromJsonOutput()
    {
        File.WriteAllText(_script, "command MAIN = print('hello')");

        var result = await Run(_script, "-t", _root, "-f", "json", "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardError, Is.Empty);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.That(json.RootElement.GetArrayLength(), Is.EqualTo(1));
        Assert.That(json.RootElement[0].GetProperty("message").GetString(), Is.EqualTo("hello"));
    }

    [Test]
    public async Task SummaryDoesNotTurnAProgramFailureIntoSuccess()
    {
        File.WriteAllText(_script, "command MAIN = missingFunction()");

        var result = await Run(_script, "-t", _root, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(2), result.StandardError);
        Assert.That(result.StandardError, Does.Not.Contain("Summary:").And.Contain("missingFunction"));
    }

    private static void AssertExecutionSummary(string stderr, int files, int commands)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.TrimEnd('\r')).Single(value => value.StartsWith("Summary:", StringComparison.Ordinal));
        var prefix = $"Summary: {files} input file{(files == 1 ? "" : "s")} processed, " +
            $"{commands} command{(commands == 1 ? "" : "s")} executed in ";
        Assert.That(line, Does.StartWith(prefix).And.EndWith("s."));
        Assert.That(double.TryParse(line[prefix.Length..^2], NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var seconds), Is.True);
        Assert.That(seconds, Is.InRange(0, 30));
    }
}
