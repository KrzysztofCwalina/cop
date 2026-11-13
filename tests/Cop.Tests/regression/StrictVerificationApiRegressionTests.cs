using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class StrictVerificationApiRegressionTests
{
    [Test]
    public void Issue128_StrictVerificationAcceptsParsedNamesAndDiagnosticData()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "cop.sln")))
        {
            directory = directory.Parent;
        }

        var repo = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        var exitCode = VerifyCommand.Execute(Path.Combine(repo, "packages", "core", "code"),
            new VerificationOptions { IsStrictStructureEnabled = true });
        Assert.That(exitCode, Is.Zero);
    }
}
