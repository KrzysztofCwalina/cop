using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class RustBundlePublishTests
{
    private string _root = null!;
    private string _native = null!;
    private string _script = null!;
    private string _archive = null!;
    private string _bundle = null!;

    [SetUp]
    public void SetUp()
    {
        var repo = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "cop.sln")))
            repo = repo.Parent;
        Assert.That(repo, Is.Not.Null);
        _root = Path.Combine(Path.GetTempPath(), $"cop rust bundle {Guid.NewGuid():N}");
        var install = Path.Combine(_root, "install");
        _native = Path.Combine(_root, "providers", "rust-provider", "native");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(_native);
        _script = Path.Combine(install, "prepare-rust-bundle.ps1");
        File.Copy(Path.Combine(repo!.FullName, "install", "prepare-rust-bundle.ps1"), _script);
        File.Copy(Path.Combine(repo.FullName, "install", "publish-rust-bundle.ps1"),
            Path.Combine(install, "publish-rust-bundle.ps1"));
        _archive = Path.Combine(_root, "source.tar.xz");
        File.WriteAllBytes(_archive, [1, 2, 3]);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_archive)));
        File.WriteAllText(Path.Combine(_native, "stdlib.json"), JsonSerializer.Serialize(new
        {
            release = "fixture",
            archiveUrl = "https://example.invalid/rust-src-fixture.tar.xz",
            archiveSha256 = hash
        }));
        File.WriteAllText(Path.Combine(_native, "prepare-stdlib.ps1"), """
            param([string]$ArchivePath, [string]$OutputDirectory)
            $stdlib = Join-Path $OutputDirectory 'rust-stdlib'
            New-Item -ItemType Directory -Force (Join-Path $stdlib 'library') | Out-Null
            Set-Content -LiteralPath (Join-Path $stdlib 'manifest.json') -Value '{}'
            Set-Content -LiteralPath (Join-Path $stdlib 'library\lib.rs') -Value '#![no_std]'
            """);
        File.WriteAllText(Path.Combine(_native, "build.ps1"), """
            param([string]$Runtime, [string]$OutputDirectory, [switch]$Offline)
            Set-Content -LiteralPath (Join-Path $PSScriptRoot 'build-invoked') -Value $Runtime
            Set-Content -LiteralPath (Join-Path $OutputDirectory 'cop-rust-frontend.exe') -Value 'fixture'
            $licenses = Join-Path $OutputDirectory 'rust-frontend-licenses'
            New-Item -ItemType Directory -Force $licenses | Out-Null
            [ordered]@{ runtime = $Runtime; offline = [bool]$Offline } | ConvertTo-Json |
                Set-Content -LiteralPath (Join-Path $licenses 'manifest.json')
            """);
        _bundle = Path.Combine(install, "native-bundles", "win-x64", "bundle.zip");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void OptionalDistribution_ContainsOnlyExplicitlyInstallableArchive()
    {
        var script = Path.Combine(Path.GetDirectoryName(_script)!, "publish-rust-bundle.ps1");
        var result = RunScript(script, "-RustSourceArchive", _archive, "-Offline");
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        var distribution = Path.Combine(Path.GetDirectoryName(_script)!, "cop-rust-win-x64.zip");
        Assert.That(result.Output.Trim(), Is.EqualTo(distribution));
        using var zip = ZipFile.OpenRead(distribution);
        Assert.That(zip.Entries.Select(e => e.FullName), Is.EqualTo(new[] { "Cop.RustFrontend.Bundle.zip" }));
        using var input = zip.Entries.Single().Open();
        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        Assert.That(bytes.ToArray(), Is.EqualTo(File.ReadAllBytes(_bundle)));
        Assert.That(Directory.Exists(Path.Combine(Path.GetDirectoryName(_script)!, "win-x64")), Is.False,
            "Building the language bundle must not install it or alter the base distribution.");
    }

    [Test]
    public void LocalVerifiedInputs_ProduceRootPayload_AndPreserveOfflineBuildOption()
    {
        var result = Run("-RustSourceArchive", _archive, "-Offline");
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        Assert.That(result.Output.Trim(), Is.EqualTo(_bundle));
        using var zip = ZipFile.OpenRead(_bundle);
        Assert.That(zip.Entries.Select(e => e.FullName.Replace('\\', '/')).Order(),
            Is.EqualTo(new[]
            {
                "cop-rust-frontend.exe",
                "rust-frontend-licenses/manifest.json",
                "rust-stdlib/library/lib.rs",
                "rust-stdlib/manifest.json"
            }));
        using var manifest = zip.GetEntry("rust-frontend-licenses/manifest.json")!.Open();
        using var json = JsonDocument.Parse(manifest);
        Assert.That(json.RootElement.GetProperty("runtime").GetString(), Is.EqualTo("win-x64"));
        Assert.That(json.RootElement.GetProperty("offline").GetBoolean(), Is.True);
        Assert.That(Directory.GetDirectories(Path.GetDirectoryName(_bundle)!), Is.Empty);
    }

    [Test]
    public void RepeatedIdenticalPayloads_ProduceIdenticalContentAddressedBundle()
    {
        var result = Run("-RustSourceArchive", _archive, "-Offline");
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        var first = File.ReadAllBytes(_bundle);
        result = Run("-RustSourceArchive", _archive, "-Offline");
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        Assert.That(File.ReadAllBytes(_bundle), Is.EqualTo(first));
    }

    [Test]
    public void WrongSourceChecksum_FailsBeforeNativeBuildOrPublication()
    {
        File.WriteAllBytes(_archive, [4, 5, 6]);
        var result = Run("-RustSourceArchive", _archive, "-Offline");
        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Output, Does.Contain("refusing incompatible Rust sources"));
        Assert.That(File.Exists(Path.Combine(_native, "build-invoked")), Is.False);
        Assert.That(File.Exists(_bundle), Is.False);
    }

    [Test]
    public void OfflineWithoutCachedArchive_FailsWithoutStartingNativeBuild()
    {
        var result = Run("-Offline");
        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Output, Does.Contain("Offline Rust publishing requires"));
        Assert.That(File.Exists(Path.Combine(_native, "build-invoked")), Is.False);
        Assert.That(File.Exists(_bundle), Is.False);
    }

    [TestCase("throw 'fixture native failure'", "fixture native failure")]
    [TestCase("param($Runtime, $OutputDirectory, [switch]$Offline)", "missing required payload")]
    public void FailedRebuild_DoesNotReplaceExistingBundleOrLeaveStaging(string build, string diagnostic)
    {
        var initial = Run("-RustSourceArchive", _archive, "-Offline");
        Assert.That(initial.ExitCode, Is.Zero, initial.Output);
        var original = File.ReadAllBytes(_bundle);
        File.WriteAllText(Path.Combine(_native, "build.ps1"), build);
        var result = Run("-RustSourceArchive", _archive, "-Offline");
        Assert.That(result.ExitCode, Is.Not.Zero);
        Assert.That(result.Output, Does.Contain(diagnostic));
        Assert.That(File.ReadAllBytes(_bundle), Is.EqualTo(original));
        Assert.That(Directory.GetDirectories(Path.GetDirectoryName(_bundle)!), Is.Empty);
    }

    private (int ExitCode, string Output) Run(params string[] arguments)
        => RunScript(_script, arguments);

    private static (int ExitCode, string Output) RunScript(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Runtime", "win-x64" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Rust bundle preparation fixture timed out.");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
