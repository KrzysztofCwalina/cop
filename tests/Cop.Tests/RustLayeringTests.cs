using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// End-to-end tests proving the language-agnostic <c>code-layering</c> package works with
/// Rust Cargo projects: <c>rust.parse()</c> discovers crates and their dependencies, and a
/// layering rule built from <c>code-layering</c> detects (and does not falsely report)
/// disallowed crate-to-crate dependencies.
/// </summary>
[TestFixture]
public class RustLayeringTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    private const string LayeringCheck = """
        import rust
        import code
        import code-layering

        let cb = codebase(rust.parse())

        let service-crates = ['service']

        predicate isFoundationCrate(Project) => Project.Name == 'foundation'
        predicate isServiceCrateName(string) => string:in(service-crates)
        predicate refsServiceCrate(Project) => Project.References:any(isServiceCrateName)

        let layering-violations = cb.Projects:isFoundationCrate:refsServiceCrate
            :toError('Foundation crate {item.Name} must not depend on a service crate')

        command LAYERING = CHECK(layering-violations)
        """;

    private static string MakeWorkspace(bool foundationDependsOnService)
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-rustlayer-" + Guid.NewGuid().ToString("N")[..8]);
        var foundation = Path.Combine(root, "foundation");
        var service = Path.Combine(root, "service");
        Directory.CreateDirectory(foundation);
        Directory.CreateDirectory(service);

        // foundation: optionally (wrongly) depends on the service crate via the dotted
        // workspace shorthand — also exercises the Cargo dotted-dependency parsing.
        var foundationDeps = foundationDependsOnService ? "service.workspace = true\n" : "";
        File.WriteAllText(Path.Combine(foundation, "Cargo.toml"),
            "[package]\nname = \"foundation\"\n\n[dependencies]\n" + foundationDeps);
        File.WriteAllText(Path.Combine(foundation, "lib.rs"), "pub struct Core;\n");

        // service legitimately depends on foundation.
        File.WriteAllText(Path.Combine(service, "Cargo.toml"),
            "[package]\nname = \"service\"\n\n[dependencies]\nfoundation = { path = \"../foundation\" }\n");
        File.WriteAllText(Path.Combine(service, "lib.rs"), "pub struct Client;\n");

        return root;
    }

    private static EngineResult RunLayering(string workspaceRoot)
    {
        var feedPaths = new List<string> { Path.Combine(RepoRoot, "packages") };
        var scriptPath = Path.Combine(Path.GetTempPath(), "cop-rustlayer-check-" + Guid.NewGuid().ToString("N")[..8] + ".cop");
        File.WriteAllText(scriptPath, LayeringCheck);
        try
        {
            // packageNames: ["rust"] loads the Rust provider; the layering script is supplied
            // as an additional script file. rules: ["LAYERING"] runs that script's command
            // (the rust package itself defines no command). Mirrors a user running a layering .cop.
            return Engine.RunProject(feedPaths, ["rust"], workspaceRoot, ["LAYERING"], additionalScriptFiles: [scriptPath]);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Test]
    public void Layering_DetectsForbiddenCrateDependency()
    {
        var workspace = MakeWorkspace(foundationDependsOnService: true);
        try
        {
            var result = RunLayering(workspace);

            Assert.That(result.HasFatalErrors, Is.False,
                "Layering check should run cleanly. Errors: " + string.Join("; ", result.Errors));
            Assert.That(result.Outputs.Any(o => o.Message.Contains("Foundation crate foundation")),
                Is.True,
                "Expected a layering violation: foundation depends on service. Outputs: "
                + string.Join(" | ", result.Outputs.Select(o => o.Message)));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Test]
    public void Layering_CleanArchitecture_NoViolations()
    {
        var workspace = MakeWorkspace(foundationDependsOnService: false);
        try
        {
            var result = RunLayering(workspace);

            Assert.That(result.HasFatalErrors, Is.False,
                "Layering check should run cleanly. Errors: " + string.Join("; ", result.Errors));
            Assert.That(result.Outputs.Any(o => o.Message.Contains("Foundation crate")),
                Is.False,
                "A clean architecture (foundation depends on nothing) must produce no layering violations. Outputs: "
                + string.Join(" | ", result.Outputs.Select(o => o.Message)));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
