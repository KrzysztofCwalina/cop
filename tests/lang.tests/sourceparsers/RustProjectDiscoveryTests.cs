using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class RustProjectDiscoveryTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-rustproject-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Test]
    public void Discover_SingleCrate_ExtractsNameAndDeps()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), """
            [package]
            name = "my_crate"
            version = "0.1.0"

            [dependencies]
            serde = "1.0"
            tokio = { version = "1", features = ["full"] }
            """);

        var projects = RustProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("my_crate"));
        Assert.That(projects[0].Language, Is.EqualTo("rust"));
        Assert.That(projects[0].References, Does.Contain("serde"));
        Assert.That(projects[0].References, Does.Contain("tokio"));
    }

    // Regression: the common Cargo workspace shorthand `dep.workspace = true`
    // (and `dep.path = "..."`, `dep.version = "..."`) was not captured.
    [Test]
    public void Discover_DottedWorkspaceDeps_AreCaptured()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), """
            [package]
            name = "azure_storage_blob"

            [dependencies]
            azure_core = { workspace = true, features = ["xml"] }
            azure_identity.workspace = true
            azure_storage_common.path = "../azure_storage_common"
            serde.version = "1.0"
            """);

        var projects = RustProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        var refs = projects[0].References;
        Assert.That(refs, Does.Contain("azure_core"));
        Assert.That(refs, Does.Contain("azure_identity"));
        Assert.That(refs, Does.Contain("azure_storage_common"));
        Assert.That(refs, Does.Contain("serde"));
    }

    // Regression: sub-table dependency headers like [dependencies.NAME] and
    // target-specific [target.'cfg(...)'.dependencies] were not recognized.
    [Test]
    public void Discover_SubTableAndTargetDeps_AreCaptured()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), """
            [package]
            name = "platform_crate"

            [dependencies.serde]
            version = "1.0"
            features = ["derive"]

            [target.'cfg(windows)'.dependencies]
            winapi = "0.3"

            [target.'cfg(unix)'.dependencies.nix]
            version = "0.27"
            """);

        var projects = RustProjectDiscovery.Discover(_tempDir, null);

        var refs = projects[0].References;
        Assert.That(refs, Does.Contain("serde"));
        Assert.That(refs, Does.Contain("winapi"));
        Assert.That(refs, Does.Contain("nix"));
        // The sub-table key lines (version/features) must NOT be treated as deps.
        Assert.That(refs, Does.Not.Contain("version"));
        Assert.That(refs, Does.Not.Contain("features"));
    }

    [Test]
    public void Discover_DevAndBuildDependencies_AreCaptured()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), """
            [package]
            name = "my_crate"

            [dependencies]
            log = "0.4"

            [dev-dependencies]
            criterion = "0.5"

            [build-dependencies]
            cc = "1.0"
            """);

        var projects = RustProjectDiscovery.Discover(_tempDir, null);

        var refs = projects[0].References;
        Assert.That(refs, Does.Contain("log"));
        Assert.That(refs, Does.Contain("criterion"));
        Assert.That(refs, Does.Contain("cc"));
    }

    [Test]
    public void Discover_Workspace_FindsNestedCrates()
    {
        // Virtual workspace root (no [package]).
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), """
            [workspace]
            members = ["crates/core", "crates/service"]
            """);

        var coreDir = Path.Combine(_tempDir, "crates", "core");
        var serviceDir = Path.Combine(_tempDir, "crates", "service");
        Directory.CreateDirectory(coreDir);
        Directory.CreateDirectory(serviceDir);

        File.WriteAllText(Path.Combine(coreDir, "Cargo.toml"), """
            [package]
            name = "core"

            [dependencies]
            """);

        File.WriteAllText(Path.Combine(serviceDir, "Cargo.toml"), """
            [package]
            name = "service"

            [dependencies]
            core = { path = "../core" }
            """);

        var projects = RustProjectDiscovery.Discover(_tempDir, null);

        var names = projects.Select(p => p.Name).ToList();
        Assert.That(names, Does.Contain("core"));
        Assert.That(names, Does.Contain("service"));
        var service = projects.First(p => p.Name == "service");
        Assert.That(service.References, Does.Contain("core"));
    }

    [Test]
    public void Discover_SkipsTargetDirectory()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), """
            [package]
            name = "my_crate"
            """);

        var targetCrate = Path.Combine(_tempDir, "target", "debug", "some_dep");
        Directory.CreateDirectory(targetCrate);
        File.WriteAllText(Path.Combine(targetCrate, "Cargo.toml"), """
            [package]
            name = "should_be_skipped"
            """);

        var projects = RustProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("my_crate"));
    }

    [Test]
    public void Discover_VirtualManifest_NoPackage_Skipped()
    {
        // A pure workspace manifest with no [package] should not yield a project.
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), """
            [workspace]
            members = []
            """);

        var projects = RustProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Is.Empty);
    }
}
