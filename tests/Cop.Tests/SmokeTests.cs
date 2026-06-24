using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class SmokeTests
{
    /// <summary>
    /// Minimal end-to-end smoke test: the engine parses and runs a trivial program and emits its
    /// output. Asserts the exact value so a silently-empty run fails (replaces a former
    /// Assert.Pass() placeholder that verified nothing).
    /// </summary>
    [Test]
    public void Engine_RunsTrivialProgram_ProducesExpectedOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-smoke-" + Guid.NewGuid().ToString("N")[..8]);
        var scripts = Path.Combine(root, "scripts");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(scripts, "program.cop"), "command main = print('ok')");

            var result = Engine.Run(scripts, target);

            Assert.That(result.HasParseErrors, Is.False, string.Join("; ", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "ok" }));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
