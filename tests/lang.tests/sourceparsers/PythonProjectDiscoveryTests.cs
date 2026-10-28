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

    [TestCase(true)]
    [TestCase(false)]
    public void Discover_QuotedMetadataOptionalDependenciesAndNestedProjects(bool useToolchain)
    {
        File.WriteAllText(Path.Combine(_tempDir, "pyproject.toml"), """
            [project] # valid inline comment
            name = 'outer'
            dependencies = [
                'requests>=2', # a dependency
                "urllib3[secure]>=1",
            ]
            [project.optional-dependencies] # extras are still project dependencies
            tests = ['pytest>=8']
            """);
        var nested = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "pyproject.toml"), "[project]\nname = 'inner'\n");
        var projects = PythonProjectDiscovery.Discover(_tempDir, null, useToolchain);
        Assert.That(projects.Select(p => p.Name), Is.EqualTo(new[] { "outer", "inner" }));
        Assert.That(projects[0].References, Is.EqualTo(new[] { "requests", "urllib3", "pytest" }));
        Assert.That(projects[1].References, Is.Empty);
    }

    [Test]
    public void Discover_DoesNotExecuteSetupCodeOrTreatUnrelatedNameAsMetadata()
    {
        File.WriteAllText(Path.Combine(_tempDir, "setup.py"), """
            import module_that_does_not_exist
            unrelated_name = 'wrong'
            raise RuntimeError('must not execute')
            setup(name='actual', install_requires=['requests>=2'],
                  extras_require={'test': ['pytest>=8']})
            """);
        var projects = PythonProjectDiscovery.Discover(_tempDir, null);
        Assert.That(projects.Select(p => p.Name), Is.EqualTo(new[] { "actual" }));
        Assert.That(projects[0].References, Is.EqualTo(new[] { "requests", "pytest" }));
    }

    [Test]
    public void Discover_InvalidManifestSurfacesDiagnostic()
    {
        Cop.Core.ProviderErrors.Clear();
        File.WriteAllText(Path.Combine(_tempDir, "pyproject.toml"), "[project]\nname = [");
        Assert.That(PythonProjectDiscovery.Discover(_tempDir, null), Is.Empty);
        var errors = Cop.Core.ProviderErrors.Drain();
        Assert.That(errors, Has.Count.EqualTo(1));
        Assert.That(errors[0], Does.Contain("pyproject.toml").And.Contain("error:"));
    }

    [Test]
    public void Discover_DynamicDependenciesAreExplicitlyReportedAsUnknown()
    {
        Cop.Core.ProviderWarnings.Clear();
        File.WriteAllText(Path.Combine(_tempDir, "setup.py"),
            "setup(name='project', install_requires=read_dependencies())");
        var projects = PythonProjectDiscovery.Discover(_tempDir, null);
        Assert.That(projects.Select(p => p.Name), Is.EqualTo(new[] { "project" }));
        Assert.That(projects.Single().References, Is.Empty);
        var warnings = Cop.Core.ProviderWarnings.Drain();
        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.Contain("install_requires").And.Contain("unknown"));
    }
}
