using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cop.Core;

/// <summary>
/// Represents a single locked file entry in .cop-lock.
/// </summary>
public class LockEntry
{
    [YamlMember(Alias = "checksum")]
    public string Checksum { get; set; } = "";

    [YamlMember(Alias = "signature")]
    public string Signature { get; set; } = "";
}

/// <summary>
/// Represents the .cop-lock file: a signed manifest of locked file checksums.
/// </summary>
public class LockFile
{
    public const string FileName = ".cop-lock";

    [YamlMember(Alias = "version")]
    public int Version { get; set; } = 1;

    [YamlMember(Alias = "files")]
    public Dictionary<string, LockEntry> Files { get; set; } = new();

    /// <summary>
    /// Loads a .cop-lock file from the given directory. Returns null if not found.
    /// </summary>
    public static LockFile? Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;

        var yaml = File.ReadAllText(path);
        return Deserialize(yaml);
    }

    /// <summary>
    /// Deserializes a LockFile from YAML text.
    /// </summary>
    public static LockFile Deserialize(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<LockFile>(yaml) ?? new LockFile();
    }

    /// <summary>
    /// Saves the lockfile to the given directory. Uses atomic write (temp + rename).
    /// </summary>
    public void Save(string directory)
    {
        var path = Path.Combine(directory, FileName);
        var yaml = Serialize();
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, yaml);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Serializes the lockfile to YAML text.
    /// </summary>
    public string Serialize()
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .Build();
        return serializer.Serialize(this);
    }

    /// <summary>
    /// Locks a file: computes its checksum and signs it with the given key.
    /// The filePath must be a repo-relative forward-slash path.
    /// </summary>
    public void Lock(string relativePath, string rootDirectory, string key)
    {
        if (relativePath == FileName)
            throw new InvalidOperationException("Cannot lock the lockfile itself.");

        var fullPath = Path.Combine(rootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {relativePath}");

        var checksum = ComputeFileChecksum(fullPath);
        var signature = Sign(checksum, key);

        Files[relativePath] = new LockEntry { Checksum = checksum, Signature = signature };
    }

    /// <summary>
    /// Unlocks a file: removes it from the lockfile.
    /// </summary>
    public bool Unlock(string relativePath)
    {
        return Files.Remove(relativePath);
    }

    /// <summary>
    /// Verifies all locked file checksums against the current disk state.
    /// Does NOT verify signatures (no key needed).
    /// Returns a list of (relativePath, status) for each locked file.
    /// </summary>
    public List<LockVerification> VerifyChecksums(string rootDirectory)
    {
        var results = new List<LockVerification>();

        foreach (var (relativePath, entry) in Files)
        {
            var fullPath = Path.Combine(rootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
            {
                results.Add(new LockVerification(relativePath, "deleted", entry.Checksum, null));
                continue;
            }

            var currentChecksum = ComputeFileChecksum(fullPath);
            var status = currentChecksum == entry.Checksum ? "clean" : "modified";
            results.Add(new LockVerification(relativePath, status, entry.Checksum, currentChecksum));
        }

        return results;
    }

    /// <summary>
    /// Verifies all signatures using the given key.
    /// Returns entries with invalid signatures.
    /// </summary>
    public List<LockVerification> VerifySignatures(string key)
    {
        var results = new List<LockVerification>();

        foreach (var (relativePath, entry) in Files)
        {
            if (!Verify(entry.Checksum, entry.Signature, key))
            {
                results.Add(new LockVerification(relativePath, "signature-invalid", entry.Checksum, null));
            }
        }

        return results;
    }

    /// <summary>
    /// Re-signs all entries with a new key. Used for key rotation or recovery.
    /// Optionally recomputes checksums from disk (pass rootDirectory).
    /// </summary>
    public void Resign(string key, string? rootDirectory = null)
    {
        foreach (var (relativePath, entry) in Files)
        {
            if (rootDirectory != null)
            {
                var fullPath = Path.Combine(rootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                    entry.Checksum = ComputeFileChecksum(fullPath);
            }
            entry.Signature = Sign(entry.Checksum, key);
        }
    }

    /// <summary>
    /// Computes SHA256 checksum of a file, prefixed with "sha256:".
    /// </summary>
    public static string ComputeFileChecksum(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Signs data with HMAC-SHA256 using the given key.
    /// </summary>
    public static string Sign(string data, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var dataBytes = Encoding.UTF8.GetBytes(data);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(dataBytes);
        return "hmac-sha256:" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Verifies an HMAC-SHA256 signature.
    /// </summary>
    public static bool Verify(string data, string signature, string key)
    {
        var expected = Sign(data, key);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }

    /// <summary>
    /// Normalizes a file path to repo-relative forward-slash format.
    /// </summary>
    public static string NormalizePath(string rootDirectory, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var rootFull = Path.GetFullPath(rootDirectory);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            rootFull += Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"File '{filePath}' is outside the project root '{rootDirectory}'.");

        return Path.GetRelativePath(rootFull, fullPath).Replace('\\', '/');
    }
}

/// <summary>
/// Result of verifying a single locked file.
/// </summary>
public record LockVerification(string RelativePath, string Status, string? ExpectedChecksum, string? ActualChecksum);
