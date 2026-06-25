using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests.Lang.Cli;

[TestFixture]
public class StartupNoticesTests
{
    private static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "run", "test", "syntax", "verify", "lock", "unlock", "help", "package", "repl", "init", "update", "vscode"
    };

    private static bool Show(string[] args, Func<string, bool>? resolver = null)
        => StartupNotices.ShouldShow(args, Verbs, resolver ?? (_ => false));

    [Test]
    public void BareInvocation_ShowsNotices()
        => Assert.That(Show(Array.Empty<string>()), Is.True);

    [TestCase("update")]
    [TestCase("-v")]
    [TestCase("--version")]
    [TestCase("-h")]
    [TestCase("--help")]
    [TestCase("help")]
    public void SelfDescribingCommands_SuppressNotices(string verb)
        => Assert.That(Show(new[] { verb }), Is.False);

    [TestCase("verify")]
    [TestCase("test")]
    [TestCase("init")]
    public void KnownVerbs_ShowNotices(string verb)
        => Assert.That(Show(new[] { verb }), Is.True);

    // `cop run` with no target is a usage error, so it must show ONLY that error — not the notices.
    [Test]
    public void RunWithoutTarget_SuppressesNotices()
        => Assert.That(Show(new[] { "run" }), Is.False);

    [Test]
    public void RunWithTarget_ShowsNotices()
        => Assert.That(Show(new[] { "run", "somepkg" }), Is.True);

    [Test]
    public void OptionLedInvocation_ShowsNotices()
        => Assert.That(Show(new[] { "-t", "." }), Is.True);

    [Test]
    public void RunnableBareToken_ShowsNotices()
        => Assert.That(Show(new[] { "checks.cop" }, _ => true), Is.True);

    // Regression: a misspelled/unknown command (e.g. `cop updater`) must show ONLY the
    // "unknown command" error — never the post-update "what's new" or update-available notices.
    [Test]
    public void UnknownCommand_SuppressesNotices()
        => Assert.That(Show(new[] { "updater" }, _ => false), Is.False);

    [Test]
    public void KnownVerbWins_EvenWhenTokenNotRunnable()
        => Assert.That(Show(new[] { "verify" }, _ => false), Is.True);
}
