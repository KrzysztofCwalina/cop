using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class JavaScriptLayeringTests
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
        import javascript
        import code
        import code

        let cb = codebase(javascript.parse())

        let service-packages = ['service']

        predicate isFoundationPackage(Project) => Project.Name == 'foundation'
        predicate isServicePackageName(string) => string:in(service-packages)
        predicate refsServicePackage(Project) => Project.References:any(isServicePackageName)

        let layering-violations = cb.Projects:isFoundationPackage:refsServicePackage
            :toError('Foundation package {item.Name} must not depend on service package')

        command LAYERING = CHECK(layering-violations)
        """;

    private static string MakeWorkspace(bool foundationDependsOnService)
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-jslayer-" + Guid.NewGuid().ToString("N")[..8]);
        var foundation = Path.Combine(root, "foundation");
        var service = Path.Combine(root, "service");
        Directory.CreateDirectory(foundation);
        Directory.CreateDirectory(service);

        var foundationDependencies = foundationDependsOnService
            ? """
              ,
                "dependencies": {
                  "service": "1.0.0"
                }
              """
            : "";
        File.WriteAllText(Path.Combine(foundation, "package.json"),
            "{\n  \"name\": \"foundation\",\n  \"version\": \"1.0.0\"" + foundationDependencies + "\n}\n");
        File.WriteAllText(Path.Combine(foundation, "index.js"), "export class Core {}\n");

        File.WriteAllText(Path.Combine(service, "package.json"), """
            {
              "name": "service",
              "version": "1.0.0",
              "dependencies": {
                "foundation": "1.0.0"
              }
            }
            """);
        File.WriteAllText(Path.Combine(service, "index.js"), "export class Client {}\n");

        return root;
    }

    private static EngineResult RunLayering(string workspaceRoot)
    {
        var feedPaths = new List<string> { Path.Combine(RepoRoot, "packages") };
        var scriptPath = Path.Combine(Path.GetTempPath(), "cop-jslayer-check-" + Guid.NewGuid().ToString("N")[..8] + ".cop");
        File.WriteAllText(scriptPath, LayeringCheck);
        try
        {
            return Engine.RunProject(feedPaths, ["javascript"], workspaceRoot, ["LAYERING"], additionalScriptFiles: [scriptPath]);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Test]
    public void Layering_DetectsForbiddenPackageDependency()
    {
        var workspace = MakeWorkspace(foundationDependsOnService: true);
        try
        {
            var result = RunLayering(workspace);

            Assert.That(result.HasFatalErrors, Is.False,
                "Layering check should run cleanly. Errors: " + string.Join("; ", result.Errors));
            Assert.That(result.Outputs.Any(o => o.Message.Contains("Foundation package foundation must not depend on service package")),
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
            Assert.That(result.Outputs.Any(o => o.Message.Contains("Foundation package")),
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

