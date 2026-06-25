using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests.Lang.Packaging;

/// <summary>
/// Unit tests for <see cref="InstructionPlacement"/> and the <c>applyTo</c> metadata field.
/// These guard the contract that package instructions are placed into
/// <c>.github/instructions/{name}.instructions.md</c> with a single applyTo front-matter block,
/// idempotently.
/// </summary>
[TestFixture]
public class InstructionPlacementTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "instr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- BuildContent ----

    [Test]
    public void BuildContent_SingleFileNoFrontmatter_EmitsApplyToAndBody()
    {
        var content = InstructionPlacement.BuildContent(
            "**/*.cs",
            new[] { ("guide.md", "# Title\n\nBody text.\n") });

        Assert.That(content, Is.EqualTo("---\napplyTo: '**/*.cs'\n---\n\n# Title\n\nBody text.\n"));
    }

    [Test]
    public void BuildContent_EmptyApplyTo_DefaultsToStarStar()
    {
        var content = InstructionPlacement.BuildContent(
            "",
            new[] { ("guide.md", "Hello.") });

        Assert.That(content, Is.EqualTo("---\napplyTo: '**'\n---\n\nHello.\n"));
    }

    [Test]
    public void BuildContent_FileWithOwnFrontmatter_IsStrippedAndPackageApplyToWins()
    {
        var content = InstructionPlacement.BuildContent(
            "**/*.cs",
            new[] { ("guide.md", "---\napplyTo: '**/*.py'\ntitle: x\n---\n\nReal body.\n") });

        Assert.That(content, Is.EqualTo("---\napplyTo: '**/*.cs'\n---\n\nReal body.\n"));
    }

    [Test]
    public void BuildContent_MultipleFiles_OrderedByNameAndJoined()
    {
        var content = InstructionPlacement.BuildContent(
            "**/*.cs",
            new[] { ("b.md", "B body"), ("a.md", "A body") });

        Assert.That(content, Is.EqualTo("---\napplyTo: '**/*.cs'\n---\n\nA body\n\nB body\n"));
    }

    [Test]
    public void BuildContent_NormalizesCrlf()
    {
        var content = InstructionPlacement.BuildContent(
            "**/*.cs",
            new[] { ("guide.md", "# Title\r\n\r\nBody.\r\n") });

        Assert.That(content, Is.EqualTo("---\napplyTo: '**/*.cs'\n---\n\n# Title\n\nBody.\n"));
    }

    // ---- StripFrontmatter ----

    [Test]
    public void StripFrontmatter_NoFrontmatter_ReturnsTrimmed()
    {
        Assert.That(InstructionPlacement.StripFrontmatter("  body text\n"), Is.EqualTo("body text"));
    }

    [Test]
    public void StripFrontmatter_RemovesLeadingBlock()
    {
        Assert.That(
            InstructionPlacement.StripFrontmatter("---\nkey: val\n---\n\nThe body\n"),
            Is.EqualTo("The body"));
    }

    [Test]
    public void StripFrontmatter_FrontmatterOnlyNoBody_ReturnsEmpty()
    {
        Assert.That(InstructionPlacement.StripFrontmatter("---\nkey: val\n---"), Is.EqualTo(string.Empty));
    }

    // ---- Place (idempotency) ----

    [Test]
    public void Place_WritesFileWithFrontmatter_AndIsIdempotent()
    {
        var repo = NewTempDir();
        try
        {
            var files = new[] { ("guide.md", "# Guide\n\nUse PascalCase.\n") };

            var path1 = InstructionPlacement.Place(repo, "demo-checks", "**/*.cs", files, out var wrote1);

            Assert.That(path1, Is.Not.Null);
            Assert.That(wrote1, Is.True, "first placement should write");
            Assert.That(path1, Is.EqualTo(Path.Combine(repo, ".github", "instructions", "demo-checks.instructions.md")));
            Assert.That(File.Exists(path1), Is.True);

            var expected = "---\napplyTo: '**/*.cs'\n---\n\n# Guide\n\nUse PascalCase.\n";
            Assert.That(File.ReadAllText(path1!).Replace("\r\n", "\n"), Is.EqualTo(expected));

            var bytesBefore = File.ReadAllBytes(path1!);

            // Second identical placement must be a no-op (no write, identical bytes).
            var path2 = InstructionPlacement.Place(repo, "demo-checks", "**/*.cs", files, out var wrote2);
            Assert.That(path2, Is.EqualTo(path1));
            Assert.That(wrote2, Is.False, "identical placement must not rewrite");
            Assert.That(File.ReadAllBytes(path2!), Is.EqualTo(bytesBefore));
        }
        finally { TryDelete(repo); }
    }

    [Test]
    public void Place_ContentChange_RewritesFile()
    {
        var repo = NewTempDir();
        try
        {
            var files = new[] { ("guide.md", "# Guide\n") };
            InstructionPlacement.Place(repo, "demo", "**/*.cs", files, out _);

            var path = InstructionPlacement.Place(repo, "demo", "**/*.py", files, out var wrote);

            Assert.That(wrote, Is.True, "changed applyTo must rewrite");
            Assert.That(File.ReadAllText(path!).Replace("\r\n", "\n"),
                Does.StartWith("---\napplyTo: '**/*.py'\n---\n"));
        }
        finally { TryDelete(repo); }
    }

    [Test]
    public void Place_NoFiles_ReturnsNull()
    {
        var repo = NewTempDir();
        try
        {
            var path = InstructionPlacement.Place(repo, "demo", "**/*.cs", Array.Empty<(string, string)>(), out var wrote);
            Assert.That(path, Is.Null);
            Assert.That(wrote, Is.False);
        }
        finally { TryDelete(repo); }
    }

    [Test]
    public void PlaceFromPackageDir_ReadsApplyToFromCopJson()
    {
        var repo = NewTempDir();
        try
        {
            var pkgDir = Path.Combine(repo, "pkg", "demo-checks");
            Directory.CreateDirectory(Path.Combine(pkgDir, "instructions"));
            File.WriteAllText(Path.Combine(pkgDir, "cop.json"),
                "{ \"name\": \"demo-checks\", \"version\": \"1.0.0\", \"title\": \"Demo\", \"description\": \"d\", \"authors\": \"a\", \"applyTo\": \"**/*.cs\" }");
            File.WriteAllText(Path.Combine(pkgDir, "instructions", "guide.md"), "# Demo\n\nUse PascalCase.\n");

            var path = InstructionPlacement.PlaceFromPackageDir(repo, pkgDir, out var wrote);

            Assert.That(wrote, Is.True);
            Assert.That(path, Is.EqualTo(Path.Combine(repo, ".github", "instructions", "demo-checks.instructions.md")));
            Assert.That(File.ReadAllText(path!).Replace("\r\n", "\n"),
                Is.EqualTo("---\napplyTo: '**/*.cs'\n---\n\n# Demo\n\nUse PascalCase.\n"));
        }
        finally { TryDelete(repo); }
    }

    [Test]
    public void PlaceFromPackageDir_NoInstructionsFolder_ReturnsNull()
    {
        var repo = NewTempDir();
        try
        {
            var pkgDir = Path.Combine(repo, "pkg", "no-instr");
            Directory.CreateDirectory(Path.Combine(pkgDir, "src"));
            File.WriteAllText(Path.Combine(pkgDir, "cop.json"),
                "{ \"name\": \"no-instr\", \"version\": \"1.0.0\", \"title\": \"N\", \"description\": \"d\", \"authors\": \"a\" }");

            var path = InstructionPlacement.PlaceFromPackageDir(repo, pkgDir, out var wrote);

            Assert.That(path, Is.Null);
            Assert.That(wrote, Is.False);
        }
        finally { TryDelete(repo); }
    }

    // ---- PackageMetadata.applyTo round-trip ----

    [Test]
    public void Metadata_ParsesApplyTo()
    {
        var meta = PackageMetadata.ParseFromJson(
            "{ \"name\": \"x\", \"version\": \"1.0.0\", \"title\": \"X\", \"description\": \"d\", \"authors\": \"a\", \"applyTo\": \"**/*.cs\" }");

        Assert.That(meta.ApplyTo, Is.EqualTo("**/*.cs"));
    }

    [Test]
    public void Metadata_ApplyToDefaultsToEmpty_WhenAbsent()
    {
        var meta = PackageMetadata.ParseFromJson(
            "{ \"name\": \"x\", \"version\": \"1.0.0\", \"title\": \"X\", \"description\": \"d\", \"authors\": \"a\" }");

        Assert.That(meta.ApplyTo, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Metadata_ApplyTo_RoundTripsThroughJson()
    {
        var meta = PackageMetadata.ParseFromJson(
            "{ \"name\": \"x\", \"version\": \"1.0.0\", \"title\": \"X\", \"description\": \"d\", \"authors\": \"a\", \"applyTo\": \"**/*.py\" }");

        var json = meta.ToJson();
        Assert.That(json, Does.Contain("\"applyTo\": \"**/*.py\""));

        var reparsed = PackageMetadata.ParseFromJson(json);
        Assert.That(reparsed.ApplyTo, Is.EqualTo("**/*.py"));
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
