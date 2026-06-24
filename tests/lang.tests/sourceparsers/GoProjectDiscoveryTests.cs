using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class GoProjectDiscoveryTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-goproject-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Test]
    public void Discover_SingleLineRequire_ExtractsModuleNameAndDependencyPath()
    {
        File.WriteAllText(Path.Combine(_tempDir, "go.mod"), """
            module example.com/foundation

            go 1.22

            require example.com/service v1.0.0
            """);

        var projects = GoProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("foundation"));
        Assert.That(projects[0].Language, Is.EqualTo("go"));
        Assert.That(projects[0].References, Has.Count.EqualTo(1));
        Assert.That(projects[0].References, Is.EqualTo(new[] { "example.com/service" }));
    }

    [Test]
    public void Discover_RequireBlock_ExtractsAllDependencyPaths()
    {
        File.WriteAllText(Path.Combine(_tempDir, "go.mod"), """
            module example.com/service

            go 1.22

            require (
                example.com/foundation v1.0.0
                github.com/stretchr/testify v1.8.4
            )
            """);

        var projects = GoProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("service"));
        Assert.That(projects[0].References, Has.Count.EqualTo(2));
        Assert.That(projects[0].References, Is.EqualTo(new[] { "example.com/foundation", "github.com/stretchr/testify" }));
    }
}

