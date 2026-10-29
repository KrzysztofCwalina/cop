using System.Net;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Cop.Cli.Commands;
using Cop.Core;
using Cop.Lang;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class ReleasePackageCompatibilityTests
{
    [Test]
    public void Issue093_AllShippedClrProvidersMatchTheHostRelease()
    {
        var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));
        var packages = Directory.EnumerateFiles(Path.Combine(root, "packages"), "cop.json", SearchOption.AllDirectories)
            .Select(path => Path.GetDirectoryName(path)!)
            .Select(path => (Path: path, Metadata: PackageMetadata.TryLoadFromDirectory(path)!))
            .Where(package => package.Metadata.IsClrProvider)
            .ToArray();

        Assert.That(packages.Select(package => package.Metadata.Name),
            Does.Contain("json").And.Contain("analysis-codeql").And.Contain("http").And.Contain("typespec-http"));
        Assert.Multiple(() =>
        {
            foreach (var package in packages)
            {
                Assert.That(ProviderCompatibility.FindAssembly(package.Path, package.Metadata), Is.Not.Null,
                    $"Missing provider DLL for {package.Metadata.Name}");
                Assert.That(ProviderCompatibility.GetError(package.Path, package.Metadata), Is.Null);
            }
        });
    }

    [TestCase("github.com/KrzysztofCwalina/cop/code", true)]
    [TestCase("github.com/another/feed/code", false)]
    [TestCase("github.com/KrzysztofCwalina/cop/code:custom-ref", false)]
    public async Task Issue093_DefaultFeedDownloadsArePinnedToHostRelease(string reference, bool defaultRelease)
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new PackageHandler(requests));
        var source = new GitHubPackageSource(http);
        var package = PackageReference.Parse(reference);
        await source.GetPackageMetadataAsync(package);
        var files = await source.DownloadPackageFilesAsync(package);

        Assert.That(files.Keys, Is.EqualTo(new[] { "cop.json" }));
        var expectedRef = defaultRelease ? $"v{typeof(DataProvider).Assembly.GetName().Version}" : package.Version;
        Assert.That(requests.Select(r => r.Query),
            Is.All.EqualTo(expectedRef is null ? "" : $"?ref={expectedRef}"));
    }

    [Test]
    public async Task Issue093_ExplicitRestoreSelectsTheSameHostRelease()
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new PackageHandler(requests));
        var resolver = new DependencyResolver(new GitHubPackageSource(http));
        var resolved = await resolver.ResolveAsync([PackageReference.Parse("github.com/KrzysztofCwalina/cop/code")]);
        var releaseRef = $"v{typeof(DataProvider).Assembly.GetName().Version}";
        Assert.That(resolved.Select(p => p.Version), Is.EqualTo(new[] { releaseRef }));
        Assert.That(requests.Select(r => r.Query), Is.All.EqualTo($"?ref={releaseRef}"));
    }

    [Test]
    public void Issue093_CacheIsScopedToHostVersion()
    {
        Assert.That(PackageResolver.GlobalCachePath, Is.EqualTo(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cop", "packages",
            typeof(DataProvider).Assembly.GetName().Version!.ToString())));
    }

    [Test]
    public void Issue093_IncompatibleLocalProviderIsSkippedWithoutDeletingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-release-packages-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stale = WriteProvider(root, "stale", new Version(2000, 1, 1, 1));
            var matching = WriteProvider(root, "matching", typeof(DataProvider).Assembly.GetName().Version!);
            Assert.That(ImportResolver.FindPackageDir(Path.GetDirectoryName(stale)!, "fixture"), Is.Null);
            Assert.That(ImportResolver.FindPackageDir(Path.GetDirectoryName(matching)!, "fixture"),
                Is.EqualTo(matching));
            Assert.That(File.Exists(Path.Combine(stale, "lib", "fixture.dll")), Is.True);

            var errors = new List<string>();
            Assert.That(ProviderLoader.Load(stale, PackageMetadata.TryLoadFromDirectory(stale)!, errors), Is.Null);
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0], Does.Contain("2000.1.1.1").And.Contain("fixture.dll").And.Contain("incompatible"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public void Issue093_EngineUsesMatchingFallbackInsteadOfIncompatibleLocalProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-provider-fallback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var stale = WriteProvider(root, "packages", new Version(2000, 1, 1, 1));
            Directory.CreateDirectory(Path.Combine(stale, "src"));
            File.WriteAllText(Path.Combine(stale, "src", "main.cop"), "export let selected = 'STALE'");
            var matching = Path.Combine(root, "fallback", "fixture");
            Directory.CreateDirectory(Path.Combine(matching, "src"));
            File.WriteAllText(Path.Combine(matching, "cop.json"),
                """{"name":"fixture","version":"1.0.0","title":"Fixture","description":"Fixture","authors":"tests"}""");
            File.WriteAllText(Path.Combine(matching, "src", "main.cop"), "export let selected = 'MATCHING'");
            var main = Path.Combine(root, "main.cop");
            File.WriteAllText(main, "import fixture\ncommand MAIN = print(selected)");

            var result = Engine.Run(root, root, additionalFeedPaths: [Path.Combine(root, "fallback")],
                scriptFiles: [main]);
            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "MATCHING" }));
            Assert.That(File.Exists(Path.Combine(stale, "lib", "fixture.dll")), Is.True);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public void Issue093_InitializationFailureDoesNotRunCommandOrReportUndefinedBindings()
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-initialization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "main.cop"), """
                let number = 123
                let print = print(number())
                let all-violations = []
                command MAIN = print(all-violations)
                """);
            var result = Engine.Run(root, root);
            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.True, string.Join("\n", result.Errors));
            Assert.That(result.Errors, Has.Count.EqualTo(1));
            Assert.That(result.Errors[0], Does.Not.Contain("Undefined variable"));
            Assert.That(result.Outputs, Is.Empty);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string WriteProvider(string root, string feed, Version hostVersion)
    {
        var package = Path.Combine(root, feed, "fixture");
        Directory.CreateDirectory(Path.Combine(package, "lib"));
        File.WriteAllText(Path.Combine(package, "cop.json"), """
            {"name":"fixture","version":"1.0.0","title":"Fixture","description":"Fixture","authors":"tests",
             "provider":"clr","providerEntry":"Fixture","providerAssembly":"fixture.dll"}
            """);
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("fixture.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("fixture"), new Version(1, 0), default, default, 0, AssemblyHashAlgorithm.None);
        metadata.AddAssemblyReference(metadata.GetOrAddString("cop"), hostVersion, default, default, 0, default);
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(), new MetadataRootBuilder(metadata), new BlobBuilder());
        var blob = new BlobBuilder();
        pe.Serialize(blob);
        File.WriteAllBytes(Path.Combine(package, "lib", "fixture.dll"), blob.ToArray());
        return package;
    }

    private sealed class PackageHandler(List<Uri> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request.RequestUri!);
            const string manifest = """{"name":"code","version":"1.0.0","title":"Fixture","description":"Fixture","authors":"tests"}""";
            var content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(manifest));
            var json = request.RequestUri!.AbsolutePath.EndsWith("/cop.json", StringComparison.Ordinal)
                ? $$"""{"content":"{{content}}","encoding":"base64"}"""
                : $$"""[{"name":"cop.json","type":"file","content":"{{content}}","encoding":"base64"}]""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
