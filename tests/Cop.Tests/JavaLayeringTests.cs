using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class JavaLayeringTests
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
        import java
        import code
        import code-layering

        let cb = codebase(java.parse())

        let service-projects = ['com.example:service']

        predicate isFoundationProject(Project) => Project.Name == 'foundation'
        predicate isServiceProjectReference(string) => string:in(service-projects)
        predicate refsServiceProject(Project) => Project.References:any(isServiceProjectReference)

        let layering-violations = cb.Projects:isFoundationProject:refsServiceProject
            :toError('Foundation project {item.Name} must not depend on service project')

        command LAYERING = CHECK(layering-violations)
        """;

    private static string MakeWorkspace(bool foundationDependsOnService)
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-javalayer-" + Guid.NewGuid().ToString("N")[..8]);
        var foundation = Path.Combine(root, "foundation");
        var service = Path.Combine(root, "service");
        Directory.CreateDirectory(Path.Combine(foundation, "src", "main", "java", "com", "example"));
        Directory.CreateDirectory(Path.Combine(service, "src", "main", "java", "com", "example"));

        var foundationDependencies = foundationDependsOnService
            ? """
                  <dependencies>
                    <dependency>
                      <groupId>com.example</groupId>
                      <artifactId>service</artifactId>
                      <version>1.0</version>
                    </dependency>
                  </dependencies>
              """
            : "";
        File.WriteAllText(Path.Combine(foundation, "pom.xml"),
            "<project>\n  <modelVersion>4.0.0</modelVersion>\n  <groupId>com.example</groupId>\n  <artifactId>foundation</artifactId>\n  <version>1.0</version>\n"
            + foundationDependencies + "\n</project>\n");
        File.WriteAllText(Path.Combine(foundation, "src", "main", "java", "com", "example", "Core.java"),
            "package com.example;\npublic class Core { }\n");

        File.WriteAllText(Path.Combine(service, "pom.xml"), """
            <project>
              <modelVersion>4.0.0</modelVersion>
              <groupId>com.example</groupId>
              <artifactId>service</artifactId>
              <version>1.0</version>
              <dependencies>
                <dependency>
                  <groupId>com.example</groupId>
                  <artifactId>foundation</artifactId>
                  <version>1.0</version>
                </dependency>
              </dependencies>
            </project>
            """);
        File.WriteAllText(Path.Combine(service, "src", "main", "java", "com", "example", "Client.java"),
            "package com.example;\npublic class Client { }\n");

        return root;
    }

    private static EngineResult RunLayering(string workspaceRoot)
    {
        var feedPaths = new List<string> { Path.Combine(RepoRoot, "packages") };
        var scriptPath = Path.Combine(Path.GetTempPath(), "cop-javalayer-check-" + Guid.NewGuid().ToString("N")[..8] + ".cop");
        File.WriteAllText(scriptPath, LayeringCheck);
        try
        {
            return Engine.RunProject(feedPaths, ["java"], workspaceRoot, ["LAYERING"], additionalScriptFiles: [scriptPath]);
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
            Assert.That(result.Outputs.Any(o => o.Message.Contains("Foundation project foundation must not depend on service project")),
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

