using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class CSharpProjectDiscoveryTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-project-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Test]
    public void Discover_SingleProject_NoReferences()
    {
        var csproj = Path.Combine(_tempDir, "MyApp.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var projects = CSharpProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("MyApp"));
        Assert.That(projects[0].Language, Is.EqualTo("csharp"));
        Assert.That(projects[0].References, Is.Empty);
    }

    [Test]
    public void Discover_WithAssemblyName_UsesAssemblyName()
    {
        var csproj = Path.Combine(_tempDir, "my-project.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>MyApp.Core</AssemblyName>
              </PropertyGroup>
            </Project>
            """);

        var projects = CSharpProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("MyApp.Core"));
    }

    [Test]
    public void Discover_ProjectReferences_ResolvedByPath()
    {
        // Create two projects: Web references Core
        var webDir = Path.Combine(_tempDir, "src", "Web");
        var coreDir = Path.Combine(_tempDir, "src", "Core");
        Directory.CreateDirectory(webDir);
        Directory.CreateDirectory(coreDir);

        File.WriteAllText(Path.Combine(coreDir, "Core.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(webDir, "Web.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Core\Core.csproj" />
              </ItemGroup>
            </Project>
            """);

        var projects = CSharpProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(2));
        var web = projects.First(p => p.Name == "Web");
        var core = projects.First(p => p.Name == "Core");

        Assert.That(web.References, Has.Count.EqualTo(1));
        Assert.That(web.References[0], Is.EqualTo("Core"));
        Assert.That(core.References, Is.Empty);
    }

    [Test]
    public void Discover_ExcludesDirectories()
    {
        var binDir = Path.Combine(_tempDir, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "ShouldIgnore.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(_tempDir, "MyApp.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var excluded = new HashSet<string> { "bin" };
        var projects = CSharpProjectDiscovery.Discover(_tempDir, excluded);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("MyApp"));
    }

    [Test]
    public void Discover_MultipleProjectReferences()
    {
        var apiDir = Path.Combine(_tempDir, "Api");
        var svcDir = Path.Combine(_tempDir, "Services");
        var dataDir = Path.Combine(_tempDir, "Data");
        Directory.CreateDirectory(apiDir);
        Directory.CreateDirectory(svcDir);
        Directory.CreateDirectory(dataDir);

        File.WriteAllText(Path.Combine(dataDir, "Data.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(svcDir, "Services.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Data\Data.csproj" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(apiDir, "Api.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Services\Services.csproj" />
                <ProjectReference Include="..\Data\Data.csproj" />
              </ItemGroup>
            </Project>
            """);

        var projects = CSharpProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(3));
        var api = projects.First(p => p.Name == "Api");
        Assert.That(api.References, Has.Count.EqualTo(2));
        Assert.That(api.References, Does.Contain("Services"));
        Assert.That(api.References, Does.Contain("Data"));
    }

    [Test]
    public void Discover_RelativePath_NormalizedWithForwardSlash()
    {
        var subDir = Path.Combine(_tempDir, "src", "MyLib");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "MyLib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var projects = CSharpProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects[0].Path, Is.EqualTo("src/MyLib/MyLib.csproj"));
    }
}
