using NUnit.Framework;
using Cop.Providers;

namespace Cop.Tests;

[TestFixture]
public class TypeSpecProviderTests
{
    private static string RepoRoot => Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));

    [Test]
    public void TypeSpecHttpProvider_PutPathSegments_FindsOddSegmentPut()
    {
        var scriptsDir = Path.Combine(RepoRoot, "samples", "s7-TypeSpec");
        var codebasePath = Path.Combine(scriptsDir, "spec");

        if (!Directory.Exists(scriptsDir) || !Directory.Exists(codebasePath))
            Assert.Ignore("Sample directory not found");

        var result = Engine.Run(scriptsDir, codebasePath);

        // Should have no fatal errors
        Assert.That(result.Errors, Is.Empty, $"Fatal errors: {string.Join("; ", result.Errors)}");

        // Should produce output — the PUT with odd segments (replaceParts at /widgets/{widgetId}/parts)
        var outputTexts = result.Outputs.Select(o => o.Content.ToPlainText()).ToList();
        Assert.That(outputTexts, Has.Count.GreaterThan(0), "Expected at least one warning output");

        // The odd-segment PUT is "replaceParts" at /widgets/{widgetId}/parts (3 segments)
        var replaceParts = outputTexts.FirstOrDefault(t => t.Contains("replaceParts"));
        Assert.That(replaceParts, Is.Not.Null, $"Expected warning for replaceParts. Outputs: {string.Join(" | ", outputTexts)}");

        // The even-segment PUTs (update, updatePart) should NOT appear
        var evenSegmentWarnings = outputTexts.Where(t => t.Contains("update") && !t.Contains("replaceParts")).ToList();
        Assert.That(evenSegmentWarnings, Is.Empty, $"Even-segment PUTs should not be flagged: {string.Join(" | ", evenSegmentWarnings)}");
    }
}
