using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// The KEYSTONE doc harness: complements <see cref="DocSnippetVerifyTests"/> by actually
/// EXECUTING documented ```cop programs, not just verifying them.
///
/// Why this exists: nearly every shipped bug had the shape "<c>cop verify</c> passes but the
/// program crashes / returns null / produces nothing at runtime". <c>cop verify</c> only
/// checks syntax/types — it never runs the program — so a verify-only harness is blind to the
/// entire bug class. This harness runs each complete documented program against a small,
/// stable multi-language fixture and asserts:
///   1. It never FATALS (exit code 2). Exit 0 (clean) and 1 (violations found) are both fine.
///   2. If the snippet carries expected-output annotations, stdout contains each of them.
///
/// Snippet selection: a fenced ```cop block is executed when it is a complete program
/// (contains a top-level `command ...`) or carries at least one `# =&gt;` expectation.
///
/// Opt-out / annotations (in the fence):
///   ```cop skip      — ignored by BOTH harnesses (illustrative/partial snippet).
///   ```cop norun     — verified but NOT executed here (needs network/specific files, or
///                      tracks a known runtime bug — cite the issue in a trailing comment).
///   # =&gt; SUBSTRING  — assert the program's stdout contains SUBSTRING (repeatable).
/// </summary>
[TestFixture]
public class DocSnippetRunTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    private static string CopExe => Path.Combine(RepoRoot, "install", "win-x64", "cop.exe");

    /// <summary>Small, stable analysis target so source/file providers have real data.</summary>
    private static string FixtureTarget =>
        Path.Combine(RepoRoot, "tests", "behavior", "_snippet-fixture");

    private static readonly string[] ExcludedDocs = ["language-design.md", "cop-grammar.md"];

    private sealed record Snippet(string Doc, int StartLine, string Content, string[] Expectations);

    private static IEnumerable<string> DocFiles()
    {
        var docsDir = Path.Combine(RepoRoot, "docs");
        foreach (var f in Directory.GetFiles(docsDir, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(RepoRoot, f).Replace('\\', '/');
            if (rel.Contains("/internal/")) continue;
            if (ExcludedDocs.Contains(Path.GetFileName(f))) continue;
            yield return f;
        }
        var readme = Path.Combine(RepoRoot, "README.md");
        if (File.Exists(readme)) yield return readme;
    }

    private static readonly Regex CommandDecl =
        new(@"^\s*command\s+\w", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExpectLine =
        new(@"^\s*#\s*=>\s*(.+?)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Extract fenced ```cop blocks that are runnable complete programs. Skips blocks tagged
    /// "skip" or "norun". Captures `# =&gt;` expectation lines.
    /// </summary>
    private static IEnumerable<Snippet> ExtractRunnableSnippets(string file)
    {
        var lines = File.ReadAllLines(file);
        var docName = Path.GetFileName(file);
        bool inFence = false, isCop = false, skip = false;
        int start = 0;
        var buf = new List<string>();

        foreach (var (raw, idx) in lines.Select((l, i) => (l, i)))
        {
            var trimmed = raw.TrimStart();
            if (!inFence)
            {
                if (trimmed.StartsWith("```"))
                {
                    var info = trimmed[3..].Trim();
                    var parts = info.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    inFence = true;
                    isCop = parts.Length > 0 && parts[0] == "cop";
                    skip = parts.Skip(1).Any(p =>
                        p.Equals("skip", StringComparison.OrdinalIgnoreCase) ||
                        p.Equals("norun", StringComparison.OrdinalIgnoreCase));
                    start = idx + 1;
                    buf.Clear();
                }
            }
            else
            {
                if (trimmed.StartsWith("```"))
                {
                    if (isCop && !skip)
                    {
                        var content = string.Join('\n', buf);
                        var expectations = buf
                            .Select(l => ExpectLine.Match(l))
                            .Where(m => m.Success)
                            .Select(m => m.Groups[1].Value)
                            .ToArray();
                        bool isProgram = buf.Any(l => CommandDecl.IsMatch(l));
                        if (isProgram || expectations.Length > 0)
                            yield return new Snippet(docName, start, content, expectations);
                    }
                    inFence = false;
                }
                else
                {
                    buf.Add(raw);
                }
            }
        }
    }

    [Test]
    public void AllRunnableDocSnippets_Execute_WithoutFatalError()
    {
        if (!File.Exists(CopExe))
            Assert.Ignore($"Published cop.exe not found at {CopExe}; run install/publish.ps1 first.");
        Assert.That(Directory.Exists(FixtureTarget), Is.True,
            $"Snippet fixture target not found: {FixtureTarget}");

        var tmpDir = Path.Combine(RepoRoot, "tests", "Cop.Tests", "obj", "docsnippets-run");
        Directory.CreateDirectory(tmpDir);

        var failures = new List<string>();
        int executed = 0;
        try
        {
            foreach (var file in DocFiles())
            foreach (var snippet in ExtractRunnableSnippets(file))
            {
                executed++;
                var copFile = Path.Combine(tmpDir, "snippet.cop");
                WriteSnippet(copFile, snippet.Content);

                var (exit, stdout, stderr) = Run(copFile);

                if (exit == 2)
                {
                    failures.Add($"{snippet.Doc}:{snippet.StartLine} FATAL (exit 2):\n    " +
                        (stdout + stderr).Trim().Replace("\n", "\n    "));
                    continue;
                }
                foreach (var expected in snippet.Expectations)
                {
                    if (!stdout.Contains(expected, StringComparison.Ordinal))
                        failures.Add($"{snippet.Doc}:{snippet.StartLine} expected stdout to contain " +
                            $"'{expected}' but it did not.\n    STDOUT: {stdout.Trim().Replace("\n", "\n    ")}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }

        Assert.That(executed, Is.GreaterThan(0), "Expected to find runnable ```cop programs in docs");
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} doc ```cop program(s) failed to execute cleanly " +
                "(tag intentionally non-runnable snippets ```cop norun and cite the issue):\n\n" +
                string.Join("\n\n", failures));
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(string copFile)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CopExe,
            Arguments = $"\"{copFile}\" -t \"{FixtureTarget}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
            try { p.Kill(entireProcessTree: true); } catch { }
        return (p.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Writes the (reused) snippet file, retrying briefly on transient IOExceptions — a
    /// previously spawned cop.exe can momentarily hold the file handle, which otherwise makes
    /// this harness flake with "the process cannot access the file ... snippet.cop".
    /// </summary>
    private static void WriteSnippet(string path, string content)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.WriteAllText(path, content); return; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
        }
    }
}
