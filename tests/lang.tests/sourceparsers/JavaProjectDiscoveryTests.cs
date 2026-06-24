using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class JavaProjectDiscoveryTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-javaproject-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Test]
    public void Discover_PomXml_ExtractsArtifactIdAndDependencies()
    {
        File.WriteAllText(Path.Combine(_tempDir, "pom.xml"), """
            <project>
              <modelVersion>4.0.0</modelVersion>
              <groupId>com.example</groupId>
              <artifactId>foundation</artifactId>
              <version>1.0</version>
              <dependencies>
                <dependency>
                  <groupId>com.example</groupId>
                  <artifactId>service</artifactId>
                  <version>1.0</version>
                </dependency>
                <dependency>
                  <groupId>org.slf4j</groupId>
                  <artifactId>slf4j-api</artifactId>
                  <version>2.0.0</version>
                </dependency>
              </dependencies>
            </project>
            """);

        var projects = JavaProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("foundation"));
        Assert.That(projects[0].Language, Is.EqualTo("java"));
        Assert.That(projects[0].References, Has.Count.EqualTo(2));
        Assert.That(projects[0].References, Is.EqualTo(new[] { "com.example:service", "org.slf4j:slf4j-api" }));
    }

    [Test]
    public void Discover_BuildGradle_UsesDirectoryNameAndExtractsDependencies()
    {
        var projectDir = Path.Combine(_tempDir, "foundation");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "build.gradle"), """
            plugins {
                id 'java-library'
            }

            dependencies {
                implementation 'com.example:service:1.0'
                api "org.slf4j:slf4j-api:2.0.0"
            }
            """);

        var projects = JavaProjectDiscovery.Discover(_tempDir, null);

        Assert.That(projects, Has.Count.EqualTo(1));
        Assert.That(projects[0].Name, Is.EqualTo("foundation"));
        Assert.That(projects[0].Path, Is.EqualTo("foundation/build.gradle"));
        Assert.That(projects[0].References, Has.Count.EqualTo(2));
        Assert.That(projects[0].References, Is.EqualTo(new[] { "com.example:service", "org.slf4j:slf4j-api" }));
    }
}

