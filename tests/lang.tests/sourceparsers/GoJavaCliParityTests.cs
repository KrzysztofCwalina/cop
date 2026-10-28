using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class GoJavaCliParityTests
{
    [TestCase("go")]
    [TestCase("java")]
    public async Task MissingToolchain_RealCliPositiveNegativeAndWarmCacheAreExact(string language)
    {
        string root = FindRoot();
        string dotnet = ToolchainProcess.FindExecutable("dotnet") ?? throw new InvalidOperationException("dotnet host missing.");
        string configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Parent!.Name;
        string cli = Path.Combine(root, "cop", "cli", "bin", configuration, "net10.0", "cop.dll");
        string directory = Path.Combine(Path.GetTempPath(), "cop-gojava-cli-" + Guid.NewGuid().ToString("N"));
        string checks = Path.Combine(directory, "cop-checks");
        string target = Path.Combine(directory, "target");
        Directory.CreateDirectory(checks);
        Directory.CreateDirectory(target);
        string script = Path.Combine(checks, "main.cop");
        string source = Path.Combine(target, language == "go" ? "fixture.go" : "Alpha.java");
        var environment = new Dictionary<string, string?> { ["PATH"] = "" };
        try
        {
            File.WriteAllText(script, $$"""
                feed '{{Path.Combine(root, "packages").Replace('\\', '/')}}'
                import code
                import {{language}}
                let cb = {{language}}.parse()
                predicate isInner(Statement) => Statement.MemberName == 'inner'
                let violations = cb.Calls:isInner:toError('Nested call found: {item.MemberName}')
                command MAIN = CHECK(violations)
                command COUNTS = foreach [0] => 'types={cb.Types.Count};calls={cb.Calls.Count};inner={violations.Count}'
                """);
            string positive = language == "go" ? """
                package fixture
                type (
                    Alpha struct { A, B int }
                    Alias = Alpha
                )
                func (a Alpha) Run(value string) string {
                    return outer(inner("line\nvalue"))
                }
                """ : """
                class Alpha {
                    int a, b;
                    String Run(String value) {
                        return outer(inner("line\nvalue"));
                    }
                }
                """;
            File.WriteAllText(source, positive);
            var verify = await ToolchainProcess.RunAsync(dotnet, [cli, "verify", checks], root,
                timeout: TimeSpan.FromSeconds(30), environment: environment);
            Assert.That(verify.Status, Is.EqualTo(ToolchainStatus.Success), verify.StandardOutput + verify.StandardError);
            ToolchainResult? first = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var result = await ToolchainProcess.RunAsync(dotnet, [cli, script, "-t", target, "-c", "MAIN,COUNTS"], root,
                    timeout: TimeSpan.FromSeconds(30), environment: environment);
                Assert.That(result.Status, Is.EqualTo(ToolchainStatus.Failed), result.Diagnostic);
                Assert.That(result.ExitCode, Is.EqualTo(1), result.StandardOutput + result.StandardError);
                Assert.That(result.StandardOutput, Does.Contain("Nested call found: inner"));
                Assert.That(result.StandardOutput, Does.Contain($"types={(language == "go" ? 2 : 1)};calls=2;inner=1"));
                Assert.That(result.StandardOutput + result.StandardError, Does.Contain("using simpler"));
                if (first is not null) Assert.That(result.StandardOutput, Is.EqualTo(first.StandardOutput), "Cold/warm output must be identical.");
                first = result;
            }
            File.WriteAllText(source, positive.Replace("inner(\"line\\nvalue\")", "\"safe\""));
            var clean = await ToolchainProcess.RunAsync(dotnet, [cli, script, "-t", target, "-c", "MAIN,COUNTS"], root,
                timeout: TimeSpan.FromSeconds(30), environment: environment);
            Assert.That(clean.Status, Is.EqualTo(ToolchainStatus.Success), clean.StandardOutput + clean.StandardError);
            Assert.That(clean.ExitCode, Is.EqualTo(0));
            Assert.That(clean.StandardOutput, Does.Not.Contain("Nested call found:"));
            Assert.That(clean.StandardOutput, Does.Contain($"types={(language == "go" ? 2 : 1)};calls=1;inner=0"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "cop.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
