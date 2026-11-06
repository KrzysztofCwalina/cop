using System.Net;
using System.Text;
using System.Text.Json;
using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public sealed class PackageDependencyRegressionTests
{
    [Test]
    public async Task Issue108_TransitiveDependenciesResolveExactVersions()
    {
        using var handler = new ManifestHandler(new Dictionary<string, string[]>
        {
            ["checks/1.0.2"] = ["code/1.2.0", "language/2.0.0"],
            ["code/1.2.0"] = [],
            ["language/2.0.0"] = ["code/1.2.0"]
        });
        using var http = new HttpClient(handler);
        var packages = await new DependencyResolver(new GitHubPackageSource(http))
            .ResolveAsync([Reference("checks/1.0.2")]);

        Assert.That(packages.Select(package => $"{package.PackageName}/{package.Version}"),
            Is.EqualTo(new[] { "checks/1.0.2", "code/1.2.0", "language/2.0.0" }));
        Assert.That(handler.Refs,
            Is.EquivalentTo(new[]
            {
                "checks/1.0.2", "checks/1.0.2", "code/1.2.0", "code/1.2.0",
                "language/2.0.0", "language/2.0.0"
            }));
    }

    [Test]
    public async Task Issue108_DiamondDependenciesKeepHighestVersionAndItsDependencies()
    {
        using var http = new HttpClient(new ManifestHandler(new Dictionary<string, string[]>
        {
            ["root/1.0.0"] = ["left/1.0.0", "right/1.0.0"],
            ["left/1.0.0"] = ["shared/1.0.0"],
            ["right/1.0.0"] = ["shared/2.0.0"],
            ["shared/1.0.0"] = [],
            ["shared/2.0.0"] = ["leaf/1.0.0"],
            ["leaf/1.0.0"] = []
        }));
        var packages = await new DependencyResolver(new GitHubPackageSource(http))
            .ResolveAsync([Reference("root/1.0.0"), Reference("shared/1.0.0")]);

        Assert.That(packages.Select(package => $"{package.PackageName}/{package.Version}"),
            Is.EqualTo(new[]
            {
                "leaf/1.0.0", "left/1.0.0", "right/1.0.0", "root/1.0.0", "shared/2.0.0"
            }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Issue108_RealDependencyCyclesRemainErrors(bool indirect)
    {
        using var http = new HttpClient(new ManifestHandler(new Dictionary<string, string[]>
        {
            ["root/1.0.0"] = [indirect ? "child/1.0.0" : "root/1.0.0"],
            ["child/1.0.0"] = ["root/1.0.0"]
        }));
        var resolver = new DependencyResolver(new GitHubPackageSource(http));
        var error = Assert.ThrowsAsync<CircularDependencyException>(async () =>
            await resolver.ResolveAsync([Reference("root/1.0.0")]));

        var expected = indirect
            ? "Circular dependency detected: example/packages/root \u2192 example/packages/child \u2192 example/packages/root"
            : "Circular dependency detected: example/packages/root \u2192 example/packages/root";
        Assert.That(error!.Message, Is.EqualTo(expected));
    }

    private static PackageReference Reference(string identifier)
    {
        var parts = identifier.Split('/');
        return PackageReference.Parse($"github.com/example/packages/{parts[0]}: {parts[1]}");
    }

    private sealed class ManifestHandler(IReadOnlyDictionary<string, string[]> dependencies)
        : HttpMessageHandler
    {
        internal List<string> Refs { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            var requestedRef = Uri.UnescapeDataString(uri.Query["?ref=".Length..]);
            Refs.Add(requestedRef);
            if (!uri.AbsolutePath.EndsWith("/cop.json", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""[{"name":"cop.json","type":"file"}]""")
                });
            }

            var name = uri.AbsolutePath.Split('/')[^2];
            var tag = requestedRef.Contains('/') ? requestedRef : $"{name}/{requestedRef}";
            var parts = tag.Split('/');
            var manifest = JsonSerializer.Serialize(new
            {
                name = parts[0],
                version = parts[1],
                title = "Fixture",
                description = "Fixture",
                authors = "tests",
                dependencies = dependencies[tag].Select(dependency => Reference(dependency).ToString())
            });
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    content,
                    encoding = "base64"
                }))
            });
        }
    }
}
