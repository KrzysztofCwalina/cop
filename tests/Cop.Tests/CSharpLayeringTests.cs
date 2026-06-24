using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class CSharpLayeringTests
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
        import csharp
        import code
        import code

        let cb = codebase(csharp.parse())

        let service-projects = ['Service']

        predicate isFoundationProject(Project) => Project.Name == 'Foundation'
        predicate isServiceProjectName(string) => string:in(service-projects)
        predicate refsServiceProject(Project) => Project.References:any(isServiceProjectName)

        let layering-violations = cb.Projects:isFoundationProject:refsServiceProject
            :toError('Foundation project {item.Name} must not depend on service project')

        command LAYERING = CHECK(layering-violations)
        """;

    private static string MakeWorkspace(bool foundationDependsOnService)
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-csharplayer-" + Guid.NewGuid().ToString("N")[..8]);
        var foundation = Path.Combine(root, "foundation");
        var service = Path.Combine(root, "service");
        Directory.CreateDirectory(foundation);
        Directory.CreateDirectory(service);

        var projectReference = foundationDependsOnService
            ? """
                  <ItemGroup>
                    <ProjectReference Include="..\service\Service.csproj" />
                  </ItemGroup>
              """
            : "";

        File.WriteAllText(Path.Combine(foundation, "Foundation.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n"
            + projectReference + "\n</Project>\n");
        File.WriteAllText(Path.Combine(foundation, "Foundation.cs"), "namespace Foundation;\npublic class Core { }\n");

        File.WriteAllText(Path.Combine(service, "Service.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(service, "Service.cs"), "namespace Service;\npublic class Client { }\n");

        return root;
    }

    private static EngineResult RunLayering(string workspaceRoot)
    {
        var feedPaths = new List<string> { Path.Combine(RepoRoot, "packages") };
        var scriptPath = Path.Combine(Path.GetTempPath(), "cop-csharplayer-check-" + Guid.NewGuid().ToString("N")[..8] + ".cop");
        File.WriteAllText(scriptPath, LayeringCheck);
        try
        {
            return Engine.RunProject(feedPaths, ["csharp"], workspaceRoot, ["LAYERING"], additionalScriptFiles: [scriptPath]);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Test]
    public void Layering_DetectsForbiddenProjectDependency()
    {
        var workspace = MakeWorkspace(foundationDependsOnService: true);
        try
        {
            var result = RunLayering(workspace);

            Assert.That(result.HasFatalErrors, Is.False,
                "Layering check should run cleanly. Errors: " + string.Join("; ", result.Errors));
            Assert.That(result.Outputs.Any(o => o.Message.Contains("Foundation project Foundation must not depend on service project")),
                Is.True,
                "Expected a layering violation: Foundation depends on Service. Outputs: "
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
            Assert.That(result.Outputs.Any(o => o.Message.Contains("Foundation project")),
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

