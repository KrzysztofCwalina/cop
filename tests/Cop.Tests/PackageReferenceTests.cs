using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Regression tests for Go-style package reference parsing. The version separator is ':' and may
/// appear with or without surrounding whitespace; earlier parsing required exactly ": " (a space),
/// so a reference written as in the type's own doc example ("host/owner/repo/pkg:1.0.0") silently
/// parsed the version into the package name.
/// </summary>
[TestFixture]
public class PackageReferenceTests
{
    [Test]
    public void Parse_NoVersion()
    {
        var r = PackageReference.Parse("github.com/org/repo/pkg");
        Assert.Multiple(() =>
        {
            Assert.That(r.Host, Is.EqualTo("github.com"));
            Assert.That(r.Owner, Is.EqualTo("org"));
            Assert.That(r.Repo, Is.EqualTo("repo"));
            Assert.That(r.PackageName, Is.EqualTo("pkg"));
            Assert.That(r.Version, Is.Null);
        });
    }

    [TestCase("github.com/org/repo/pkg:1.0.0")]   // no space (the doc-example form that used to break)
    [TestCase("github.com/org/repo/pkg: 1.0.0")]  // with space
    [TestCase("github.com/org/repo/pkg :1.0.0")]  // space before colon
    public void Parse_WithVersion_AcceptsColonWithOrWithoutSpace(string reference)
    {
        var r = PackageReference.Parse(reference);
        Assert.Multiple(() =>
        {
            Assert.That(r.PackageName, Is.EqualTo("pkg"), "package name must not absorb the version");
            Assert.That(r.Version, Is.EqualTo("1.0.0"));
        });
    }

    [Test]
    public void Parse_RoundTripsThroughToString()
    {
        var original = PackageReference.Parse("github.com/org/repo/pkg:2.3.4");
        var reparsed = PackageReference.Parse(original.ToString());
        Assert.Multiple(() =>
        {
            Assert.That(reparsed.PackageName, Is.EqualTo("pkg"));
            Assert.That(reparsed.Version, Is.EqualTo("2.3.4"));
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("too/few/segments")]
    public void Parse_Invalid_Throws(string reference)
    {
        Assert.Throws<ArgumentException>(() => PackageReference.Parse(reference));
    }
}
