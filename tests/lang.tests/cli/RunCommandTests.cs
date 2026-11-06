using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests.Lang.Cli;

[TestFixture]
[NonParallelizable]
public class RunCommandTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cop-run-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProgramAsync(
        string source, string? format = null)
    {
        var dir = NewTempDir();
        var previousCwd = Directory.GetCurrentDirectory();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        try
        {
            var file = Path.Combine(dir, "program.cop");
            File.WriteAllText(file, source);
            Directory.SetCurrentDirectory(dir);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            Console.SetOut(stdout);
            Console.SetError(stderr);

            var exitCode = await RunCommand.ExecuteAsync(file, new RunOptions
            {
                Target = dir,
                OutputFormat = format
            });
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Directory.SetCurrentDirectory(previousCwd);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Test]
    public async Task ForeachReportOutput_ExitsZero()
    {
        var result = await RunProgramAsync("foreach [1 2] => 'n={item}'");

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(NormalizeLines(result.Stdout).Trim(), Is.EqualTo("n=1\nn=2"));
    }

    [Test]
    public async Task JsonReportOutput_ExitsZero()
    {
        var result = await RunProgramAsync("foreach [1 2] => 'n={item}'", format: "json");

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(result.Stdout, Does.Contain("\"message\": \"n=1\""));
        Assert.That(result.Stdout, Does.Contain("\"message\": \"n=2\""));
    }

    [Test]
    public async Task CheckWithViolations_ExitsOne()
    {
        var result = await RunProgramAsync("""
            import code
            let violations = [Violation { Severity = 1.0, Certainty = 1.0, Message = 'bad', File = 'program.cop', Line = 1, Source = 'x' }]
            command MAIN = CHECK(violations)
            """);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(result.Stdout, Does.Contain("bad"));
    }

    [Test]
    public async Task FatalRuntimeError_ExitsTwo()
    {
        var result = await RunProgramAsync("command MAIN = noSuchFunction()");

        Assert.That(result.ExitCode, Is.EqualTo(2), Describe(result));
        Assert.That(result.Stderr, Does.Contain("noSuchFunction"));
    }

    [TestCase("1 + 2", "3")]
    [TestCase("'Hello World'", "Hello World")]
    public async Task BareTopLevelExpression_PrintsAndExitsZero(
        string source, string expectedOutput)
    {
        var result = await RunProgramAsync(source);

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(result.Stdout.Trim(), Is.EqualTo(expectedOutput));
    }

    private static string NormalizeLines(string value) => value.Replace("\r\n", "\n");

    private static string Describe((int ExitCode, string Stdout, string Stderr) result) =>
        $"ExitCode: {result.ExitCode}\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}";
}
