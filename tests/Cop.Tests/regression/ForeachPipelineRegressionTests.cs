using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class ForeachPipelineRegressionTests
{
    [Test]
    [Explicit("Issue #88: verified multiline command silently emits no output - remove when fixed")]
    [Category("PendingFix")]
    public void Issue088_MultilineCommandDoesNotSilentlyDropLiteralAndForeachOutput()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cop-issue088-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "main.cop"), """
                let values = [1, 2]
                command MAIN =
                    'count:{values.Count}'
                    foreach values => 'value:{item}'
                """);
            var result = Engine.Run(directory, directory);
            Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message),
                Is.EqualTo(new[] { "count:2", "value:1", "value:2" }));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    [Explicit("Issue #84: verified pipe-separated foreach silently emits no output - remove when fixed")]
    [Category("PendingFix")]
    public void Issue084_PipeSeparatedForeachCommandsProduceBothOutputs()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cop-issue084-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var program = Path.Combine(directory, "program");
            var input = Path.Combine(directory, "input");
            Directory.CreateDirectory(program);
            Directory.CreateDirectory(input);
            File.WriteAllText(Path.Combine(input, "input.cop"), """
                function invoke(value : string) => value:upper()
                function repeat(value : string) => invoke(invoke(value)) + invoke(value)
                command MAIN = repeat('sample')
                """);
            File.WriteAllText(Path.Combine(program, "main.cop"), """
                import code
                import cop
                let cb = cop.parse()
                command MAIN =
                    foreach cb.Methods => 'method:{item.Name}:{item.File.Path}' |
                    foreach cb.Calls => 'call:{item.MemberName}:method={item.Method.Name}'
                """);
            var result = Engine.Run(program, input);
            Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(new[]
            {
                "method:invoke:input.cop", "method:repeat:input.cop", "method:MAIN:input.cop",
                "call:upper:method=invoke",
                "call:invoke:method=repeat", "call:invoke:method=repeat",
                "call:invoke:method=repeat", "call:repeat:method=MAIN"
            }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
