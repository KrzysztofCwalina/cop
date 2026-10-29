using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Cop.Core;

internal static class ProviderCompatibility
{
    private static readonly System.Reflection.AssemblyName Host = typeof(DataProvider).Assembly.GetName();
    private static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.Ordinal);

    internal static string HostVersion => Host.Version!.ToString();

    internal static string? GetError(string packageDir, PackageMetadata metadata)
    {
        if (!metadata.IsClrProvider || FindAssembly(packageDir, metadata) is not { } path)
            return null;

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            if (reader.GetString(reference.Name) == Host.Name && reference.Version != Host.Version)
                return $"Provider '{metadata.Name}' at '{path}' is incompatible: it requires cop {reference.Version}, " +
                    $"but this executable is {HostVersion}. A matching package must be restored or rebuilt.";
        }
        return null;
    }

    internal static void ReportSkipped(string error)
    {
        if (Reported.TryAdd(error, 0))
            Console.Error.WriteLine($"Warning: {error} Skipping this package; looking for a compatible version.");
    }

    internal static string? FindAssembly(string packageDir, PackageMetadata metadata)
    {
        var libDir = Path.Combine(packageDir, "lib");
        if (!Directory.Exists(libDir))
            return null;
        var dlls = Directory.GetFiles(libDir, "*.dll");
        if (dlls.Length == 0)
        {
            var ridDir = Path.Combine(libDir, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
            if (Directory.Exists(ridDir))
                dlls = Directory.GetFiles(ridDir, "*.dll");
        }
        if (dlls.Length == 0)
            return null;
        if (dlls.Length == 1)
            return dlls[0];
        return dlls.FirstOrDefault(d => Path.GetFileName(d).Equals(metadata.ProviderAssembly, StringComparison.OrdinalIgnoreCase))
            ?? dlls.FirstOrDefault(d => Path.GetFileNameWithoutExtension(d).Equals(metadata.Name, StringComparison.OrdinalIgnoreCase))
            ?? dlls[0];
    }
}
