using System.Diagnostics;
using System.Threading;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Auto-extraction harness: pulls every fenced ```cop snippet out of the
/// user-facing docs and runs `cop verify` on it, so documented code can't
/// silently rot into syntax/binding/type errors.
///
/// Scope: docs/**/*.md (excluding docs/internal/) + README.md. The design-rationale
/// docs (language-design.md, cop-grammar.md) are excluded — they contain conceptual
/// fragments, not runnable programs.
///
/// Opt-out: a snippet that is intentionally illustrative/partial (placeholder package
/// names, syntax templates with &lt;...&gt;) is tagged in its fence info string, e.g.
/// ```cop skip — the word "skip" after the language tells this harness to ignore it.
///
/// Note: snippets are written under the repo tree so the repo's local packages/ feed
/// resolves imports (cop verify does not auto-restore from the network).
/// </summary>
[TestFixture]
public class DocSnippetVerifyTests
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

    private static readonly string[] ExcludedDocs = ["language-design.md", "cop-grammar.md"];

    private sealed record Snippet(string Doc, int StartLine, string Content);

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

    /// <summary>Extract fenced ```cop blocks, skipping any whose info string contains "skip".</summary>
    private static IEnumerable<Snippet> ExtractCopSnippets(string file)
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
                    skip = parts.Skip(1).Any(p => p.Equals("skip", StringComparison.OrdinalIgnoreCase));
                    start = idx + 1;
                    buf.Clear();
                }
            }
            else
            {
                if (trimmed.StartsWith("```"))
                {
                    if (isCop && !skip)
                        yield return new Snippet(docName, start, string.Join('\n', buf));
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
    public void AllCopDocSnippets_PassVerify()
    {
        if (!File.Exists(CopExe))
            Assert.Ignore($"Published cop.exe not found at {CopExe}; run install/publish.ps1 first.");

        // Write snippets under the repo tree so the local packages/ feed resolves imports.
        var tmpDir = Path.Combine(RepoRoot, "tests", "Cop.Tests", "obj", "docsnippets");
        Directory.CreateDirectory(tmpDir);

        var failures = new List<string>();
        int verified = 0;
        try
        {
            foreach (var file in DocFiles())
            foreach (var snippet in ExtractCopSnippets(file))
            {
                verified++;
                var copFile = Path.Combine(tmpDir, "snippet.cop");
                WriteSnippet(copFile, snippet.Content);

                var psi = new ProcessStartInfo
                {
                    FileName = CopExe,
                    Arguments = $"verify \"{copFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = RepoRoot,
                };
                using var process = Process.Start(psi)!;
                var outTask = process.StandardOutput.ReadToEndAsync();
                var errTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30_000))
                    try { process.Kill(entireProcessTree: true); } catch { }
                var stdout = outTask.GetAwaiter().GetResult();
                var stderr = errTask.GetAwaiter().GetResult();

                if (process.ExitCode != 0)
                    failures.Add($"{snippet.Doc}:{snippet.StartLine}\n    {(stdout + stderr).Trim().Replace("\n", "\n    ")}");
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }

        Assert.That(verified, Is.GreaterThan(0), "Expected to find ```cop snippets in docs");
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} doc ```cop snippet(s) failed `cop verify` " +
                $"(mark intentionally-partial snippets with ```cop skip):\n\n" +
                string.Join("\n\n", failures));
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
