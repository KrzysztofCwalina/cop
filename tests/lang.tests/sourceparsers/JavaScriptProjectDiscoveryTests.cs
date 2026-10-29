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

    [Test]
    public void Discover_AllDependencyKinds_DeduplicatesWithExplicitScope()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), """
            {
                "name": "app",
                "dependencies": { "shared": "^1" },
                "devDependencies": { "shared": "^1", "test": "^2" },
                "peerDependencies": { "peer": "*" },
                "optionalDependencies": { "optional": "*" }
            }
            """);
        var project = JavaScriptProjectDiscovery.Discover(_tempDir, null).Single();
        Assert.That(project.References, Is.EqualTo(new[] { "optional", "peer", "shared", "test" }));
        Assert.That(project.Packages, Is.EqualTo(project.References));
        Assert.That(project.Properties["DiscoveryScope"], Does.Contain("no lockfile resolution"));
        Assert.That(project.Properties.Keys, Does.Contain("peerDependencies").And.Contain("optionalDependencies"));
    }

    [Test]
    public void Discover_WorkspaceObjectAndExclusion_DoesNotAssumeEveryNestedPackageIsAMember()
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), """
            { "name": "root", "workspaces": { "packages": ["packages/*", "!packages/excluded"] } }
            """);
        foreach (var name in new[] { "member", "excluded" })
        {
            var directory = Path.Combine(_tempDir, "packages", name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "package.json"), """{"name":""" + "\"" + name + "\"}");
        }
        var projects = JavaScriptProjectDiscovery.Discover(_tempDir, null);
        Assert.That(projects, Has.Count.EqualTo(3));
        Assert.That(projects.Single(p => p.Name == "member").Properties["WorkspaceRoot"], Is.EqualTo("root"));
        Assert.That(projects.Single(p => p.Name == "excluded").Properties.ContainsKey("WorkspaceRoot"), Is.False);
    }

    [Test]
    public void Discover_TypeScriptConfig_UsesCompilerMembershipExtendsAliasesAndReferences()
    {
        if (Cop.Providers.SourceParsers.JavaScriptToolchain.Discover(Path.Combine(_tempDir, "source.ts")) is null)
            Assert.Ignore("Native configuration tests require an installed TypeScript compiler.");
        Directory.CreateDirectory(Path.Combine(_tempDir, "src"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "referenced"));
        File.WriteAllText(Path.Combine(_tempDir, "base.json"), """
            { "compilerOptions": { "baseUrl": ".", "paths": { "@app/*": ["src/*"] } } }
            """);
        File.WriteAllText(Path.Combine(_tempDir, "tsconfig.json"), """
            {
                // Compiler JSONC and inherited path aliases are intentional.
                "extends": "./base.json",
                "include": ["src/**/*.ts"],
                "exclude": ["src/ignored.ts"],
                "references": [{ "path": "./referenced" }],
            }
            """);
        File.WriteAllText(Path.Combine(_tempDir, "referenced", "tsconfig.json"), """{ "files": ["library.ts"] }""");
        File.WriteAllText(Path.Combine(_tempDir, "referenced", "library.ts"), "export class Library {}");
        File.WriteAllText(Path.Combine(_tempDir, "src", "included.ts"), "export class Included {}");
        File.WriteAllText(Path.Combine(_tempDir, "src", "ignored.ts"), "export class Ignored {}");
        using var scope = new Cop.Core.ToolchainExecutionScope();
        var projects = JavaScriptProjectDiscovery.Discover(_tempDir, null);
        Assert.That(projects, Has.Count.EqualTo(2));
        var project = projects.Single(p => p.Path == "tsconfig.json");
        Assert.That(project.SourceFiles, Is.EqualTo(new[] { Path.Combine(_tempDir, "src", "included.ts") }));
        Assert.That(project.References, Is.EqualTo(new[] { "referenced:tsconfig" }));
        Assert.That(project.Properties["PathAliases"], Is.EqualTo("""{"@app/*":["src/*"]}"""));
        Assert.That(project.Properties["BaseUrl"], Is.EqualTo(_tempDir.Replace('\\', '/')).Or.EqualTo(_tempDir));
    }

    [TestCase("packages/*", "packages/member", true)]
    [TestCase("packages/*", "packages/nested/member", false)]
    [TestCase("packages/?", "packages/a", true)]
    [TestCase("packages/?", "packages/ab", false)]
    [TestCase("**/member", "member", true)]
    [TestCase("**/member", "packages/nested/member", true)]
    [TestCase("**/member", "notmember", false)]
    [TestCase("packages/**/member", "packages/member", true)]
    [TestCase("packages/**/member", "packages/nested/member", true)]
    [TestCase("packages/**/member", "packages/notmember", false)]
    [TestCase("packages/**", "packages/nested/member", true)]
    [TestCase("packages/[literal]", "packages/[literal]", true)]
    [TestCase("packages/[literal]", "packages/l", false)]
    public void Discover_WorkspaceGlob_HonorsSegmentsRecursiveDirectoriesAndLiteralCharacters(
        string pattern, string member, bool expected)
    {
        File.WriteAllText(Path.Combine(_tempDir, "package.json"),
            System.Text.Json.JsonSerializer.Serialize(new { name = "root", workspaces = new[] { pattern } }));
        var directory = Path.Combine(_tempDir, member.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "package.json"), """{ "name": "member" }""");
        var project = JavaScriptProjectDiscovery.Discover(_tempDir, null).Single(p => p.Name == "member");
        Assert.That(project.Properties.ContainsKey("WorkspaceRoot"), Is.EqualTo(expected));
    }

    [Test]
    public void Discover_MalformedManifest_ReportsErrorInsteadOfSilentlyDroppingIt()
    {
        Cop.Core.ProviderErrors.Clear();
        File.WriteAllText(Path.Combine(_tempDir, "package.json"), """{ "name": "bad", "dependencies": [] }""");
        Assert.That(JavaScriptProjectDiscovery.Discover(_tempDir, null), Is.Empty);
        Assert.That(Cop.Core.ProviderErrors.Drain().Single(), Does.Contain("'dependencies' must be an object"));
    }

    [Test]
    public void Discover_MissingCompiler_ReportsUnknownConfigMembership()
    {
        Cop.Core.ProviderWarnings.Clear();
        File.WriteAllText(Path.Combine(_tempDir, "tsconfig.json"), """{ "files": [] }""");
        Assert.That(JavaScriptProjectDiscovery.Discover(_tempDir, null, toolchainSearchPath: ""), Is.Empty);
        Assert.That(Cop.Core.ProviderWarnings.Drain().Single(), Does.Contain("membership").And.Contain("unknown"));
    }
}
