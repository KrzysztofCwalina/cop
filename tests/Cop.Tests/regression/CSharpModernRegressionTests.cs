using System.Diagnostics;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class CSharpModernRegressionTests
{
    [Test]
    public void Issue095_CSharp14DeclarationsRunWithoutInstalledDotnet()
    {
        RunStandalone("""
            #:sdk Microsoft.NET.Sdk.Web
            #:package Azure.FoundryApplications@0.8.7-preview
            System.Console.WriteLine("ready");
            public static class Extensions {
                extension(System.Console) {
                    public static string ReadValue() => "value";
                }
                extension(string value) {
                    public string Clean() => value.Trim();
                    public int Size => value.Length;
                    public string Text { get => value.Trim(); }
                }
            }
            """, """
            let extensions = cb.Methods:asCSharp:isExtensionMethod
            command MAIN = print('MODERN {cb.Files.Count} {cb.Types.Count} {cb.Methods.Count} {cb.Calls.Count} {extensions.Count}')
            """, "MODERN 1 1 4 3 4");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Issue096_AssignmentOnlyCatchesAreNotEmpty_AndRealEmptyCatchesRemainVisible(bool topLevel)
    {
        var body = """
            int width = 0;
            object? cached = null;
            object?[] args = new object?[2];
            try { width = System.Console.WindowWidth; } catch { width = 80; }
            try { } catch { args[0] = cached; }
            try { } catch { args[1] = null; }
            try { } catch { cached = null; }
            try { } catch { width += 1; }
            try { } catch { width = System.Math.Abs(-80); }
            try { } catch { width++; }
            try { } catch { }
            try { } catch { throw; }
            """;
        RunStandalone(topLevel ? body : "class Example { void Run() {\n" + body + "\n} }", """
            predicate swallowsException(Statement) => Statement.Kind == catch && Statement.Children.count() == 0 && !Statement.Rethrows
            let catches = cb.Statements.Where(item.Kind == catch)
            let nonempty = catches.Where(item.Children.Count > 0)
            let nestedCalls = cb.Calls.Where(item.Parent != nic && item.Parent.Parent != nic && item.Parent.Parent.Kind == catch)
            let missingParents = cb.Statements.Where(item.Kind != declaration && item.Kind != try && item.Parent == nic)
            command MAIN = print('CATCHES {catches.Count} {nonempty.Count} {cb.Statements:swallowsException.Count} {nestedCalls.Count} {missingParents.Count}')
            """, "CATCHES 9 8 1 1 0");
    }

    private static void RunStandalone(string source, string query, string expected)
    {
        var repository = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(repository, "cop.sln")))
            repository = Path.GetDirectoryName(repository) ?? throw new AssertionException("Cannot locate repository.");
        var published = Environment.GetEnvironmentVariable("COP_TEST_PUBLISHED_EXE")
            ?? Path.Combine(repository, "install", "win-x64", "cop.exe");
        var root = Path.Combine(Path.GetTempPath(), "cop-modern-csharp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var portable = Path.Combine(root, "portable");
            Directory.CreateDirectory(portable);
            var executable = Path.Combine(portable, Path.GetFileName(published));
            File.Copy(published, executable);
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "sample.cs"), source);
            var program = Path.Combine(root, "main.cop");
            var feed = Path.Combine(repository, "packages").Replace("\\", "\\\\");
            File.WriteAllText(program, $$"""
                feed '{{feed}}'
                import code
                import csharp
                let cb = codebase(csharp.parse())
                {{query}}
                """);
            for (int pass = 0; pass < 3; pass++)
            {
                var start = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var argument in pass == 0
                    ? new[] { "verify", program, "--strict-structure" }
                    : new[] { program, "-t", target, "--no-color" })
                    start.ArgumentList.Add(argument);
                start.Environment["PATH"] = "";
                start.Environment["DOTNET_ROOT"] = Path.Combine(root, "no-installed-dotnet");
                start.Environment["DOTNET_ROOT_X64"] = start.Environment["DOTNET_ROOT"];
                start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60_000))
                {
                    process.Kill(entireProcessTree: true);
                    Assert.Fail("Standalone C# regression timed out.");
                }
                var output = stdout.GetAwaiter().GetResult().Trim();
                var errors = stderr.GetAwaiter().GetResult().Trim();
                TestContext.WriteLine($"pass={pass}, exit={process.ExitCode}\n{output}\n{errors}");
                Assert.That(process.ExitCode, Is.Zero, errors);
                if (pass > 0) Assert.That(output, Is.EqualTo(expected), errors);
                Assert.That(errors, Is.Empty);
            }
            Assert.That(Directory.GetFiles(portable).Select(Path.GetFileName),
                Is.EqualTo(new[] { Path.GetFileName(published) }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
