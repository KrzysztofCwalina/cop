using System.Reflection;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class CSharpFrameworkReferenceTests
{
    [Test]
    public void ExistingRuntimePackDirectoryDoesNotHideAnotherSdkInstallation()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "cop-framework-roots-" + Guid.NewGuid().ToString("N"));
        string runtimePacks = Path.Combine(root, "runtime", "packs");
        string sdkPacks = Path.Combine(root, "sdk", "packs");
        Directory.CreateDirectory(runtimePacks);
        Directory.CreateDirectory(sdkPacks);
        try
        {
            Type resolver = typeof(CSharpProvider).Assembly.GetType(
                "Cop.Providers.SourceParsers.CSharpFrameworkReferences", throwOnError: true)!;
            MethodInfo discover = resolver.GetMethod("FindPackRoots",
                BindingFlags.NonPublic | BindingFlags.Static, binder: null,
                types: [typeof(string), typeof(Func<string[]>)], modifiers: null)!;
            Func<string[]> installedSdks = () => [sdkPacks, runtimePacks, sdkPacks];
            string[] actual = (string[])discover.Invoke(null, [runtimePacks, installedSdks])!;
            Assert.That(actual, Is.EqualTo(new[] { runtimePacks, sdkPacks }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
