using System.Text.Json;
using NUnit.Framework;

namespace Cop.Tests;

public partial class ToolchainCliIntegrationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RedirectedSummaryIsPlainAndSingularWithoutSetupNoise(bool noColor)
    {
        File.WriteAllText(_script, "command MAIN = print('hello')");
        var args = new List<string> { _script, "-t", _root };
        if (noColor) args.Add("--no-color");

        var result = await Run(args.ToArray());

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput.Trim(), Is.EqualTo("hello"));
        Assert.That(result.StandardError, Does.StartWith("  Summary   0 input files processed, 1 computation evaluated in ")
            .And.Not.Contain("Packages").And.Not.Contain("Analyze"));
        Assert.That(result.StandardError.Contains('\x1b'), Is.False);
        AssertExecutionSummary(result.StandardError, 0, 1);
    }

    [Test]
    public async Task SingleCheckKeepsSingularCheckSummary()
    {
        var repository = new DirectoryInfo(Path.GetDirectoryName(_cli)!);
        while (!File.Exists(Path.Combine(repository.FullName, "cop.sln"))) repository = repository.Parent!;
        var feed = Path.Combine(repository.FullName, "packages").Replace("\\", "\\\\");
        File.WriteAllText(_script, $$"""
            feed '{{feed}}'
            import code
            let single : [Violation] = []
            command MAIN = CHECK(single)
            """);

        var verify = await Run("verify", _script);
        Assert.That(verify.ExitCode, Is.EqualTo(0), verify.StandardError);
        var result = await Run(_script, "-t", _root, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput, Is.Empty);
        Assert.That(result.StandardError, Does.StartWith("  Summary   0 input files processed, 1 check evaluated in "));
    }

    [TestCase("text")]
    [TestCase("json")]
    public async Task PackageRunPreservesProgramOutputAndSummaryRouting(string format)
    {
        var package = Path.Combine(_root, "packages", "presentation-fixture");
        Directory.CreateDirectory(Path.Combine(package, "src"));
        File.WriteAllText(Path.Combine(package, "cop.json"),
            """{"name":"presentation-fixture","version":"1.0.0","title":"Fixture","description":"Fixture","authors":"tests"}""");
        File.WriteAllText(Path.Combine(package, "src", "main.cop"), "command MAIN = print('hello')");

        var verify = await Run("verify", Path.Combine(package, "src"));
        Assert.That(verify.ExitCode, Is.EqualTo(0), verify.StandardError);
        var result = await Run("run", "presentation-fixture", "-t", _root, "-f", format);

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        if (format == "json")
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            Assert.That(json.RootElement.GetArrayLength(), Is.EqualTo(1));
            Assert.That(json.RootElement[0].GetProperty("message").GetString(), Is.EqualTo("hello"));
            Assert.That(result.StandardError, Is.Empty);
        }
        else
        {
            Assert.That(result.StandardOutput.Trim(), Is.EqualTo("hello"));
            AssertExecutionSummary(result.StandardError, 0, 1);
        }
    }

    [Test]
    public async Task SingleRealInputFileUsesSingularSummary()
    {
        var repository = new DirectoryInfo(Path.GetDirectoryName(_cli)!);
        while (!File.Exists(Path.Combine(repository.FullName, "cop.sln"))) repository = repository.Parent!;
        var feed = Path.Combine(repository.FullName, "packages").Replace("\\", "\\\\");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "example.cs"), "// Single input fixture.\n");
        File.WriteAllText(_script, $$"""
            feed '{{feed}}'
            import csharp
            command MAIN = print(csharp.parse().Files.Count)
            """);

        var verify = await Run("verify", _script);
        Assert.That(verify.ExitCode, Is.EqualTo(0), verify.StandardError);
        var result = await Run(_script, "-t", target);

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput.Trim(), Is.EqualTo("1"));
        Assert.That(result.StandardError, Does.StartWith("  Summary   1 input file processed, "));
    }

    [TestCase(false, 0)]
    [TestCase(true, 1)]
    public async Task CheckSummaryCountsCleanAndFailingComposedRulesNotHelpersOrAliases(bool finding, int exitCode)
    {
        var repository = new DirectoryInfo(Path.GetDirectoryName(_cli)!);
        while (!File.Exists(Path.Combine(repository.FullName, "cop.sln"))) repository = repository.Parent!;
        var feed = Path.Combine(repository.FullName, "packages").Replace("\\", "\\\\");
        File.WriteAllText(_script, $$"""
            feed '{{feed}}'
            import code
            function positives(values: [int]) : [Violation] =>
                values.Where(item > 0).Select(Violation {
                    Severity: 1.0, Certainty: 1.0, Message: 'finding',
                    File: '', Line: 0, Source: '', Check: 'fixture'
                })
            let clean = positives([-1])
            let other = positives([{{(finding ? "1" : "-2")}}])
            export function suite() : [Violation] => clean + other
            let unused = positives([1])
            let helper = 42
            let result = suite()
            let alias = result
            command MAIN = print(helper) & CHECK(alias + clean)
            """);

        var result = await Run(_script, "-t", _root, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(exitCode), result.StandardError);
        Assert.That(result.StandardOutput.Split('\n').Count(line => line.Contains("error: finding")),
            Is.EqualTo(finding ? 1 : 0));
        Assert.That(result.StandardError, Does.Contain("0 input files processed, 2 checks evaluated in ")
            .And.Not.Contain("commands executed"));
    }

    [Test]
    public async Task CheckSummaryCountsDistinctChecksAndExcludesUnusedProviderWork()
    {
        var repository = new DirectoryInfo(Path.GetDirectoryName(_cli)!);
        while (!File.Exists(Path.Combine(repository.FullName, "cop.sln"))) repository = repository.Parent!;
        var feed = Path.Combine(repository.FullName, "packages").Replace("\\", "\\\\");
        File.WriteAllText(_script, $$"""
            feed '{{feed}}'
            import code
            import csharp
            let unused = csharp.parse().Statements:toError('unused')
            let first : [Violation] = []
            let alias = first
            let second : [Violation] = []
            command MAIN = CHECK(first + alias + second)
            """);

        var result = await Run(_script, "-t", _root, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput, Is.Empty);
        Assert.That(result.StandardError, Does.Contain("0 input files processed, 2 checks evaluated in "));
    }

    [Test]
    public async Task ExplicitObservationDoesNotEnumerateADeferredProviderCollection()
    {
        WriteScript("""
            function observe(value: T, category: string, name: string) : T => intrinsic
            function ignore(value: object) => 'ready'
            command MAIN = print(ignore(observe(Items, 'probe', 'fixture/items')))
            """);

        var result = await Run(_script, "-t", _root, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput.Trim(), Is.EqualTo("ready"));
        Assert.That(result.StandardError, Does.Contain("0 input files processed, ")
            .And.Not.Contain("checks evaluated"));
        Assert.That(File.Exists(Path.Combine(_root, "pids.txt")), Is.False);
    }

    [TestCase(false, 57)]
    [TestCase(true, 58)]
    public async Task BuiltInCSharpSuiteCountsLeafFunctionsAndDoesNotDoubleCountCompatibilityAliases(
        bool includeOptional, int checks)
    {
        var repository = new DirectoryInfo(Path.GetDirectoryName(_cli)!);
        while (!File.Exists(Path.Combine(repository.FullName, "cop.sln"))) repository = repository.Parent!;
        var feed = Path.Combine(repository.FullName, "packages").Replace("\\", "\\\\");
        File.WriteAllText(_script, $$"""
            feed '{{feed}}'
            import code
            import csharp
            import csharp-checks
            let cb = codebase(csharp.parse())
            command MAIN = CHECK(csharpChecks(cb){{(includeOptional
                ? " + avoidBoolParams(cb) + boolParameterNaming(cb) + emptyCatches(cb) + emptyCatches(cb)"
                : "")}})
            """);

        var result = await Run(_script, "-t", _root, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput, Is.Empty);
        Assert.That(result.StandardError, Does.Contain($"0 input files processed, {checks} checks evaluated in "));
    }

    [TestCase("MAIN", 65)]
    [TestCase("DERIVED", 29)]
    [TestCase("MAIN,DERIVED", 94)]
    public async Task RepositorySuitesCountOnlySelectedChecksOnAnAllCleanRealInput(string commands, int checks)
    {
        var repository = new DirectoryInfo(Path.GetDirectoryName(_cli)!);
        while (!File.Exists(Path.Combine(repository.FullName, "cop.sln"))) repository = repository.Parent!;
        var target = Path.Combine(_root, "clean-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "clean.cs"), "// Clean fixture.\n");

        var result = await Run(Path.Combine(repository.FullName, "cop-checks", "main.cop"),
            "-t", target, "-c", commands, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
        Assert.That(result.StandardOutput, Is.Empty);
        Assert.That(result.StandardError, Does.Contain($", {checks} checks evaluated in "));
    }

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
        Assert.That(result.StandardError, Does.Not.Contain("  Summary   ").And.Contain("missingFunction"));
    }

    private static void AssertExecutionSummary(string stderr, int files, int commands)
        => TestTextParsing.AssertExecutionSummary(stderr, files, commands);
}
