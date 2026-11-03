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
    public void Discover_ExternalLinkedSourcesGiveAnActionableTargetWarning()
    {
        var target = Path.Combine(_tempDir, "App");
        Directory.CreateDirectory(target);
        var project = Path.Combine(target, "App.csproj");
        File.WriteAllText(project, "<Project><ItemGroup><Compile Include=\"..\\Shared\\**\\*.cs\" /></ItemGroup></Project>");
        Cop.Core.ProviderWarnings.Clear();
        try
        {
            Assert.That(CSharpProjectDiscovery.Discover(target, null).Single().SourceFiles, Is.Empty);
            Assert.That(Cop.Core.ProviderWarnings.Drain(), Is.EqualTo(new[]
            {
                $"Warning: csharp: Project '{project}' includes sources outside the analysis target '{target}' (<Compile Include=\"..\\Shared\\**\\*.cs\">). " +
                "Those files are not read, so project symbols may be incomplete. " +
                "Run with '-t' pointing to a common parent directory that contains the project and its linked sources."
            }));
        }
        finally
        {
            Cop.Core.ProviderWarnings.Clear();
        }
    }

    [TestCase("Top.cs", "**/*.cs", true, true)]
    [TestCase("nested/Top.cs", "**/*.cs", true, true)]
    [TestCase("a/b/Top.cs", "a/**/Top.cs", true, true)]
    [TestCase("a/Top.cs", "a/**/Top.cs", true, true)]
    [TestCase("a/b/Top.cs", "a/*.cs", true, false)]
    [TestCase("a/b/Top.cs", "a/**", true, true)]
    [TestCase("Keep2.cs", "Keep?.cs", true, true)]
    [TestCase("Keep.cs", "Keep?.cs", true, false)]
    [TestCase("a/[literal].cs", @"a\[literal].cs", true, true)]
    [TestCase("top.CS", "*.cs", true, true)]
    [TestCase("top.CS", "*.cs", false, false)]
    [TestCase("a/b", "a?b", true, false)]
    [TestCase("", "**/", true, true)]
    [TestCase("Top.cs.extra", "*.cs", true, false)]
    public void SourcePathGlobsPreserveSegmentAndRecursiveMatching(string path, string pattern, bool ignoreCase, bool expected)
    {
        Assert.That(Cop.Core.PathGlob.IsMatch(path, pattern, ignoreCase), Is.EqualTo(expected));
    }

    [Test]
    public void Discover_CompileRemoveAndDefaultExclusionsPreserveExactMembership()
    {
        var files = new[] { "Keep.cs", "samples/Skip.cs", "samples/nested/Skip.cs", "Generated/Skip.cs",
            "obj/Skip.cs", "bin/Skip.cs", ".hidden/Skip.cs" };
        foreach (var relative in files)
        {
            var path = Path.Combine(_tempDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "class Example {}");
        }
        File.WriteAllText(Path.Combine(_tempDir, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><DefaultItemExcludes>Generated\**\*.cs</DefaultItemExcludes></PropertyGroup>
              <ItemGroup><Compile Remove="samples\**\*.cs" /></ItemGroup>
            </Project>
            """);

        Assert.That(CSharpProjectDiscovery.Discover(_tempDir, null).Single().SourceFiles,
            Is.EqualTo(new[] { Path.Combine(_tempDir, "Keep.cs") }));
    }

    [TestCase("EnableDefaultItems")]
    [TestCase("EnableDefaultCompileItems")]
    public void Discover_DisabledDefaultsAndLiteralGlobsPreserveExactMembership(string property)
    {
        var files = new[] { "Keep.cs", "Keep2.cs", "Nested/Keep3.cs", "Nested/Skip.cs", "Other.cs" };
        foreach (var relative in files)
        {
            var path = Path.Combine(_tempDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "class Example {}");
        }
        File.WriteAllText(Path.Combine(_tempDir, "App.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><{property}>false</{property}></PropertyGroup>
              <ItemGroup>
                <Compile Include="**\Keep?.cs;Other.cs" Exclude="Nested\Keep3.cs" />
                <Compile Remove="Other.cs" />
              </ItemGroup>
            </Project>
            """);

        Assert.That(CSharpProjectDiscovery.Discover(_tempDir, null).Single().SourceFiles,
            Is.EqualTo(new[] { Path.Combine(_tempDir, "Keep2.cs") }));
    }

    [Test]
    public void Discover_DisabledDefaultCompilationIsExplicitlyEmpty()
    {
        File.WriteAllText(Path.Combine(_tempDir, "App.csproj"),
            "<Project><PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_tempDir, "Excluded.cs"), "class Example {}");

        Assert.That(CSharpProjectDiscovery.Discover(_tempDir, null).Single().SourceFiles, Is.Empty);
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
    public void Discover_XmlDeclaredEncoding_PreservesProjectMetadata()
    {
        var contents = "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>\n" +
            "<Project><PropertyGroup><AssemblyName>Caf\u00e9</AssemblyName></PropertyGroup></Project>";
        File.WriteAllBytes(Path.Combine(_tempDir, "app.csproj"), System.Text.Encoding.Latin1.GetBytes(contents));

        var projects = CSharpProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("Caf\u00e9"));
    }

    [Test]
    public void Discover_Frameworks_IncludeSingleTargetFramework()
    {
        var csproj = Path.Combine(_tempDir, "MyApp.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var projects = CSharpProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Frameworks, Is.EqualTo(new[] { "net10.0" }));
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
