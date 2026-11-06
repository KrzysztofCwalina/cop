// Stores signed file checksums and verifies project tamper-protection locks.

using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cop.Core;

/// <summary>
/// Represents the .cop-lock file: a signed manifest of locked file checksums.
/// </summary>
public class LockFile
{
    /// <summary>The project file containing signed content locks.</summary>
    public const string FileName = ".cop-lock";

    /// <summary>The serialized lock-manifest format version.</summary>
    [YamlMember(Alias = "version")]
    public int Version { get; set; } = 1;

    /// <summary>Locked file entries indexed by their project-relative paths.</summary>
    [YamlMember(Alias = "files")]
    public Dictionary<string, LockEntry> Files { get; set; } = new();

    /// <summary>
    /// Loads a .cop-lock file from the given directory. Returns null if not found.
    /// </summary>
    public static LockFile? Load(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        string yaml = File.ReadAllText(path);
        return Deserialize(yaml);
    }

    /// <summary>
    /// Deserializes a LockFile from YAML text.
    /// </summary>
    public static LockFile Deserialize(string yaml)
    {
        IDeserializer deserializer = new DeserializerBuilder()
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
        string path = Path.Combine(directory, FileName);
        string yaml = Serialize();
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, yaml);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Serializes the lockfile to YAML text.
    /// </summary>
    public string Serialize()
    {
        ISerializer serializer = new SerializerBuilder()
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
        {
            throw new InvalidOperationException("Cannot lock the lockfile itself.");
        }

        string fullPath = Path.Combine(rootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {relativePath}");
        }

        string checksum = ComputeFileChecksum(fullPath);
        string signature = Sign(checksum, key);

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
        List<LockVerification> results = new List<LockVerification>();

        foreach ((string? relativePath, LockEntry? entry) in Files)
        {
            string fullPath = Path.Combine(rootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
            {
                results.Add(new LockVerification(relativePath, "deleted", entry.Checksum, null));
                continue;
            }

            string currentChecksum = ComputeFileChecksum(fullPath);
            string status = currentChecksum == entry.Checksum ? "clean" : "modified";
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
        List<LockVerification> results = new List<LockVerification>();

        foreach ((string? relativePath, LockEntry? entry) in Files)
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
        foreach ((string? relativePath, LockEntry? entry) in Files)
        {
            if (rootDirectory != null)
            {
                string fullPath = Path.Combine(rootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                {
                    entry.Checksum = ComputeFileChecksum(fullPath);
                }
            }
            entry.Signature = Sign(entry.Checksum, key);
        }
    }

    /// <summary>
    /// Computes SHA256 checksum of a file, prefixed with "sha256:".
    /// </summary>
    public static string ComputeFileChecksum(string filePath)
    {
        using SHA256 sha256 = SHA256.Create();
        using FileStream stream = File.OpenRead(filePath);
        byte[] hash = sha256.ComputeHash(stream);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Signs data with HMAC-SHA256 using the given key.
    /// </summary>
    public static string Sign(string data, string key)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        using HMACSHA256 hmac = new HMACSHA256(keyBytes);
        byte[] hash = hmac.ComputeHash(dataBytes);
        return "hmac-sha256:" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Verifies an HMAC-SHA256 signature.
    /// </summary>
    public static bool Verify(string data, string signature, string key)
    {
        string expected = Sign(data, key);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }

    /// <summary>
    /// Normalizes a file path to repo-relative forward-slash format.
    /// </summary>
    public static string NormalizePath(string rootDirectory, string filePath)
    {
        string fullPath = Path.GetFullPath(filePath);
        string rootFull = Path.GetFullPath(rootDirectory);
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
        {
            rootFull += Path.DirectorySeparatorChar;
        }

        if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"File '{filePath}' is outside the project root '{rootDirectory}'.");
        }

        return Path.GetRelativePath(rootFull, fullPath).Replace('\\', '/');
    }
}
