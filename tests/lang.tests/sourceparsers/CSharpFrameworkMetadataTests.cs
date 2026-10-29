using Cop.Providers;
using Cop.Providers.SourceParsers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class CSharpFrameworkMetadataTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void AssemblyApi_ResolvesReferenceAndImplementationCoreIdentities_FromBundledContracts(bool implementationCore)
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-framework-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = CSharpSyntaxTree.ParseText("""
                using System;
                using System.Collections.Generic;
                public enum Shade { Light, Dark }
                public struct Point { public int X; }
                public class Surface : IDisposable {
                    static Surface() => throw new Exception("Target code must never execute.");
                    public void Dispose() {}
                    public Dictionary<string, int[]> Map(List<string> values, DateTime date) => null;
                }
                """);
            var provider = typeof(CSharpProvider).Assembly;
            var references = implementationCore
                ? new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) }
                : provider.GetManifestResourceNames()
                    .Where(name => name.StartsWith("Cop.CSharp.Framework.", StringComparison.Ordinal))
                    .Select(name =>
                    {
                        using var stream = provider.GetManifestResourceStream(name)!;
                        return MetadataReference.CreateFromStream(stream);
                    }).ToArray();
            var path = Path.Combine(root, "surface.dll");
            var result = CSharpCompilation.Create("surface", [source], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(path);
            Assert.That(result.Success, Is.True, string.Join("\n", result.Diagnostics));
            Assert.That(Directory.GetFiles(root).Select(Path.GetFileName), Is.EqualTo(new[] { "surface.dll" }),
                "The inspected target must not have adjacent framework assemblies.");
            var file = AssemblyApiReader.ReadAssembly(path);
            Assert.That(file.Types.Select(type => (type.Name, type.Kind.ToString())),
                Is.EqualTo(new[] { ("Shade", "Enum"), ("Point", "Struct"), ("Surface", "Class") }));
            Assert.That(file.Types[0].EnumValues, Is.EqualTo(new[] { "Light", "Dark" }));
            Assert.That(file.Types[1].Fields.Single().Type?.ToString(), Is.EqualTo("System.Int32"));
            var surface = file.Types[2];
            Assert.That(surface.BaseTypes, Is.EqualTo(new[] { "System.IDisposable" }));
            Assert.That(surface.Methods.Select(method => method.Name), Is.EqualTo(new[] { "Dispose", "Map" }));
            var map = surface.Methods[1];
            Assert.That(map.ReturnType?.ToString(),
                Is.EqualTo("System.Collections.Generic.Dictionary<System.String, System.Int32[]>"));
            Assert.That(map.Parameters.Select(parameter => parameter.Type?.ToString()),
                Is.EqualTo(new[] { "System.Collections.Generic.List<System.String>", "System.DateTime" }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
