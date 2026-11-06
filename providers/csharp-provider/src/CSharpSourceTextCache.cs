// Caches C# source text while validating current file contents.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Cop.Providers;

internal sealed class CSharpSourceTextCache
{
    private const int Version = 2;
    private readonly string _path;
    private readonly Lazy<Dictionary<string, Entry>> _entries;
    private bool _changed;
    private sealed record Entry(long Length, long Modified, long Changed, string Text);

    /// <summary>
    /// Selects the source-text cache associated with an analysis scope.
    /// </summary>
    public CSharpSourceTextCache(string scope)
    {
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cop", "cache", $"csharp-text-{key}.bin");
        _entries = new(Load);
    }

    /// <summary>
    /// Reads source text while validating cached content against the current file.
    /// </summary>
    public async ValueTask<string> ReadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        FileInfo info = new FileInfo(path);
        long length = info.Length;
        long modified = info.LastWriteTimeUtc.Ticks;
        long changed = OperatingSystem.IsWindows() ? SourceFileMetadata.ReadChangeTime(path) : 0;
        using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new StreamReader(stream);
        string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        lock (_entries.Value)
        {
        // Even native change times can collide or be restored; metadata is not content identity.
        if (changed != 0 && _entries.Value.TryGetValue(path, out Entry? cached)
            && cached.Length == length && cached.Modified == modified && cached.Changed == changed
            && string.Equals(cached.Text, text, StringComparison.Ordinal))
        {
            return cached.Text;
        }

        _entries.Value[path] = new(length, modified, changed, text);
        _changed = true;
        return text;
        }
    }

    private Dictionary<string, Entry> Load()
    {
        Dictionary<string, Entry> result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!File.Exists(_path))
        {
            return result;
        }

        try
        {
            using FileStream stream = File.OpenRead(_path);
            using GZipStream gzip = new GZipStream(stream, CompressionMode.Decompress);
            using BinaryReader reader = new BinaryReader(gzip, Encoding.UTF8);
            if (reader.ReadInt32() != Version)
            {
                return result;
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > 1_000_000)
            {
                throw new InvalidDataException("Invalid source-text cache entry count.");
            }

            for (int i = 0; i < count; i++)
            {
                if (!result.TryAdd(reader.ReadString(),
                    new(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadString())))
                {
                    throw new InvalidDataException("Duplicate source-text cache entry.");
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: Could not read source-text cache '{_path}': {ex.Message}");
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Persists source-text entries changed during this execution.
    /// </summary>
    public void Save(IReadOnlyList<string> paths)
    {
        if (!_changed || !OperatingSystem.IsWindows())
        {
            return;
        }

        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (FileStream stream = File.Create(temporary))
            using (GZipStream gzip = new GZipStream(stream, CompressionLevel.Fastest))
            using (BinaryWriter writer = new BinaryWriter(gzip, Encoding.UTF8))
            {
                writer.Write(Version);
                writer.Write(paths.Count);
                foreach (string path in paths)
                {
                    Entry entry = _entries.Value[path];
                    writer.Write(path);
                    writer.Write(entry.Length);
                    writer.Write(entry.Modified);
                    writer.Write(entry.Changed);
                    writer.Write(entry.Text);
                }

            }
            File.Move(temporary, _path, overwrite: true);
            _changed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: Could not write source-text cache '{_path}': {ex.Message}");
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Warning: Could not remove temporary source-text cache '{temporary}': {ex.Message}");
                }
            }
        }
    }
}
