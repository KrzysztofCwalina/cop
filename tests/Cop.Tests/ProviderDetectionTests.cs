using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Regression tests for provider detection. Detection used to substring-match the literal text
/// <c>"provider"</c> in the raw cop.json, so any package that merely mentioned the word (e.g. in a
/// keyword or description) was misclassified as a provider. Detection now parses the manifest and
/// checks the actual provider field (which holds a provider <em>kind</em>: clr / node / python).
/// <see cref="Cop.Lang.Interpreter.ModuleLoader"/> delegates to <see cref="PackageMetadata.IsProvider"/>.
/// </summary>
[TestFixture]
public class ProviderDetectionTests
{
    [Test]
    public void Manifest_MentioningTheWordProvider_IsNotAProvider()
    {
        var meta = PackageMetadata.ParseFromJson(
            "{\"name\":\"notaprovider\",\"version\":\"1.0.0\",\"title\":\"Not A Provider\",\"description\":\"a provider of checks\",\"authors\":\"me\",\"keywords\":[\"provider\"]}");
        Assert.That(meta.IsProvider, Is.False,
            "a manifest that only mentions the word 'provider' must not be classified as a provider");
    }

    [TestCase("clr")]
    [TestCase("node")]
    [TestCase("python")]
    public void Manifest_DeclaringAProviderKind_IsAProvider(string kind)
    {
        var meta = PackageMetadata.ParseFromJson(
            $"{{\"name\":\"realprovider\",\"version\":\"1.0.0\",\"title\":\"Real\",\"description\":\"x\",\"authors\":\"me\",\"provider\":\"{kind}\"}}");
        Assert.That(meta.IsProvider, Is.True);
    }

    [Test]
    public void Manifest_WithNoProviderField_IsNotAProvider()
    {
        var meta = PackageMetadata.ParseFromJson(
            "{\"name\":\"checks\",\"version\":\"1.0.0\",\"title\":\"Checks\",\"description\":\"x\",\"authors\":\"me\"}");
        Assert.That(meta.IsProvider, Is.False);
    }
}
