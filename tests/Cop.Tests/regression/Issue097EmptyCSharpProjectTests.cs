using NUnit.Framework;

namespace Cop.Tests;

public partial class ToolchainCliIntegrationTests
{
    [TestCase("")]
    [TestCase(" \r\n\t")]
    public async Task Issue097_EmptyProject_WarnsOnceWithoutDroppingSourcesOrProjectEdges(string contents)
    {
        WriteCSharpProjectFixture(contents);

        var verify = await Run("verify", _script);
        Assert.That(verify.ExitCode, Is.EqualTo(0), verify.StandardError);

        for (int run = 0; run < 2; run++)
        {
            var result = await Run(_script, "-t", _root, "--no-color");

            Assert.That(result.ExitCode, Is.EqualTo(0), result.StandardError);
            Assert.That(result.StandardOutput.Replace("\r\n", "\n").Trim(), Is.EqualTo("2\n2\n[app]\nInt32"));
            Assert.That(result.StandardError.Trim(), Is.EqualTo(
                $"Warning: csharp: Project '{Path.Combine(_root, "app", "app.csproj")}' is empty; " +
                "analyzing its C# sources without project metadata."));
        }
    }

    [Test]
    public async Task Issue097_NonEmptyMalformedProject_RemainsAnAnalysisError()
    {
        WriteCSharpProjectFixture("<Project>");

        var result = await Run(_script, "-t", _root, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(1), result.StandardError);
        Assert.That(result.StandardError, Does.Contain("source analysis error(s)")
            .And.Contain("app.csproj").And.Not.Contain("is empty;"));
    }

    [Test]
    public async Task Issue097_EmptyProject_DoesNotHideUnresolvedSemanticFacts()
    {
        WriteCSharpProjectFixture("");
        File.WriteAllText(Path.Combine(_root, "app", "App.cs"),
            "public class App { public int Run() => Missing.Abs(-1); }");

        var result = await Run(_script, "-t", _root, "--no-color");

        Assert.That(result.ExitCode, Is.EqualTo(1), result.StandardError);
        Assert.That(result.StandardOutput.Replace("\r\n", "\n").Trim(), Is.EqualTo("2\n2\n[app]\nnull"));
        Assert.That(result.StandardError, Does.Contain("is empty;")
            .And.Contain("source analysis error(s)").And.Contain("CS0103").And.Contain("Missing"));
    }

    private void WriteCSharpProjectFixture(string contents)
    {
        var app = Path.Combine(_root, "app");
        var host = Path.Combine(_root, "host");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(host);
        File.WriteAllText(Path.Combine(app, "app.csproj"), contents);
        File.WriteAllText(Path.Combine(app, "App.cs"),
            "public class App { public int Run() => System.Math.Abs(-1); }");
        File.WriteAllText(Path.Combine(host, "Host.cs"), "public class Host {}");
        File.WriteAllText(Path.Combine(host, "host.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><ProjectReference Include="../app/app.csproj" /></ItemGroup>
            </Project>
            """);
        var repo = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));
        var packages = Path.Combine(repo, "packages").Replace('\\', '/');
        File.WriteAllText(_script, $"""
            feed '{packages}'
            import code
            import csharp
            let cb = csharp.parse()
            predicate isHost(Project) => Project.Name == 'host'
            predicate isAbs(Statement) => Statement.MemberName == 'Abs'
            command MAIN = print(cb.Projects.Count)
                & print(cb.Types.Count)
                & foreach cb.Projects:isHost => print(item.References)
                & foreach cb.Calls:isAbs => print(item.ValueType.Name)
            """);
    }
}
