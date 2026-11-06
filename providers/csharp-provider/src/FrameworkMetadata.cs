// Supplies bundled framework reference metadata for source and assembly analysis.

using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Cop.Providers.SourceParsers;

/// <summary>SDK-selected framework reference images, independent of the runtime's deployment layout.</summary>
internal static class FrameworkMetadata
{
    private const string Prefix = "Cop.CSharp.Framework.";
    private static readonly Lazy<ImmutableDictionary<string, ImmutableArray<byte>>> s_images = new(ReadImages);
    private static readonly Lazy<ImmutableArray<PortableExecutableReference>> s_references = new(() =>
        s_images.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => MetadataReference.CreateFromImage(pair.Value, filePath: pair.Key + ".dll"))
            .ToImmutableArray());

    internal static ImmutableArray<PortableExecutableReference> CompilationReferences => s_references.Value;

    internal static MetadataAssemblyResolver CreateAssemblyResolver(string assemblyPath) =>
        new FrameworkAssemblyResolver(assemblyPath);

    private static ImmutableDictionary<string, ImmutableArray<byte>> ReadImages()
    {
        Assembly assembly = typeof(FrameworkMetadata).Assembly;
        ImmutableDictionary<string, ImmutableArray<byte>>.Builder images = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.OrdinalIgnoreCase);
        foreach (string? name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using Stream stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Bundled C# framework metadata '{name}' is missing.");
            using MemoryStream buffer = new MemoryStream();
            stream.CopyTo(buffer);
            images.Add(Path.GetFileNameWithoutExtension(name[Prefix.Length..]), ImmutableArray.CreateRange(buffer.ToArray()));
        }
        if (!images.ContainsKey("System.Runtime") || !images.ContainsKey("mscorlib"))
        {
            throw new InvalidOperationException("Bundled C# framework reference metadata is incomplete; rebuild the C# provider with the .NET SDK.");
        }

        return images.ToImmutable();
    }

    private sealed class FrameworkAssemblyResolver(string assemblyPath) : MetadataAssemblyResolver
    {
        private readonly PathAssemblyResolver _local = new(
            Directory.EnumerateFiles(Path.GetDirectoryName(assemblyPath)!, "*.dll"));

        /// <inheritdoc />
        public override Assembly? Resolve(MetadataLoadContext context, AssemblyName assemblyName)
        {
            // Framework references must come from one coherent pack, never implementation DLLs
            // incidentally adjacent to the inspected library.
            if (assemblyName.Name is { } name && s_images.Value.TryGetValue(name, out ImmutableArray<byte> image))
            {
                return context.LoadFromByteArray(image.ToArray());
            }
            // Direct implementation-core references need the pack's forwarding facade:
            // core-library APIs are split across System.Runtime, System.Collections, etc.
            if (assemblyName.Name == "System.Private.CoreLib")
            {
                return context.LoadFromByteArray(s_images.Value["mscorlib"].ToArray());
            }

            return _local.Resolve(context, assemblyName);
        }
    }
}
