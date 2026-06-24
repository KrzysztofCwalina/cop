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

    private static (int ExitCode, string Stdout, string Stderr) RunProgram(string source, string? format = null)
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

            var exitCode = RunCommand.Execute(file, target: dir, format: format);
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
    public void ForeachReportOutput_ExitsZero()
    {
        var result = RunProgram("foreach [1 2] => 'n={item}'");

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(NormalizeLines(result.Stdout).Trim(), Is.EqualTo("n=1\nn=2"));
    }

    [Test]
    public void JsonReportOutput_ExitsZero()
    {
        var result = RunProgram("foreach [1 2] => 'n={item}'", format: "json");

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(result.Stdout, Does.Contain("\"message\": \"n=1\""));
        Assert.That(result.Stdout, Does.Contain("\"message\": \"n=2\""));
    }

    [Test]
    public void CheckWithViolations_ExitsOne()
    {
        var result = RunProgram("""
            import code
            let violations = [Violation { Severity = 1.0, Certainty = 1.0, Message = 'bad', File = 'program.cop', Line = 1, Source = 'x' }]
            command MAIN = CHECK(violations)
            """);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(result.Stdout, Does.Contain("bad"));
    }

    [Test]
    public void FatalRuntimeError_ExitsTwo()
    {
        var result = RunProgram("command MAIN = noSuchFunction()");

        Assert.That(result.ExitCode, Is.EqualTo(2), Describe(result));
        Assert.That(result.Stderr, Does.Contain("noSuchFunction"));
    }

    [TestCase("1 + 2", "3")]
    [TestCase("'Hello World'", "Hello World")]
    public void BareTopLevelExpression_PrintsAndExitsZero(string source, string expectedOutput)
    {
        var result = RunProgram(source);

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(result.Stdout.Trim(), Is.EqualTo(expectedOutput));
    }

    private static string NormalizeLines(string value) => value.Replace("\r\n", "\n");

    private static string Describe((int ExitCode, string Stdout, string Stderr) result) =>
        $"ExitCode: {result.ExitCode}\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}";
}
