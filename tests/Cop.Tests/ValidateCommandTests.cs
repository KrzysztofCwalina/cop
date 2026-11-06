using System.Text.Json;
using Cop.Cli.Commands;
using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
[NonParallelizable]
public sealed class ValidateCommandTests
{
    [Test]
    public async Task SampleValidationPreservesOtherInvocationsTemporaryFiles()
    {
        string id = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), $"cop-validation-{id}");
        string package = Path.Combine(root, "widget");
        string sampleName = $"sample-{id}";
        string otherInvocation = Path.Combine(Path.GetTempPath(), "cop-validate", sampleName);
        string sentinel = Path.Combine(otherInvocation, "sentinel.txt");
        TextWriter previousOutput = Console.Out;
        TextWriter previousError = Console.Error;
        using var output = new StringWriter();
        using var errors = new StringWriter();
        try
        {
            foreach (string directory in new[] { "instructions", "skills", "src", "tests", "samples" })
            {
                Directory.CreateDirectory(Path.Combine(package, directory));
            }
            File.WriteAllText(Path.Combine(package, PackageMetadata.MetadataFileName),
                JsonSerializer.Serialize(new
                {
                    name = "widget",
                    version = "1.2.3",
                    title = "Widget",
                    description = "Validation test",
                    authors = "Cop Tests"
                }));
            File.WriteAllText(Path.Combine(package, "src", "main.cop"),
                "export let number = 7");
            File.WriteAllText(Path.Combine(package, "samples", $"{sampleName}.cop"),
                "command MAIN = print('sample executed')");
            Directory.CreateDirectory(otherInvocation);
            File.WriteAllText(sentinel, "owned by another invocation");
            Console.SetOut(output);
            Console.SetError(errors);

            int exitCode = await ValidateCommand.ExecuteAsync(package);

            Assert.That(exitCode, Is.EqualTo(0), errors.ToString() + output);
            Assert.That(File.Exists(sentinel), Is.True);
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("owned by another invocation"));
        }
        finally
        {
            Console.SetOut(previousOutput);
            Console.SetError(previousError);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            if (Directory.Exists(otherInvocation))
            {
                Directory.Delete(otherInvocation, recursive: true);
            }
        }
    }
}
