using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Regression tests for "false-green" runtime bugs surfaced by the architecture audit: cases where a
/// broken filter silently returned an empty result instead of failing. The maintainer's rule is that
/// a check which silently returns ZERO must FAIL, not pass.
/// </summary>
[TestFixture]
public class FalseGreenRegressionTests
{
    private static (string scripts, string code) NewProgram(string main)
    {
        var temp = Path.Combine(Path.GetTempPath(), "cop-fg-" + Guid.NewGuid().ToString("N")[..8]);
        var scripts = Path.Combine(temp, "scripts");
        var code = Path.Combine(temp, "code");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(Path.Combine(code, "sub")); // a folder so filesystem.Folders is non-empty
        File.WriteAllText(Path.Combine(scripts, "main.cop"), main);
        return (scripts, code);
    }

    [Test]
    public void FilterByNonCallableEnumValue_FailsLoudly_NotSilentEmpty()
    {
        // 'green' is an enum member value, NOT a predicate. Filtering a collection by it used to
        // bypass the typo guard (any env binding counted as "resolvable") and then silently return
        // []. It must now fail loudly.
        var (scripts, code) = NewProgram(
            "enum Color = red | green\n" +
            "let v = filesystem.Folders:green\n" +
            "command MAIN = v\n");
        try
        {
            var result = Engine.Run(scripts, code);

            Assert.That(result.HasFatalErrors, Is.True,
                "filtering by a non-callable binding (an enum value) must error, not silently return empty");
            Assert.That(string.Join(" | ", result.Errors), Does.Contain("green"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(scripts)!, recursive: true); }
    }

    [Test]
    public void FilterByRealPredicate_StillWorks()
    {
        // The guard must not over-fire: a genuinely-defined predicate still filters normally.
        var (scripts, code) = NewProgram(
            "predicate isTarget(Folder) => true\n" +
            "let v = filesystem.Folders:isTarget\n" +
            "command MAIN = foreach v => 'folder:{item.Path}'\n");
        try
        {
            var result = Engine.Run(scripts, code);

            Assert.That(result.HasFatalErrors, Is.False, string.Join(" | ", result.Errors));
            Assert.That(result.Outputs, Is.Not.Empty, "a real predicate must still filter and produce output");
        }
        finally { Directory.Delete(Path.GetDirectoryName(scripts)!, recursive: true); }
    }
}
