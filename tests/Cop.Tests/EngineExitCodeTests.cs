using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// The exit code for an AUTO-ROUTED [Violation] result — a command that returns a raw violation
/// list, not wrapped in CHECK — must come from CHECK's count STRUCTURALLY, not from string-sniffing
/// the formatted output for "): error:". Guards the engine exit-code cleanup (audit P3).
/// </summary>
[TestFixture]
public class EngineExitCodeTests
{
    private static string RepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    private static string PackagesDir => Path.Combine(RepoRoot(), "packages");

    private static string NewTemp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cop-exit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public void RawViolationList_RoutedThroughCheck_SetsStructuralExitCode()
    {
        var temp = NewTemp();
        var scriptsDir = Path.Combine(temp, "scripts");
        var codebaseDir = Path.Combine(temp, "code");
        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(Path.Combine(codebaseDir, "sub")); // a folder so filesystem.Folders is non-empty
        try
        {
            // MAIN returns a RAW [Violation] list (not CHECK(...)), so the engine must auto-route it.
            File.WriteAllText(Path.Combine(scriptsDir, "main.cop"),
                "import code\n" +
                "predicate isTarget(Folder) => true\n" +
                "let violations = filesystem.Folders:isTarget:toError('V {item.Path}')\n" +
                "command MAIN = violations\n");

            var result = Engine.Run(scriptsDir, codebaseDir, additionalFeedPaths: [PackagesDir]);

            Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
            // P3: the routed result yields a STRUCTURAL exit code (CHECK's count), not a null that
            // forces the CLI to string-sniff the formatted output.
            Assert.That(result.ExitCode, Is.Not.Null, "auto-routed violations must set a structural exit code");
            Assert.That(result.ExitCode!.Value, Is.GreaterThan(0));
            // Violations are still formatted/printed.
            Assert.That(result.Outputs.Any(o => o.Message.Contains("error:")), Is.True,
                "violations should still be formatted: " + string.Join(" | ", result.Outputs.Select(o => o.Message)));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Test]
    public void NoMatchingViolations_RawList_ExitsClean()
    {
        var temp = NewTemp();
        var scriptsDir = Path.Combine(temp, "scripts");
        var codebaseDir = Path.Combine(temp, "code");
        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(Path.Combine(codebaseDir, "sub")); // non-empty provider (no empty-provider warning)
        try
        {
            File.WriteAllText(Path.Combine(scriptsDir, "main.cop"),
                "import code\n" +
                "predicate isTarget(Folder) => false\n" +
                "let violations = filesystem.Folders:isTarget:toError('V {item.Path}')\n" +
                "command MAIN = violations\n");

            var result = Engine.Run(scriptsDir, codebaseDir, additionalFeedPaths: [PackagesDir]);

            Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
            // An empty violation list is not routed → clean exit.
            Assert.That(result.ExitCode is null or 0, Is.True, $"clean run must not fail (ExitCode={result.ExitCode})");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
