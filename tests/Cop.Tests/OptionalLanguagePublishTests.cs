using System.Diagnostics;
using System.IO.Compression;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class OptionalLanguagePublishTests
{
    [TestCase(0)]
    [TestCase(17)]
    public void DefaultPublish_DoesNotPrepareOrShipNativeLanguageDependencies(int installExitCode)
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
                        $executable = Join-Path $output 'cop.exe'
                        Set-Item -LiteralPath "Function:global:$executable" -Value {
                            $args | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'install-arguments.json')
                            $global:LASTEXITCODE = INSTALL_EXIT_CODE
                        }
                    }
                    $global:LASTEXITCODE = 0
                }
                & (Join-Path $PSScriptRoot 'install\publish.ps1') -Runtimes win-x64
                """.Replace("INSTALL_EXIT_CODE", installExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
            var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            if (System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier == "win-x64")
            {
                Assert.That(File.ReadAllText(Path.Combine(root, "install-arguments.json")).Trim(),
                    Is.EqualTo("[\"update\",\"--local\"]"));
                Assert.That(process.ExitCode, installExitCode == 0 ? Is.Zero : Is.Not.Zero, output);
                if (installExitCode != 0)
                    Assert.That(output, Does.Contain("Local Cop installation failed"));
            }
            else
            {
                Assert.That(process.ExitCode, Is.Zero, output);
                Assert.That(File.Exists(Path.Combine(root, "install-arguments.json")), Is.False,
                    "Cross-platform publishing must not install a foreign executable.");
            }
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
