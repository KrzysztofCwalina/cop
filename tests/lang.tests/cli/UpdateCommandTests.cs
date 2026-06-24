using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests.Lang.Cli;

[TestFixture]
public class UpdateCommandTests
{
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
