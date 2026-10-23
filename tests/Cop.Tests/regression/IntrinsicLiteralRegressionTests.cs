using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class IntrinsicLiteralRegressionTests
{
    [Test]
    public void Issue065_FunctionCanReturnIntrinsicStringLiteral()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cop-issue065-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "main.cop"), """
                function value() : string => 'intrinsic'
                command MAIN = print(value())
                """);
            var result = Engine.Run(directory, directory);
            Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "intrinsic" }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
