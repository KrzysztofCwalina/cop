using System.Security.Cryptography;
using System.Text;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class DeterminismAndScaleTests
{
    private const int DeterminismRuns = 5;
    private const int ScaleRuns = 3;
    private const int ScaleFileCount = 2000;
    private const int ScaleWritesPerFile = 1;

    private string _tempRoot = null!;
    private string _scriptsDir = null!;
    private string _targetDir = null!;

    private static string RepoRoot => FindRepoRoot();
    private static string PackagesDir => Path.Combine(RepoRoot, "packages");

    [SetUp]
    public void SetUp()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cop-determinism-" + Guid.NewGuid().ToString("N"));
        _scriptsDir = Path.Combine(_tempRoot, "checks");
        _targetDir = Path.Combine(_tempRoot, "target");
        Directory.CreateDirectory(_scriptsDir);
        Directory.CreateDirectory(_targetDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (!string.IsNullOrEmpty(_targetDir))
            ClearSourceCacheForTarget(_targetDir);

        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Test]
    [CancelAfter(90_000)]
    public void SameProgramAndFixture_ProducesIdenticalNonZeroOutputsAcrossRuns()
    {
        const int fileCount = 24;
        const int writesPerFile = 3;
        var expectedCount = fileCount * writesPerFile;
        WriteConsoleWriteLineRule();
        GenerateCSharpTree(fileCount, writesPerFile);
        ClearSourceCacheForTarget(_targetDir);

        var snapshots = Enumerable.Range(0, DeterminismRuns)
            .Select(_ => RunAndCapture())
            .ToArray();

        AssertStableSnapshots(snapshots, $"console-writeline-count={expectedCount}");
    }

    [Test]
    [CancelAfter(60_000)]
    public void CacheInvalidation_SourceEditWithChangedStats_ReparsesUpdatedSource()
    {
        WriteConsoleWriteLineRule();
        var sourcePath = Path.Combine(_targetDir, "Edited.cs");
        ClearSourceCacheForTarget(_targetDir);

        File.WriteAllText(sourcePath, CSharpSource("Edited", writeLineCount: 2));
        var before = RunAndCapture();
        Assert.That(before.Messages, Is.EqualTo(new[] { "console-writeline-count=2" }));

        File.WriteAllText(sourcePath, CSharpSource("Edited", writeLineCount: 3));
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow.AddSeconds(2));

        var after = RunAndCapture();
        Assert.That(after.Messages, Is.EqualTo(new[] { "console-writeline-count=3" }),
            "The second run must observe the edited source, not a stale source cache entry.");
    }

    [Test]
    [CancelAfter(60_000)]
    public void CacheInvalidation_SameLengthSameTimestampEdit_DoesNotServeStaleParse()
    {
        WriteConsoleWriteLineRule();
        var sourcePath = Path.Combine(_targetDir, "SameStats.cs");
        ClearSourceCacheForTarget(_targetDir);

        var original = PadToLength(CSharpSource("SameStats", writeLineCount: 1), length: 700);
        var edited = PadToLength(CSharpSource("SameStats", writeLineCount: 2), length: 700);

        File.WriteAllText(sourcePath, original);
        var originalWriteTime = new DateTime(2026, 06, 23, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sourcePath, originalWriteTime);

        var before = RunAndCapture();
        Assert.That(before.Messages, Is.EqualTo(new[] { "console-writeline-count=1" }));

        File.WriteAllText(sourcePath, edited);
        File.SetLastWriteTimeUtc(sourcePath, originalWriteTime);

        var after = RunAndCapture();
        Assert.That(after.Messages, Is.EqualTo(new[] { "console-writeline-count=2" }),
            "A source edit with unchanged file length and timestamp must not reuse stale parsed source.");
    }

    [Test]
    [CancelAfter(180_000)]
    public void LargeCSharpTree_ProducesExactStableNonZeroCountAcrossRuns()
    {
        var expectedCount = ScaleFileCount * ScaleWritesPerFile;
        WriteConsoleWriteLineRule();

        // 2000 small files is large enough to exercise parallel parser/cache behavior while
        // keeping this regression suite under a couple of minutes on developer machines.
        GenerateCSharpTree(ScaleFileCount, ScaleWritesPerFile);
        ClearSourceCacheForTarget(_targetDir);

        var snapshots = Enumerable.Range(0, ScaleRuns)
            .Select(_ => RunAndCapture())
            .ToArray();

        AssertStableSnapshots(snapshots, $"console-writeline-count={expectedCount}");
    }

    private RunSnapshot RunAndCapture()
    {
        var result = Engine.Run(_scriptsDir, _targetDir, additionalFeedPaths: new[] { PackagesDir });

        Assert.That(result.HasParseErrors, Is.False, string.Join(Environment.NewLine, result.ParseErrors));
        Assert.That(result.HasFatalErrors, Is.False, string.Join(Environment.NewLine, result.Errors));

        var messages = result.Outputs.Select(output => output.Message).ToArray();
        Assert.That(messages, Is.Not.Empty, "A false-green run returned zero outputs.");

        return new RunSnapshot(messages);
    }

    private static void AssertStableSnapshots(IReadOnlyList<RunSnapshot> snapshots, string expectedMessage)
    {
        Assert.That(snapshots, Has.Count.GreaterThan(0));
        Assert.That(snapshots[0].Messages, Is.EqualTo(new[] { expectedMessage }));

        for (var i = 1; i < snapshots.Count; i++)
        {
            Assert.That(snapshots[i].Messages, Is.EqualTo(snapshots[0].Messages),
                $"Run {i + 1} differed from run 1.");
        }
    }

    private void WriteConsoleWriteLineRule()
    {
        var source = $$"""
            import code
            import csharp

            let cb = csharp.parse()

            predicate isConsoleWriteLine(Statement) =>
                Statement:isCall && Statement.TypeName == 'Console' && Statement.MemberName == 'WriteLine'

            let console-writeline-calls = cb.Statements:isConsoleWriteLine

            command MAIN = print('console-writeline-count={console-writeline-calls.Count}')
            """;

        File.WriteAllText(Path.Combine(_scriptsDir, "count-console-writeline.cop"), source);
    }

    private void GenerateCSharpTree(int fileCount, int writesPerFile)
    {
        for (var i = 0; i < fileCount; i++)
        {
            var folder = Path.Combine(_targetDir, "src", (i / 100).ToString("000"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"Generated{i:0000}.cs"),
                CSharpSource($"Generated{i:0000}", writesPerFile));
        }
    }

    private static string CSharpSource(string typeName, int writeLineCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using System;");
        builder.AppendLine($"public sealed class {typeName}");
        builder.AppendLine("{");
        builder.AppendLine("    public void Run()");
        builder.AppendLine("    {");
        builder.AppendLine("        try");
        builder.AppendLine("        {");
        for (var i = 0; i < writeLineCount; i++)
            builder.AppendLine($"            Console.WriteLine(\"{typeName}-{i}\");");
        builder.AppendLine("        }");
        builder.AppendLine("        catch (Exception ex)");
        builder.AppendLine("        {");
        builder.AppendLine("            throw new InvalidOperationException(\"wrapped\", ex);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string PadToLength(string value, int length)
    {
        Assert.That(value.Length, Is.LessThanOrEqualTo(length));
        return value + new string(' ', length - value.Length);
    }

    private static void ClearSourceCacheForTarget(string rootPath)
    {
        var cachePath = GetSourceCachePath(rootPath);
        if (File.Exists(cachePath))
            File.Delete(cachePath);
    }

    private static string GetSourceCachePath(string rootPath)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(rootPath))));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cop",
            "cache",
            $"source-{hash[..16]}.bin");
    }

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    private sealed record RunSnapshot(IReadOnlyList<string> Messages);
}
