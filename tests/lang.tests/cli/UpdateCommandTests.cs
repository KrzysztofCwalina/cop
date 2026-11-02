using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests.Lang.Cli;

[TestFixture]
public class UpdateCommandTests
{
    private string _home = null!;

    [SetUp]
    public void SetUp()
    {
        _home = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cop-install", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_home, recursive: true);

    [Test]
    public void InstallationAlwaysUsesCopHomeAndReplacesOnlyTheExecutable()
    {
        var source = Path.Combine(_home, "published-cop");
        File.WriteAllText(source, "new Cop");
        var canonical = CopInstallation.DirectoryPath(_home);
        Assert.That(canonical, Is.EqualTo(Path.Combine(_home, ".cop")));
        Directory.CreateDirectory(canonical);
        var target = Path.Combine(canonical, OperatingSystem.IsWindows() ? "cop.exe" : "cop");
        File.WriteAllText(target, "old Cop");
        var config = Path.Combine(canonical, "ai.json");
        File.WriteAllText(config, "keep configuration");

        Assert.That(CopInstallation.InstallExecutable(source, _home), Is.EqualTo(target));
        Assert.That(File.ReadAllText(target), Is.EqualTo("new Cop"));
        Assert.That(File.ReadAllText(config), Is.EqualTo("keep configuration"));
        Assert.That(File.Exists(target + ".old"), Is.False);
        Assert.That(CopInstallation.InstallExecutable(target, _home), Is.EqualTo(target));
        Assert.That(File.ReadAllText(target), Is.EqualTo("new Cop"));
    }

    [Test]
    [Platform("Win")]
    public void FailedReplacementPreservesTheExistingInstallation()
    {
        var source = Path.Combine(_home, "published-cop");
        File.WriteAllText(source, "new Cop");
        var canonical = CopInstallation.DirectoryPath(_home);
        Directory.CreateDirectory(canonical);
        var target = Path.Combine(canonical, "cop.exe");
        File.WriteAllText(target, "old Cop");
        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => CopInstallation.InstallExecutable(source, _home));

        Assert.That(File.ReadAllText(target), Is.EqualTo("old Cop"));
        Assert.That(File.Exists(target + ".new"), Is.False);
        Assert.That(File.Exists(target + ".old"), Is.False);
    }

    [Test]
    [Platform("Win")]
    public void InstallationRemovesLegacyCopButPreservesOtherDotnetTools()
    {
        var tools = Path.Combine(_home, ".dotnet", "tools");
        Directory.CreateDirectory(tools);
        var legacy = Path.Combine(tools, "cop.exe");
        File.WriteAllText(legacy, "new Cop");
        var other = Path.Combine(tools, "dotnet-trace.exe");
        File.WriteAllText(other, "keep tool");
        File.WriteAllText(Path.Combine(tools, "Cop.RustFrontend.Bundle.zip"), "optional bundle");

        var target = CopInstallation.InstallExecutable(legacy, _home);

        Assert.That(target, Is.EqualTo(Path.Combine(_home, ".cop", "cop.exe")));
        Assert.That(File.ReadAllText(target), Is.EqualTo("new Cop"));
        Assert.That(File.Exists(legacy), Is.False);
        Assert.That(File.Exists(legacy + ".old"), Is.False);
        Assert.That(File.ReadAllText(other), Is.EqualTo("keep tool"));
        Assert.That(File.ReadAllText(Path.Combine(_home, ".cop", "Cop.RustFrontend.Bundle.zip")),
            Is.EqualTo("optional bundle"));
    }

    [Test]
    [Platform("Win")]
    public void InstallationCanMigrateARunningLegacyExecutable()
    {
        var tools = Path.Combine(_home, ".dotnet", "tools");
        Directory.CreateDirectory(tools);
        var legacy = Path.Combine(tools, "cop.exe");
        File.WriteAllText(legacy, "new Cop");
        using (File.Open(legacy, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var target = CopInstallation.InstallExecutable(legacy, _home);
            Assert.That(File.ReadAllText(target), Is.EqualTo("new Cop"));
            Assert.That(File.Exists(legacy), Is.False);
        }
    }

    [Test]
    [Platform("Win")]
    public void DownloadedUpdatePreservesAnExplicitLegacyRustBundle()
    {
        var tools = Path.Combine(_home, ".dotnet", "tools");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "cop.exe"), "old Cop");
        File.WriteAllText(Path.Combine(tools, "Cop.RustFrontend.Bundle.zip"), "legacy bundle");
        var download = Path.Combine(_home, "downloaded-cop");
        File.WriteAllText(download, "new Cop");

        var target = CopInstallation.InstallExecutable(download, _home);

        Assert.That(File.ReadAllText(target), Is.EqualTo("new Cop"));
        Assert.That(File.ReadAllText(Path.Combine(_home, ".cop", "Cop.RustFrontend.Bundle.zip")),
            Is.EqualTo("legacy bundle"));
        File.WriteAllText(Path.Combine(tools, "Cop.RustFrontend.Bundle.zip"), "other bundle");
        CopInstallation.InstallExecutable(download, _home);
        Assert.That(File.ReadAllText(Path.Combine(_home, ".cop", "Cop.RustFrontend.Bundle.zip")),
            Is.EqualTo("legacy bundle"));
    }

    [Test]
    [Platform("Win")]
    public void WindowsPathUsesOneCanonicalEntryWithoutRemovingOtherTools()
    {
        var build = Path.Combine(_home, "repo", "install", "win-x64");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "cop.exe"), "build output");
        var tools = Path.Combine(_home, ".dotnet", "tools");
        var canonical = Path.Combine(_home, ".cop");
        var path = string.Join(';', tools, build, canonical.ToUpperInvariant() + "\\", "C:\\Windows", canonical);
        var expected = string.Join(';', canonical, tools, "C:\\Windows");

        Assert.That(CopInstallation.WindowsPath(path, _home), Is.EqualTo(expected));
        Assert.That(CopInstallation.WindowsPath(expected, _home), Is.EqualTo(expected));
        Assert.That(CopInstallation.WindowsPath(null, _home), Is.EqualTo(canonical));
        Assert.That(File.ReadAllText(Path.Combine(build, "cop.exe")), Is.EqualTo("build output"));
    }

    [Test]
    [Platform("Win")]
    public void WindowsPathPreservesNonCopBuildDirectories()
    {
        var unrelated = Path.Combine(_home, "other", "install", "win-arm64");
        Directory.CreateDirectory(unrelated);
        Assert.That(CopInstallation.WindowsPath(unrelated, _home),
            Is.EqualTo(string.Join(';', Path.Combine(_home, ".cop"), unrelated)));
    }

    [Test]
    public void IsUpToDate_TrueWhenCurrentEqualsLatest()
        => Assert.That(UpdateCommand.IsUpToDate(new Version("2026.6.24.1"), "v2026.6.24.1"), Is.True);

    [Test]
    public void IsUpToDate_TrueWhenCurrentNewerThanLatest()
        => Assert.That(UpdateCommand.IsUpToDate(new Version("2026.6.25.1"), "v2026.6.24.1"), Is.True);

    [Test]
    public void IsUpToDate_FalseWhenCurrentOlder()
        => Assert.That(UpdateCommand.IsUpToDate(new Version("2026.6.23.2"), "v2026.6.24.1"), Is.False);

    [Test]
    public void IsUpToDate_FalseWhenVersionsUnknown()
    {
        Assert.That(UpdateCommand.IsUpToDate(null, "v2026.6.24.1"), Is.False, "unknown current version → install");
        Assert.That(UpdateCommand.IsUpToDate(new Version("2026.6.24.1"), null), Is.False, "unknown latest tag → install");
        Assert.That(UpdateCommand.IsUpToDate(new Version("2026.6.24.1"), "not-a-version"), Is.False, "unparseable tag → install");
    }
}
