using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace Cop.Providers.SourceParsers;

internal sealed record RustFrontendDeployment(string Executable, Func<string>? PrepareStandardLibrary = null);

internal static class RustFrontendBundle
{
    internal const string ResourceName = "Cop.RustFrontend.Bundle.zip";
    private const int MaximumArchiveBytes = 128 * 1024 * 1024;
    private const long MaximumExtractedBytes = 256L * 1024 * 1024;
    private static string ExecutableName => OperatingSystem.IsWindows() ? "cop-rust-frontend.exe" : "cop-rust-frontend";
    private static IEnumerable<string> Directories =>
        new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(RustFrontendBundle).Assembly.Location)! }.Distinct();

    internal static bool IsAvailable => Assembly.GetEntryAssembly()?.GetManifestResourceInfo(ResourceName) is not null ||
        Directories.Any(d => File.Exists(Path.Combine(d, ExecutableName)) || File.Exists(Path.Combine(d, ResourceName)));

    internal static RustFrontendDeployment? Find(Func<Stream?>? bundleFactory = null, string? cacheDirectory = null)
    {
        if (bundleFactory is not null)
        {
            using var supplied = bundleFactory();
            return supplied is null ? null : Deploy(supplied, cacheDirectory);
        }
        using var embedded = Assembly.GetEntryAssembly()?.GetManifestResourceStream(ResourceName);
        if (embedded is not null) return Deploy(embedded, cacheDirectory);
        foreach (string directory in Directories)
        {
            string executable = Path.Combine(directory, ExecutableName);
            if (File.Exists(executable)) return new(executable);
            string archive = Path.Combine(directory, ResourceName);
            if (!File.Exists(archive)) continue;
            using var stream = File.OpenRead(archive);
            return Deploy(stream, cacheDirectory);
        }
        return null;
    }

    private static RustFrontendDeployment Deploy(Stream source, string? cacheDirectory)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = source.Read(buffer)) != 0)
        {
            if (bytes.Length + count > MaximumArchiveBytes)
                throw new InvalidDataException("Rust frontend bundle exceeds 128 MiB.");
            bytes.Write(buffer.AsSpan(0, count));
        }
        byte[] content = bytes.ToArray();
        string identity = Convert.ToHexString(SHA256.HashData(content));
        string executable = Extract(content, identity, cacheDirectory, includeStandardLibrary: false);
        var standardLibrary = new Lazy<string>(() => Path.Combine(
            Path.GetDirectoryName(Extract(content, identity, cacheDirectory, includeStandardLibrary: true))!, "rust-stdlib"));
        return new(executable, () => standardLibrary.Value);
    }

    private static string Extract(byte[] content, string identity, string? cacheDirectory, bool includeStandardLibrary)
    {
        using var bytes = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        if (archive.Entries.Count > 15000) throw new InvalidDataException("Rust frontend bundle exceeds 15000 entries.");
        long length = 0;
        var files = new Dictionary<string, ZipArchiveEntry>(OperatingSystem.IsWindows() ?
            StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName;
            if (name.Contains('\\') || name.Contains(':') || name.StartsWith('/') ||
                name.TrimEnd('/').Split('/').Any(s => s.Length == 0 || s is "." or ".." ||
                    s.EndsWith('.') || s.EndsWith(' ') || s.Any(char.IsControl)) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException($"Unsafe Rust frontend bundle entry: {name}");
            if (name.EndsWith('/')) continue;
            if (name.Length == 0 || entry.Length > 64L * 1024 * 1024 ||
                (length += entry.Length) > MaximumExtractedBytes || !files.TryAdd(name, entry))
                throw new InvalidDataException("Rust frontend bundle contains duplicate/oversized entries.");
        }
        if (!files.ContainsKey(ExecutableName) || !files.ContainsKey("rust-stdlib/manifest.json") ||
            !files.ContainsKey("rust-stdlib/library/core/src/lib.rs") ||
            !files.ContainsKey("rust-frontend-licenses/manifest.json"))
            throw new InvalidDataException("Rust frontend bundle is missing its helper, sources, or attribution.");
        if (!includeStandardLibrary)
            files = files.Where(file => !file.Key.StartsWith("rust-stdlib/", StringComparison.Ordinal))
                .ToDictionary(file => file.Key, file => file.Value, files.Comparer);
        string cache = Path.GetFullPath(cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cop", "rust-frontends"));
        Directory.CreateDirectory(cache);
        string destination = Path.Combine(cache, identity + (includeStandardLibrary ? "" : "-syntax"));
        string staging = Path.Combine(cache, ".extracting-" + Guid.NewGuid().ToString("N"));
        if (!Directory.Exists(destination))
        {
            Directory.CreateDirectory(staging);
            try
            {
                byte[] buffer = new byte[81920];
                foreach (var (name, entry) in files)
                {
                    string path = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var input = entry.Open();
                    using var output = new FileStream(path, FileMode.CreateNew);
                    long written = 0;
                    int count;
                    while ((count = input.Read(buffer)) != 0)
                    {
                        if ((written += count) > entry.Length)
                            throw new InvalidDataException("Rust frontend bundle entry exceeds its declared size.");
                        output.Write(buffer.AsSpan(0, count));
                    }
                    if (written != entry.Length) throw new InvalidDataException("Truncated Rust frontend bundle entry.");
                }
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(Path.Combine(staging, ExecutableName),
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                try { Directory.Move(staging, destination); }
                catch (IOException) when (Directory.Exists(destination)) { }
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        foreach (var (name, entry) in files)
        {
            string path = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
            for (string? parent = path; parent is not null && parent.StartsWith(destination, StringComparison.Ordinal); parent = Path.GetDirectoryName(parent))
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Rust frontend cache contains a symbolic link/reparse point.");
            if (new FileInfo(path).Length != entry.Length)
                throw new InvalidDataException("Rust frontend cache entry has changed.");
            using var installed = File.OpenRead(path);
            using var original = entry.Open();
            if (!SHA256.HashData(installed).AsSpan().SequenceEqual(SHA256.HashData(original)))
                throw new InvalidDataException("Rust frontend cache entry failed integrity validation.");
        }
        return Path.Combine(destination, ExecutableName);
    }
}
