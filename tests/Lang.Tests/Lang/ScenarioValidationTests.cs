using Cop.Lang;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class ScenarioValidationTests
{
    private static string GetSamplesDir(string sampleName)
    {
        // Navigate from test output to repo root: bin/Debug/net10.0 → tests/Checks.Tests → repo root
        var testDir = TestContext.CurrentContext.TestDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var samplesDir = Path.Combine(repoRoot, "samples", sampleName);
        Assert.That(Directory.Exists(samplesDir), Is.True,
            $"Samples directory not found at: {samplesDir}");
        return samplesDir;
    }

    [Test]
    public void S1_HelloWorld_DetectsVarUsage()
    {
        // Arrange: samples/s1-HelloWorld has rules.cop and app.cs
        // rules.cop: import code, usesVar predicate, foreach with file path and line
        // app.cs: "var x = 1;" on line 1
        var sampleDir = GetSamplesDir("s1-HelloWorld");

        // Act: Run the check engine against the sample directory
        var result = Engine.Run(sampleDir, sampleDir);

        // Assert: No fatal errors or parse errors
        Assert.That(result.Errors, Is.Empty,
            $"Fatal errors: {string.Join("; ", result.Errors)}");
        Assert.That(result.ParseErrors, Is.Empty,
            $"Parse errors: {string.Join("; ", result.ParseErrors)}");

        // Assert: At least one output about 'var' usage
        Assert.That(result.Outputs, Has.Count.GreaterThan(0),
            "Expected at least one output for 'var' usage");

        // Assert: Output message contains file path and line number
        var diag = result.Outputs[0];
        Assert.That(diag.Message, Does.Contain("app.cs"),
            "Output should reference the source file");
        Assert.That(diag.Message, Does.Contain("uses 'var'"),
            "Output should describe the var usage violation");

        // Assert: Line number is part of the message template (user controls the format)
        Assert.That(diag.Message, Does.Contain("1"),
            "Message should contain line number 1 where 'var x = 1;' is");
    }

    [Test]
    public void S1_HelloWorld_ImportCodeResolvesTypes()
    {
        // Verify that 'import code' properly resolves the code package
        // and types (Statement, etc.) are available for predicate evaluation
        var sampleDir = GetSamplesDir("s1-HelloWorld");

        var result = Engine.Run(sampleDir, sampleDir);

        // If import resolution failed, there would be errors or no outputs
        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.Outputs, Has.Count.GreaterThan(0),
            "Import code must resolve successfully for outputs to be produced");
    }

    [Test]
    public void S1_HelloWorld_PrintInterpolation()
    {
        // Verify that {item.File.Path}:{item.Line} interpolation works
        var sampleDir = GetSamplesDir("s1-HelloWorld");

        var result = Engine.Run(sampleDir, sampleDir);

        Assert.That(result.Outputs, Has.Count.GreaterThan(0));
        var message = result.Outputs[0].Message;

        // The template is: 'ERROR: {item.File.Path}:{item.Line} uses \'var\''
        // Should interpolate to something like: "ERROR: app.cs:1 uses 'var'"
        Assert.That(message, Does.Match(@"app\.cs.*1.*uses 'var'"),
            $"Message should contain interpolated file path and line. Got: {message}");
    }
}
