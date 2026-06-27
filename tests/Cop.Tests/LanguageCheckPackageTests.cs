using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Tests for language-specific check packages (csharp-checks, python-checks, ...).
///
/// Language-specific packages must hardcode their language provider via
/// codebase(&lt;lang&gt;.parse()) so that `cop &lt;lang&gt;-checks -t &lt;dir&gt;` works with NO
/// -p flag. Using codebase(Program.Providers) makes them silently produce nothing
/// unless the caller passes `-p &lt;lang&gt;`.
/// </summary>
[TestFixture]
public class LanguageCheckPackageTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    /// <summary>
    /// End-to-end: running the csharp-checks package against C# source must produce
    /// violations WITHOUT a -p provider flag (the package hardcodes csharp.parse()).
    /// This is the exact scenario `cop csharp-checks -t &lt;dir&gt;` exercises.
    /// </summary>
    [Test]
    public void CSharpChecks_RunsWithoutProviderFlag_ProducesViolations()
    {
        var feedPaths = new List<string> { Path.Combine(RepoRoot, "packages") };

        var fixtureDir = Path.Combine(Path.GetTempPath(), "cop-langcheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureDir);
        try
        {
            // `var` triggers csharp-checks' var-declarations rule.
            File.WriteAllText(Path.Combine(fixtureDir, "Sample.cs"),
                "class Sample { void M() { var x = 1; } }");

            // No `providers:` argument — mirrors `cop csharp-checks -t <dir>` with no -p.
            var result = Engine.RunProject(feedPaths, ["csharp-checks"], fixtureDir, []);

            Assert.That(result.HasFatalErrors, Is.False,
                "csharp-checks should run without -p. Errors: " + string.Join("; ", result.Errors));
            Assert.That(result.Outputs, Is.Not.Empty,
                "csharp-checks produced no output without -p — the package likely uses " +
                "codebase(Program.Providers) instead of codebase(csharp.parse()).");
            Assert.That(result.Outputs.Any(o => o.Message.Contains("var", StringComparison.OrdinalIgnoreCase)),
                Is.True, "Expected a 'var' violation from the analyzed C# source.");
        }
        finally
        {
            Directory.Delete(fixtureDir, recursive: true);
        }
    }

    /// <summary>
    /// End-to-end: running the java-checks package against Java source must produce
    /// violations WITHOUT a -p provider flag (the package hardcodes java.parse()).
    /// This is the exact scenario `cop java-checks -t &lt;dir&gt;` exercises.
    /// </summary>
    [Test]
    public void JavaChecks_RunsWithoutProviderFlag_ProducesViolations()
    {
        var feedPaths = new List<string> { Path.Combine(RepoRoot, "packages") };

        var fixtureDir = Path.Combine(Path.GetTempPath(), "cop-langcheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureDir);
        try
        {
            // System.exit(...) triggers java-checks' system-exit rule.
            File.WriteAllText(Path.Combine(fixtureDir, "Sample.java"),
                "public class Sample { void m() { System.out.println(\"x\"); System.exit(1); } }");

            // No `providers:` argument — mirrors `cop java-checks -t <dir>` with no -p.
            var result = Engine.RunProject(feedPaths, ["java-checks"], fixtureDir, []);

            Assert.That(result.HasFatalErrors, Is.False,
                "java-checks should run without -p. Errors: " + string.Join("; ", result.Errors));
            Assert.That(result.Outputs, Is.Not.Empty,
                "java-checks produced no output without -p — the package likely uses " +
                "codebase(Program.Providers) instead of codebase(java.parse()).");
            Assert.That(result.Outputs.Any(o => o.Message.Contains("System.exit", StringComparison.OrdinalIgnoreCase)),
                Is.True, "Expected a System.exit() violation from the analyzed Java source.");
        }
        finally
        {
            Directory.Delete(fixtureDir, recursive: true);
        }
    }
    /// <summary>
    /// End-to-end: running the rust-checks package against Rust source must produce
    /// violations WITHOUT a -p provider flag (the package hardcodes rust.parse()).
    /// This is the exact scenario `cop rust-checks -t &lt;dir&gt;` exercises.
    /// </summary>
    [Test]
    public void RustChecks_RunsWithoutProviderFlag_ProducesViolations()
    {
        var feedPaths = new List<string> { Path.Combine(RepoRoot, "packages") };

        var fixtureDir = Path.Combine(Path.GetTempPath(), "cop-langcheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureDir);
        try
        {
            // .unwrap() triggers rust-checks' unwrap-calls rule.
            File.WriteAllText(Path.Combine(fixtureDir, "sample.rs"),
                "pub fn run() { let x = parse().unwrap(); }");

            // No `providers:` argument — mirrors `cop rust-checks -t <dir>` with no -p.
            var result = Engine.RunProject(feedPaths, ["rust-checks"], fixtureDir, []);

            Assert.That(result.HasFatalErrors, Is.False,
                "rust-checks should run without -p. Errors: " + string.Join("; ", result.Errors));
            Assert.That(result.Outputs, Is.Not.Empty,
                "rust-checks produced no output without -p — the package likely uses " +
                "codebase(Program.Providers) instead of codebase(rust.parse()).");
            Assert.That(result.Outputs.Any(o => o.Message.Contains("unwrap", StringComparison.OrdinalIgnoreCase)),
                Is.True, "Expected an .unwrap() violation from the analyzed Rust source.");
        }
        finally
        {
            Directory.Delete(fixtureDir, recursive: true);
        }
    }

    /// <summary>
    /// End-to-end: running the go-checks package against Go source must produce
    /// violations WITHOUT a -p provider flag (the package hardcodes go.parse()).
    /// This is the exact scenario `cop go-checks -t &lt;dir&gt;` exercises.
    /// </summary>
    [Test]
    public void GoChecks_RunsWithoutProviderFlag_ProducesViolations()
    {
        var feedPaths = new List<string> { Path.Combine(RepoRoot, "packages") };

        var fixtureDir = Path.Combine(Path.GetTempPath(), "cop-langcheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureDir);
        try
        {
            // panic() triggers go-checks' panic-calls rule.
            File.WriteAllText(Path.Combine(fixtureDir, "lib.go"),
                "package lib\nfunc Run() { panic(\"boom\") }\n");

            // No `providers:` argument — mirrors `cop go-checks -t <dir>` with no -p.
            var result = Engine.RunProject(feedPaths, ["go-checks"], fixtureDir, []);

            Assert.That(result.HasFatalErrors, Is.False,
                "go-checks should run without -p. Errors: " + string.Join("; ", result.Errors));
            Assert.That(result.Outputs, Is.Not.Empty,
                "go-checks produced no output without -p — the package likely uses " +
                "codebase(Program.Providers) instead of codebase(go.parse()).");
            Assert.That(result.Outputs.Any(o => o.Message.Contains("panic", StringComparison.OrdinalIgnoreCase)),
                Is.True, "Expected a panic() violation from the analyzed Go source.");
        }
        finally
        {
            Directory.Delete(fixtureDir, recursive: true);
        }
    }

    /// <summary>
    /// Every language-specific check package (now under checks/ and languages/) must
    /// hardcode its provider via codebase(&lt;lang&gt;.parse()) so it works without a -p
    /// flag — it must not build from Program.Providers. code-metrics is exempt: it is a
    /// language-agnostic report that intentionally uses the ambient codebase.
    /// </summary>
    [Test]
    public void LanguageCheckPackages_DoNotUseProgramProviders()
    {
        var packagesDir = Path.Combine(RepoRoot, "packages");
        string[] checkDirs = ["checks", "languages"];

        var offenders = new List<string>();
        foreach (var lang in checkDirs)
        {
            var langDir = Path.Combine(packagesDir, lang);
            if (!Directory.Exists(langDir)) continue;

            foreach (var copFile in Directory.GetFiles(langDir, "*.cop", SearchOption.AllDirectories))
            {
                var rel = copFile.Replace('\\', '/');
                if (rel.Contains("/samples/")) continue;
                if (rel.Contains("/code-metrics/")) continue; // language-agnostic report; intentionally ambient
                if (File.ReadAllText(copFile).Contains("codebase(Program.Providers)"))
                    offenders.Add(Path.GetRelativePath(RepoRoot, copFile).Replace('\\', '/'));
            }
        }

        Assert.That(offenders, Is.Empty,
            "Language-specific packages must hardcode their provider via codebase(<lang>.parse()), " +
            "not codebase(Program.Providers):\n  " + string.Join("\n  ", offenders));
    }
}
