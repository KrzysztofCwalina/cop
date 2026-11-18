using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests.Lang.Cli;

[TestFixture]
public sealed class CheckRenamerTests
{
    [Test]
    public void Rename_ChangesCodeReferencesAndInterpolationsOnly()
    {
        const string source = """
            # old-check is described here
            let old-check = items:isBad:toError('old-check {old-check.Count} {old-check@bold} {item.old-check}')
            let total = old-check + other.old-check + pkg::old-check + old-checks + @'old-check'
            """;

        string result = CheckRenamer.Rename(source, "old-check", "new-check", out int count);

        Assert.That(result, Is.EqualTo("""
            # old-check is described here
            let new-check = items:isBad:toError('old-check {new-check.Count} {new-check@bold} {item.old-check}')
            let total = new-check + other.old-check + pkg::old-check + old-checks + @'old-check'
            """));
        Assert.That(count, Is.EqualTo(4));
        Assert.That(CheckRenamer.Rename("'{legacy@bold} {legacy}'", "legacy", "modern", out int styled),
            Is.EqualTo("'{legacy@bold} {modern}'"), "a bare word before @style is literal text");
        Assert.That(styled, Is.EqualTo(1));
    }

    [TestCase("good-name", null)]
    [TestCase("1bad", "not a valid Cop name")]
    [TestCase("trailing-", "not a valid Cop name")]
    [TestCase("let", "reserved Cop keyword")]
    public void ValidateName_RejectsInvalidNamesAndKeywords(string name, string? error)
    {
        string? message = CheckRenamer.ValidateName(name);

        if (error is null)
        {
            Assert.That(message, Is.Null);
        }
        else
        {
            Assert.That(message, Does.Contain(error));
        }
    }
}
