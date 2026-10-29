using System.Diagnostics;
using System.IO.Compression;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class OptionalLanguagePublishTests
{
    [Test]
    public void DefaultPublish_DoesNotPrepareOrShipNativeLanguageDependencies()
    {
        var repository = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(repository, "cop.sln")))
            repository = Path.GetDirectoryName(repository) ?? throw new AssertionException("Cannot locate repository.");
        var root = Path.Combine(Path.GetTempPath(), "cop-optional-publish-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(install);
        try
        {
            File.Copy(Path.Combine(repository, "install", "publish.ps1"), Path.Combine(install, "publish.ps1"));
            File.WriteAllText(Path.Combine(install, "prepare-rust-bundle.ps1"),
                "throw 'Base publishing must not build or download a Rust frontend.'");
            File.WriteAllText(Path.Combine(install, "provider-smoke.ps1"), """
                param([string]$Executable, [switch]$RequireRustBundle)
                if ($RequireRustBundle) { throw 'Base smoke must not require a Rust frontend.' }
                """);
            var extension = Path.Combine(install, "vscode-cop");
            Directory.CreateDirectory(Path.Combine(extension, "syntaxes"));
            foreach (var name in new[] { "extension.js", "package.json", "language-configuration.json" })
                File.WriteAllText(Path.Combine(extension, name), "{}");
            var harness = Path.Combine(root, "publish-fixture.ps1");
            File.WriteAllText(harness, """
                $ErrorActionPreference = 'Stop'
                function dotnet {
                    if ($args[0] -eq 'publish') {
                        $output = $args[[Array]::IndexOf($args, '-o') + 1]
                        New-Item -ItemType Directory -Force $output | Out-Null
                        Set-Content -LiteralPath (Join-Path $output 'cop.exe') -Value 'base fixture' -NoNewline
                    }
                    $global:LASTEXITCODE = 0
                }
                & (Join-Path $PSScriptRoot 'install\publish.ps1') -Runtimes win-x64
                """);
            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", harness })
                start.ArgumentList.Add(argument);
            start.Environment["USERPROFILE"] = root;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Base publishing fixture timed out.");
            }
            Assert.That(process.ExitCode, Is.Zero, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
            using var archive = ZipFile.OpenRead(Path.Combine(install, "cop-win-x64.zip"));
            Assert.That(archive.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "cop.exe" }));
            using var reader = new StreamReader(archive.GetEntry("cop.exe")!.Open());
            Assert.That(reader.ReadToEnd(), Is.EqualTo("base fixture"));
            Assert.That(Directory.Exists(Path.Combine(install, "native-bundles")), Is.False);
            Assert.That(File.Exists(Path.Combine(install, "cop-rust-win-x64.zip")), Is.False);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
