// Persists demanded source models using parser identity and source-content fingerprints.

using System.Security.Cryptography;
using System.Text;
using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

internal sealed class DeferredSourceCache(string scope)
{
    private const int Version = 2;
    private readonly string _path = CodeCollectionBuilder.GetCachePath(scope + "\ndeferred-bundle");
    private Dictionary<string, Entry>? _entries;
    private readonly object _gate = new();
    private bool _changed;
    private sealed record Entry(string Parser, byte[] Fingerprint, byte[] Model);

    /// <summary>
    /// Restores a cached source model or parses the exact current source text.
    /// </summary>
    public async ValueTask<SourceFile?> ReadAsync(
        string path, ISourceParser parser, CancellationToken cancellationToken = default)
    {
        string parserId = parser.GetCacheIdentity(path);
        bool canCache = parser.CanCache(path);
        using FileStream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new StreamReader(stream);
        string source = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        byte[] fingerprint = canCache
            ? SHA256.HashData(Encoding.UTF8.GetBytes(source))
            : [];
        Dictionary<string, Entry>? entries;
        Entry? entry;
        lock (_gate)
        {
            entries = canCache ? _entries ??= Load() : null;
            entry = entries?.GetValueOrDefault(path);
        }
        if (entry is not null && entry.Parser == parserId
            && entry.Fingerprint.AsSpan().SequenceEqual(fingerprint))
        {
            using MemoryStream buffer = new MemoryStream(entry.Model, writable: false);
            List<SourceFile>? cached = SourceCacheSerializer.LoadOrDefault(
                buffer, fingerprint, parser.GetType().Assembly);
            if (cached is { Count: 1 })
            {
                if (CopDiagnostics.IsTimingEnabled)
                {
                    Console.Error.WriteLine($"[provider-cache] {cached[0].Language} {path}");
                }

                return cached[0];
            }
            Console.Error.WriteLine($"Warning: Could not reconstruct cached source model for '{path}'.");
        }

        SourceFile? parsed = await parser.ParseAsync(path, source, cancellationToken)
            .ConfigureAwait(false);
        if (CopDiagnostics.IsTimingEnabled)
        {
            Console.Error.WriteLine($"[provider-parse] {parser.Language} {path}");
        }

        lock (_gate)
        {
            if (entries is not null)
            {
                entries.Remove(path);
                _changed = true;
                if (parsed is not null && parsed.ParseErrors.Count == 0)
                {
                    using MemoryStream buffer = new MemoryStream();
                    SourceCacheSerializer.Save(buffer, fingerprint, [parsed]);
                    entries[path] = new(parserId, fingerprint, buffer.ToArray());
                }
            }
        }
        return parsed;
    }

    private Dictionary<string, Entry> Load()
    {
        Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!File.Exists(_path))
        {
            return entries;
        }

        try
        {
            using FileStream stream = File.OpenRead(_path);
            using BinaryReader reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadInt32() != Version)
            {
                return entries;
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > 1_000_000)
            {
                throw new InvalidDataException("Invalid deferred-cache entry count.");
            }

            for (int i = 0; i < count; i++)
            {
                string path = reader.ReadString();
                string parser = reader.ReadString();
                byte[] fingerprint = ReadBytes(reader);
                byte[] model = ReadBytes(reader);
                if (!entries.TryAdd(path, new(parser, fingerprint, model)))
                {
                    throw new InvalidDataException("Duplicate deferred-cache source path.");
                }
            }
            return entries;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: Could not read deferred source cache '{_path}': {ex.Message}");
            return new(StringComparer.Ordinal);
        }
    }

    private static byte[] ReadBytes(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("Invalid deferred-cache payload length.");
        }

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException("Incomplete deferred-cache payload.");
        }

        return bytes;
    }

    /// <summary>
    /// Persists changed source-cache entries for later executions.
    /// </summary>
    public void Save(IReadOnlyList<string> paths)
    {
        lock (_gate)
        {
            SaveEntries(paths);
        }
    }

    private void SaveEntries(IReadOnlyList<string> paths)
    {
        if (!_changed || _entries is null)
        {
            return;
        }

        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (FileStream stream = File.Create(temporary))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Version);
                writer.Write(paths.Count(path => _entries.ContainsKey(path)));
                foreach (string path in paths)
                {
                    if (!_entries.TryGetValue(path, out Entry? entry))
                    {
                        continue;
                    }

                    writer.Write(path);
                    writer.Write(entry.Parser);
                    writer.Write(entry.Fingerprint.Length);
                    writer.Write(entry.Fingerprint);
                    writer.Write(entry.Model.Length);
                    writer.Write(entry.Model);
                }
            }
            File.Move(temporary, _path, overwrite: true);
            _changed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: Could not write deferred source cache '{_path}': {ex.Message}");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Warning: Could not remove temporary source cache '{temporary}': {ex.Message}");
            }
        }
    }
}
