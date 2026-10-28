using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Cop.Providers.SourceParsers;

/// <summary>SDK-selected framework reference images, independent of the runtime's deployment layout.</summary>
internal static class FrameworkMetadata
{
    private const string Prefix = "Cop.CSharp.Framework.";
    private static readonly Lazy<ImmutableDictionary<string, ImmutableArray<byte>>> Images = new(ReadImages);
    private static readonly Lazy<ImmutableArray<PortableExecutableReference>> References = new(() =>
        Images.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => MetadataReference.CreateFromImage(pair.Value, filePath: pair.Key + ".dll"))
            .ToImmutableArray());

    internal static ImmutableArray<PortableExecutableReference> CompilationReferences => References.Value;

    internal static MetadataAssemblyResolver CreateAssemblyResolver(string assemblyPath) =>
        new FrameworkAssemblyResolver(assemblyPath);

    private static ImmutableDictionary<string, ImmutableArray<byte>> ReadImages()
    {
        var assembly = typeof(FrameworkMetadata).Assembly;
        var images = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Bundled C# framework metadata '{name}' is missing.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            images.Add(Path.GetFileNameWithoutExtension(name[Prefix.Length..]), ImmutableArray.CreateRange(buffer.ToArray()));
        }
        if (!images.ContainsKey("System.Runtime") || !images.ContainsKey("mscorlib"))
            throw new InvalidOperationException("Bundled C# framework reference metadata is incomplete; rebuild the C# provider with the .NET SDK.");
        return images.ToImmutable();
    }

    private sealed class FrameworkAssemblyResolver(string assemblyPath) : MetadataAssemblyResolver
    {
        private readonly PathAssemblyResolver _local = new(
            Directory.EnumerateFiles(Path.GetDirectoryName(assemblyPath)!, "*.dll"));

        public override Assembly? Resolve(MetadataLoadContext context, AssemblyName assemblyName)
        {
            // Framework references must come from one coherent pack, never implementation DLLs
            // incidentally adjacent to the inspected library.
            if (assemblyName.Name is { } name && Images.Value.TryGetValue(name, out var image))
                return context.LoadFromByteArray(image.ToArray());
            // Direct implementation-core references need the pack's forwarding facade:
            // core-library APIs are split across System.Runtime, System.Collections, etc.
            if (assemblyName.Name == "System.Private.CoreLib")
                return context.LoadFromByteArray(Images.Value["mscorlib"].ToArray());
            return _local.Resolve(context, assemblyName);
        }
    }
}
