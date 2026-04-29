using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class JavaScriptProjectDiscoveryTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-jsproject-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Test]
    public void Discover_SinglePackage_ExtractsNameAndDeps()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), """
            {
                "name": "my-app",
                "version": "1.0.0",
                "dependencies": {
                    "express": "^4.18.0",
                    "lodash": "^4.17.21"
                },
                "devDependencies": {
                    "jest": "^29.0.0"
                }
            }
            """);

        var projects = JavaScriptProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("my-app"));
        Assert.That(projects[0].Language, Is.EqualTo("javascript"));
        Assert.That(projects[0].References, Does.Contain("express"));
        Assert.That(projects[0].References, Does.Contain("lodash"));
        Assert.That(projects[0].References, Does.Contain("jest"));
    }

    [Test]
    public void Discover_MonorepoWorkspaces()
    {
        // Root package
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), """
            {
                "name": "monorepo",
                "private": true,
                "workspaces": ["packages/*"]
            }
            """);

        // Sub-packages
        var pkgA = Path.Combine(_tempDir, "packages", "pkg-a");
        var pkgB = Path.Combine(_tempDir, "packages", "pkg-b");
        Directory.CreateDirectory(pkgA);
        Directory.CreateDirectory(pkgB);

        File.WriteAllText(Path.Combine(pkgA, "package.json"), """
            {
                "name": "@mono/pkg-a",
                "dependencies": { "@mono/pkg-b": "workspace:*" }
            }
            """);

        File.WriteAllText(Path.Combine(pkgB, "package.json"), """
            {
                "name": "@mono/pkg-b",
                "dependencies": {}
            }
            """);

        var projects = JavaScriptProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(3));
        var pkgAProject = projects.First(p => p.Name == "@mono/pkg-a");
        Assert.That(pkgAProject.References, Does.Contain("@mono/pkg-b"));
    }

    [Test]
    public void Discover_SkipsNodeModules()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), """
            { "name": "my-app", "dependencies": {} }
            """);

        var nodeModules = Path.Combine(_tempDir, "node_modules", "some-dep");
        Directory.CreateDirectory(nodeModules);
        File.WriteAllText(Path.Combine(nodeModules, "package.json"), """
            { "name": "some-dep", "version": "1.0.0" }
            """);

        var projects = JavaScriptProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("my-app"));
    }

    [Test]
    public void Discover_NoNameField_Skipped()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), """
            { "version": "1.0.0", "dependencies": {} }
            """);

        var projects = JavaScriptProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Is.Empty);
    }
}
