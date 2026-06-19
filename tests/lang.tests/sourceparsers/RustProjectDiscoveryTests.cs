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

        // References = production dependencies only (used for layering); dev/build deps are
        // tracked in Packages so a prod layering rule isn't tripped by test-only crates.
        var refs = projects[0].References;
        var packages = projects[0].Packages;
        Assert.That(refs, Does.Contain("log"));
        Assert.That(refs, Does.Not.Contain("criterion"), "dev-dependencies must not be in References");
        Assert.That(refs, Does.Not.Contain("cc"), "build-dependencies must not be in References");
        Assert.That(packages, Does.Contain("criterion"));
        Assert.That(packages, Does.Contain("cc"));
    }

    // Regression: a renamed dependency `key = { package = "real" }` must record the real
    // crate name (used by layering), not the local alias.
    [Test]
    public void Discover_RenamedDependency_RecordsRealCrate()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), """
            [package]
            name = "my_crate"

            [dependencies]
            reqwest = { package = "azure_core_reqwest", version = "1" }
            plain = "1.0"
            """);

        var refs = RustProjectDiscovery.Discover(_tempDir, null)[0].References;
        Assert.That(refs, Does.Contain("azure_core_reqwest"));
        Assert.That(refs, Does.Not.Contain("reqwest"), "the local alias must not be recorded");
        Assert.That(refs, Does.Contain("plain"));
    }

    // Regression: a multi-line inline dependency table must not inject phantom deps from its
    // version/features keys, and a `package =` rename inside it is honored.
    [Test]
    public void Discover_MultiLineInlineTable_NoPhantomDeps()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), """
            [package]
            name = "my_crate"

            [dependencies]
            tokio = {
                version = "1",
                features = ["full"]
            }
            serde = "1"
            """);

        var refs = RustProjectDiscovery.Discover(_tempDir, null)[0].References;
        Assert.That(refs, Does.Contain("tokio"));
        Assert.That(refs, Does.Contain("serde"));
        Assert.That(refs, Does.Not.Contain("version"));
        Assert.That(refs, Does.Not.Contain("features"));
    }

    // Regression: [workspace.dependencies] defines versions for the workspace, not the root
    // crate's own dependencies, so those crates must not appear in the root crate's References.
    [Test]
    public void Discover_WorkspaceDependencies_NotAttributedToRootCrate()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), """
            [package]
            name = "my_root_crate"

            [dependencies]
            log = "0.4"

            [workspace.dependencies]
            serde = "1.0"
            tokio = { version = "1" }
            """);

        var refs = RustProjectDiscovery.Discover(_tempDir, null)[0].References;
        Assert.That(refs, Does.Contain("log"));
        Assert.That(refs, Does.Not.Contain("serde"), "workspace.dependencies are not the crate's deps");
        Assert.That(refs, Does.Not.Contain("tokio"));
    }

    // Regression: a trailing comment on a [package]/[dependencies] header must not break
    // section detection (previously dropped the crate name / whole section).
    [Test]
    public void Discover_HeaderTrailingComment_StillParsed()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), """
            [package] # the main crate
            name = "my_crate"

            [dependencies] # third-party
            serde = "1"
            """);

        var projects = RustProjectDiscovery.Discover(_tempDir, null);
        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("my_crate"));
        Assert.That(projects[0].References, Does.Contain("serde"));
    }

    // Regression: target-specific dev/build dependency tables.
    [Test]
    public void Discover_TargetDevDependencies_TrackedNotInProdRefs()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Cargo.toml"), """
            [package]
            name = "my_crate"

            [target.'cfg(unix)'.dependencies]
            nix = "0.27"

            [target.'cfg(windows)'.dev-dependencies]
            wintest = "0.1"
            """);

        var projects = RustProjectDiscovery.Discover(_tempDir, null);
        var refs = projects[0].References;
        var packages = projects[0].Packages;
        Assert.That(refs, Does.Contain("nix"), "target normal deps are production deps");
        Assert.That(refs, Does.Not.Contain("wintest"), "target dev-deps are not production deps");
        Assert.That(packages, Does.Contain("wintest"));
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
