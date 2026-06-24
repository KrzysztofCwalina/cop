using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class PythonProjectDiscoveryTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-pythonproject-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Test]
    public void Discover_Pyproject_ExtractsNameAndVersionStrippedDependencies()
    {
        File.WriteAllText(Path.Combine(_tempDir, "pyproject.toml"), """
            [project]
            name = "foundation"
            dependencies = ["service>=1.0", "requests==2.31.0", "typing-extensions~=4.0"]
            """);

        var projects = PythonProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("foundation"));
        Assert.That(projects[0].Language, Is.EqualTo("python"));
        Assert.That(projects[0].References, Has.Count.EqualTo(3));
        Assert.That(projects[0].References, Is.EqualTo(new[] { "service", "requests", "typing-extensions" }));
    }

    [Test]
    public void Discover_SetupPy_ExtractsNameAndInstallRequires()
    {
        File.WriteAllText(Path.Combine(_tempDir, "setup.py"), """
            from setuptools import setup

            setup(
                name='service',
                install_requires=[
                    'foundation>=1.0',
                    "numpy<2",
                ],
            )
            """);

        var projects = PythonProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("service"));
        Assert.That(projects[0].References, Has.Count.EqualTo(2));
        Assert.That(projects[0].References, Is.EqualTo(new[] { "foundation", "numpy" }));
    }

    [Test]
    public void Discover_Pyproject_NoDependencies_HasEmptyReferences()
    {
        File.WriteAllText(Path.Combine(_tempDir, "pyproject.toml"), """
            [project]
            name = "standalone"
            """);

        var projects = PythonProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("standalone"));
        Assert.That(projects[0].References, Is.Empty);
    }

    [Test]
    public void Discover_RelativePath_NormalizedWithForwardSlash()
    {
        var packageDir = Path.Combine(_tempDir, "src", "foundation");
        Directory.CreateDirectory(packageDir);
        File.WriteAllText(Path.Combine(packageDir, "pyproject.toml"), """
            [project]
            name = "foundation"
            """);

        var projects = PythonProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Path, Is.EqualTo("src/foundation/pyproject.toml"));
    }
}

