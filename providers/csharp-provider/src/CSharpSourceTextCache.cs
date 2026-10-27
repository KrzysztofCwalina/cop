using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cop.Providers;

internal sealed class CSharpSourceTextCache
{
    private const int Version = 2;
    private readonly string _path;
    private readonly Lazy<Dictionary<string, Entry>> _entries;
    private bool _changed;
    private sealed record Entry(long Length, long Modified, long Changed, string Text);

    public CSharpSourceTextCache(string scope)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cop", "cache", $"csharp-text-{key}.bin");
        _entries = new(Load);
    }

    private static long ReadChangeTime(string path)
    {
        // Attribute-only access avoids opening source data, while ChangeTime detects edits
        // even when a tool restores the file's length and LastWriteTime.
        var nativePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
        using var handle = CreateFileW(nativePath, 0x80, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 0, out var information,
                (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            throw new IOException($"Could not inspect source file '{path}': {error.Message}", error);
        }
        return information.ChangeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileBasicInfo information, uint size);

    public string Read(string path)
    {
        var info = new FileInfo(path);
        var length = info.Length;
        var modified = info.LastWriteTimeUtc.Ticks;
        var changed = OperatingSystem.IsWindows() ? ReadChangeTime(path) : 0;
        if (changed != 0 && _entries.Value.TryGetValue(path, out var cached)
            && cached.Length == length && cached.Modified == modified && cached.Changed == changed)
            return cached.Text;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        _entries.Value[path] = new(length, modified, changed, text);
        _changed = true;
        return text;
    }

    private Dictionary<string, Entry> Load()
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (!File.Exists(_path)) return result;
        try
        {
            using var stream = File.OpenRead(_path);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip, Encoding.UTF8);
            if (reader.ReadInt32() != Version) return result;
            var count = reader.ReadInt32();
            if (count < 0 || count > 1_000_000)
                throw new InvalidDataException("Invalid source-text cache entry count.");
            for (int i = 0; i < count; i++)
                if (!result.TryAdd(reader.ReadString(),
                    new(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadString())))
                    throw new InvalidDataException("Duplicate source-text cache entry.");
            return result;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: Could not read source-text cache '{_path}': {ex.Message}");
            return new(StringComparer.Ordinal);
        }
    }

    public void Save(IReadOnlyList<string> paths)
    {
        if (!_changed || !OperatingSystem.IsWindows()) return;
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = File.Create(temporary))
            using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(gzip, Encoding.UTF8))
            {
                writer.Write(Version);
                writer.Write(paths.Count);
                foreach (var path in paths)
                {
                    var entry = _entries.Value[path];
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
