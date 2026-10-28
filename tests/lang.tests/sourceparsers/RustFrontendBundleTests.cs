using System.IO.Compression;
using System.Text;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class RustFrontendBundleTests
{
    private string _root = "";
    private static string Helper => OperatingSystem.IsWindows() ? "cop-rust-frontend.exe" : "cop-rust-frontend";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-rust-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private static byte[] Bundle(string? extra = null, bool symlink = false)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in new[] { Helper, "rust-stdlib/manifest.json",
                "rust-stdlib/library/core/src/lib.rs", "rust-frontend-licenses/manifest.json" })
            {
                using var output = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                output.Write(name == Helper ? "immutable helper fixture" : "{}");
            }
            if (extra is not null)
            {
                var entry = zip.CreateEntry(extra);
                if (symlink) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var output = new StreamWriter(entry.Open());
                output.Write("not a permitted target");
            }
        }
        return buffer.ToArray();
    }

    private RustSourceParser Parser(byte[] bytes) =>
        new(bundleFactory: () => new MemoryStream(bytes, writable: false), cacheDirectory: _root);

    [Test]
    public void EquivalentEmbeddedArchiveIsLazyAndConcurrentPublicationReusesOneVerifiedDirectory()
    {
        byte[] bytes = Bundle();
        int opened = 0;
        var lazy = new RustSourceParser(bundleFactory: () => { opened++; return new MemoryStream(bytes); }, cacheDirectory: _root);
        Assert.That(lazy.Extensions, Is.EqualTo(new[] { ".rs" }));
        Assert.That(opened, Is.Zero, "provider construction must not extract a frontend");
        var identities = Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => Parser(bytes).GetCacheIdentity("lib.rs")))).GetAwaiter().GetResult();
        Assert.That(identities.Distinct().Count(), Is.EqualTo(1));
        Assert.That(lazy.GetCacheIdentity("lib.rs"), Is.EqualTo(identities[0]));
        Assert.That(lazy.CanCache("lib.rs"), Is.False);
        Assert.That(opened, Is.EqualTo(1));
        var directories = Directory.GetDirectories(_root);
        Assert.That(directories, Has.Length.EqualTo(1), "atomic extraction leaves no staging directories");
        string helper = Path.Combine(directories[0], Helper);
        Assert.That(File.ReadAllText(helper), Is.EqualTo("immutable helper fixture"));
        if (!OperatingSystem.IsWindows())
            Assert.That(File.GetUnixFileMode(helper).HasFlag(UnixFileMode.UserExecute), Is.True);
    }

    [TestCase("../escaped")]
    [TestCase("/absolute")]
    [TestCase("C:/absolute")]
    [TestCase("rust-stdlib/../../escaped")]
    [TestCase("rust-stdlib\\..\\escaped")]
    [TestCase("rust-stdlib/.. /escaped")]
    [TestCase("rust-stdlib//ambiguous")]
    public void TraversalAndAmbiguousNamesAreErrorsNotFallback(string entry)
    {
        var parsed = Parser(Bundle(entry)).Parse("fixture.rs", "fn run(){}")!;
        Assert.That(parsed.ParseErrors, Has.Count.EqualTo(1));
        Assert.That(parsed.ParseErrors.Single(), Does.Contain("Unsafe Rust frontend bundle entry"));
        Assert.That(parsed.Methods, Is.Empty);
        Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty);
    }

    [Test]
    public void SymlinksCorruptionAndCacheTamperingAreNotSilentlyAccepted()
    {
        Assert.Throws<InvalidDataException>(() => Parser(Bundle("link", symlink: true)).GetCacheIdentity("lib.rs"));
        Assert.Throws<InvalidDataException>(() => Parser([1, 2, 3]).GetCacheIdentity("lib.rs"));
        byte[] bytes = Bundle();
        Parser(bytes).GetCacheIdentity("lib.rs");
        string helper = Path.Combine(Directory.GetDirectories(_root).Single(), Helper);
        File.WriteAllText(helper, "corrupt");
        Assert.Throws<InvalidDataException>(() => Parser(bytes).GetCacheIdentity("lib.rs"));
    }

    [Test]
    public void OversizedExpandedEntryIsRejectedBeforeExtraction()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("large").Open();
            byte[] zeroes = new byte[8192];
            for (int i = 0; i < (64 * 1024 * 1024 / zeroes.Length) + 1; i++) entry.Write(zeroes);
        }
        Assert.Throws<InvalidDataException>(() => Parser(buffer.ToArray()).GetCacheIdentity("lib.rs"));
        Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty);
    }

    [Test]
    public void MissingArchiveHasGenuineStructuralFallbackRatherThanBackendFailure()
    {
        var parser = new RustSourceParser(bundleFactory: () => null, cacheDirectory: _root);
        var file = parser.Parse("fixture.rs", "pub struct Alpha; impl Alpha { pub fn run(&self) {} }")!;
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Types.Where(t => t.Kind == TypeKind.Struct).SelectMany(t => t.Methods).Select(m => m.Name),
            Is.EqualTo(new[] { "run" }));
        Assert.That(parser.CanCache("fixture.rs"), Is.True);
        Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty);
    }
}
