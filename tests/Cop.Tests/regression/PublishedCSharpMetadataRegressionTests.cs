using System.Diagnostics;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class PublishedCSharpMetadataRegressionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Issue089_SelfContainedHostBindsFrameworkAndLocalCalls_WithoutInstalledDotnet(bool isolatedCopy)
    {
        var repository = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(repository, "cop.sln")))
            repository = Path.GetDirectoryName(repository) ?? throw new AssertionException("Cannot locate repository.");
        var published = Environment.GetEnvironmentVariable("COP_TEST_PUBLISHED_EXE")
            ?? Path.Combine(repository, "install", "win-x64", "cop.exe");
        Assert.That(File.Exists(published), Is.True,
            "Publish the self-contained executable before running this deployment regression, or set COP_TEST_PUBLISHED_EXE.");
        var root = Path.Combine(Path.GetTempPath(), "cop-issue089-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = published;
            if (isolatedCopy)
            {
                var portable = Path.Combine(root, "portable");
                Directory.CreateDirectory(portable);
                executable = Path.Combine(portable, Path.GetFileName(published));
                File.Copy(published, executable);
                Assert.That(Directory.GetFiles(portable).Select(Path.GetFileName),
                    Is.EqualTo(new[] { Path.GetFileName(published) }));
                Assert.That(Directory.GetDirectories(portable), Is.Empty);
            }
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "sample.cs"), """
                using System;
                using System.Collections.Generic;
                using System.Linq;
                class Alpha { public int Get(int value) => value; }
                class Caller {
                    void Run() {
                        var a = new Alpha();
                        a.Get(1);
                        Math.Abs(-2);
                        " sample ".Trim();
                        new List<int> { 1, 2 }.Count();
                    }
                }
                """);
            var program = Path.Combine(root, "main.cop");
            var feed = Path.Combine(repository, "packages").Replace("\\", "\\\\");
            File.WriteAllText(program, $$"""
                feed '{{feed}}'
                import code
                import csharp
                predicate isSelected(Statement) => Statement.MemberName == 'Get' || Statement.MemberName == 'Abs' || Statement.MemberName == 'Trim' || Statement.MemberName == 'Count'
                let cb = csharp.parse()
                command MAIN = foreach cb.Calls:isSelected => '{item.MemberName}|{item.ReceiverType.Name}|{item.ValueType.Name}|{item.ReferencedCallable.Signature}'
                """);
            for (int run = 0; run < 2; run++)
            {
                var start = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var argument in new[] { program, "-t", target, "--no-color" })
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
                    Assert.Fail("Isolated C# semantic query timed out.");
                }
                var output = stdout.GetAwaiter().GetResult().Replace("\r\n", "\n").Trim();
                var errors = stderr.GetAwaiter().GetResult().Trim();
                TestContext.WriteLine($"isolated={isolatedCopy}, run={run}, exit={process.ExitCode}\n{output}\n{errors}");
                Assert.That(process.ExitCode, Is.Zero, errors);
                Assert.That(output.Split('\n'), Is.EqualTo(new[]
                {
                    "Get|Alpha|Int32|Alpha.Get(int)",
                    "Abs|null|Int32|System.Math.Abs(int)",
                    "Trim|String|String|string.Trim()",
                    "Count|List|Int32|System.Collections.Generic.IEnumerable<int>.Count<int>()"
                }));
                Assert.That(errors, Is.Empty, "Basic framework binding must not produce missing-core-type diagnostics.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
