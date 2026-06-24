using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class GoLayeringTests
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
        import go
        import code
        import code

        let cb = codebase(go.parse())

        let service-modules = ['example.com/service']

        predicate isFoundationModule(Project) => Project.Name == 'foundation'
        predicate isServiceModulePath(string) => string:in(service-modules)
        predicate refsServiceModule(Project) => Project.References:any(isServiceModulePath)

        let layering-violations = cb.Projects:isFoundationModule:refsServiceModule
            :toError('Foundation module {item.Name} must not depend on service module')

        command LAYERING = CHECK(layering-violations)
        """;

    private static string MakeWorkspace(bool foundationDependsOnService)
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-golayer-" + Guid.NewGuid().ToString("N")[..8]);
        var foundation = Path.Combine(root, "foundation");
        var service = Path.Combine(root, "service");
        Directory.CreateDirectory(foundation);
        Directory.CreateDirectory(service);

        var foundationRequire = foundationDependsOnService ? "\nrequire example.com/service v1.0.0\n" : "";
        File.WriteAllText(Path.Combine(foundation, "go.mod"),
            "module example.com/foundation\n\ngo 1.22\n" + foundationRequire);
        File.WriteAllText(Path.Combine(foundation, "core.go"), "package foundation\ntype Core struct{}\n");

        File.WriteAllText(Path.Combine(service, "go.mod"), """
            module example.com/service

            go 1.22

            require example.com/foundation v1.0.0
            """);
        File.WriteAllText(Path.Combine(service, "client.go"), "package service\ntype Client struct{}\n");

        return root;
    }

    private static EngineResult RunLayering(string workspaceRoot)
    {
        var feedPaths = new List<string> { Path.Combine(RepoRoot, "packages") };
        var scriptPath = Path.Combine(Path.GetTempPath(), "cop-golayer-check-" + Guid.NewGuid().ToString("N")[..8] + ".cop");
        File.WriteAllText(scriptPath, LayeringCheck);
        try
        {
            return Engine.RunProject(feedPaths, ["go"], workspaceRoot, ["LAYERING"], additionalScriptFiles: [scriptPath]);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Test]
    public void Layering_DetectsForbiddenModuleDependency()
    {
        var workspace = MakeWorkspace(foundationDependsOnService: true);
        try
        {
            var result = RunLayering(workspace);

            Assert.That(result.HasFatalErrors, Is.False,
                "Layering check should run cleanly. Errors: " + string.Join("; ", result.Errors));
            Assert.That(result.Outputs.Any(o => o.Message.Contains("Foundation module foundation must not depend on service module")),
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
            Assert.That(result.Outputs.Any(o => o.Message.Contains("Foundation module")),
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

