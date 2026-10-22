using System.Security.Cryptography;
using System.Text;

namespace Cop.Core;

public sealed partial class ChangeSnapshot
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", ".vs", ".idea", "node_modules", ".nuget", ".dotnet", "packages",
        "TestResults", "__pycache__", ".mypy_cache", ".pytest_cache", "dist", "build", "out", ".next", ".cache"
    };

    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".netrc", ".npmrc", ".pypirc", ".git-credentials", "credentials", "credentials.json",
        "secrets.json", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519"
    };

    /// <summary>Reads repository-relative source from the pinned base or the fingerprinted final working tree.</summary>
    public string ReadSource(string repositoryRelativePath, int startLine, int lineCount, string side = "head")
    {
        if (startLine < 1 || lineCount < 1 || lineCount > MaxReadLines)
            throw new InvalidOperationException($"Source reads require a positive start line and 1..{MaxReadLines} lines; split larger requests.");
        if (side is not ("head" or "base"))
            throw new InvalidOperationException("Source side must be 'head' (final working tree) or 'base' (merge base).");
        var path = ValidateSourcePath(repositoryRelativePath, side == "head");
        byte[] bytes;
        if (side == "head")
        {
            bytes = ReadFileBounded(ResolveRepositoryPath(path));
            EnsureFingerprint(path, bytes);
        }
        else
        {
            if (!Files.Any(file => PathComparer.Equals(file.Path, path) || PathComparer.Equals(file.OldPath, path))
                && !fingerprints.ContainsKey(path))
                throw new InvalidOperationException("Base source is available only for eligible files belonging to this snapshot.");
            // The argument is a single revision:path token, never a shell expression.
            bytes = StrictUtf8.GetBytes(Git("show", $"{BaseCommit}:{path}"));
        }
        if (!TryText(bytes, out var source))
            throw new InvalidOperationException("Binary or non-UTF-8 source cannot be read by review tools.");
        var lines = SourceLines(source);
        if (startLine > lines.Length)
            throw new InvalidOperationException($"Source starts beyond EOF ({lines.Length} lines).");
        var selected = lines.Skip(startLine - 1).Take(lineCount).ToArray();
        var text = string.Join('\n', selected.Select((line, index) => $"{startLine + index}: {line}"));
        if (Encoding.UTF8.GetByteCount(text) > MaxSourceResponseBytes)
            throw new InvalidOperationException($"Source response exceeds {MaxSourceResponseBytes} UTF-8 bytes; request fewer lines.");
        return text;
    }

    /// <summary>Literal, case-insensitive search of eligible snapshot files, including unchanged source.</summary>
    public string SearchSource(string query, int maxResults = MaxSearchResults)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 512 || query.Contains('\n') || query.Contains('\r') || query.Contains('\0'))
            throw new InvalidOperationException("Source search requires a nonempty, single-line literal query of at most 512 characters.");
        if (maxResults < 1 || maxResults > MaxSearchResults)
            throw new InvalidOperationException($"Source search accepts 1..{MaxSearchResults} results.");
        var results = new List<string>();
        var bytesUsed = 0;
        var incomplete = false;
        const string marker = "[incomplete: search reached a result/byte limit or skipped binary/oversized source]";
        var reserve = Encoding.UTF8.GetByteCount(marker) + 1;
        foreach (var path in fingerprints.Keys.OrderBy(path => path, StringComparer.Ordinal))
        {
            var fullPath = ResolveRepositoryPath(path);
            RejectReparsePoints(fullPath);
            if (Fingerprint(fullPath) != fingerprints[path])
                throw Stale();
            if (!File.Exists(fullPath))
                continue;
            if (new FileInfo(fullPath).Length > MaxSourceFileBytes)
            {
                incomplete = true;
                continue;
            }
            var bytes = ReadFileBounded(fullPath);
            EnsureFingerprint(path, bytes);
            if (!TryText(bytes, out var source))
            {
                incomplete = true;
                continue;
            }
            var lines = SourceLines(source);
            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                var match = $"{path}:{index + 1}: {lines[index]}";
                var size = Encoding.UTF8.GetByteCount(match) + 1;
                if (results.Count == maxResults || bytesUsed + size + reserve > MaxSourceResponseBytes)
                {
                    incomplete = true;
                    goto Done;
                }
                results.Add(match);
                bytesUsed += size;
            }
        }
    Done:
        if (results.Count == 0)
            results.Add("No matches.");
        if (incomplete)
            results.Add(marker);
        return string.Join('\n', results);
    }

    private string ValidateSourcePath(string path, bool requireCurrent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':'))
            throw new InvalidOperationException("Source tool paths must be repository-relative paths using forward slashes.");
        RejectTraversal(path);
        if (!IsEligible(path))
            throw new InvalidOperationException("Source path is outside the target, generated, or sensitive.");
        var fullPath = ResolveRepositoryPath(path);
        RejectReparsePoints(fullPath);
        if (requireCurrent && (!fingerprints.ContainsKey(path) || !File.Exists(fullPath)))
            throw new InvalidOperationException("Source is not a tracked or nonignored file in this snapshot (or has been deleted).");
        return path;
    }

    private bool IsEligible(string path)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':'))
            return false;
        var parts = path.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".."))
            return false;
        if (targetPrefix.Length != 0 && !path.StartsWith(targetPrefix + "/", PathComparison))
            return false;
        if (parts.Any(part => ExcludedDirectories.Contains(part) || part.Equals(".ssh", StringComparison.OrdinalIgnoreCase)
            || part.Equals(".aws", StringComparison.OrdinalIgnoreCase) || part.Equals(".azure", StringComparison.OrdinalIgnoreCase)))
            return false;
        var name = parts[^1];
        return !SensitiveNames.Contains(name)
            && !name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".p12", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveRepositoryPath(string path)
    {
        RejectTraversal(path);
        var fullPath = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), RepositoryRoot);
        if (!IsWithin(fullPath, RootPath))
            throw new InvalidOperationException("Source path is outside the review target.");
        return fullPath;
    }

    private static bool IsWithin(string path, string root) =>
        path.Equals(root, PathComparison)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);

    private static void RejectTraversal(string path)
    {
        if (path.Contains('\0') || path.Split('/', '\\').Any(part => part is "." or ".."))
            throw new InvalidOperationException("Source paths cannot contain traversal components.");
        if (OperatingSystem.IsWindows())
        {
            var tail = Path.IsPathRooted(path) ? path[Path.GetPathRoot(path)!.Length..] : path;
            if (tail.Contains(':') || tail.Split('/', '\\').Any(part => part.EndsWith('.') || part.EndsWith(' ')))
                throw new InvalidOperationException("Source paths cannot use alternate streams or ambiguous Windows names.");
        }
    }

    private static void RejectReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Source access through symbolic links, junctions, or other reparse points is prohibited.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private static string Fingerprint(string fullPath)
    {
        if (!File.Exists(fullPath))
            return "<missing>";
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private void EnsureFingerprint(string path, byte[] bytes)
    {
        if (!fingerprints.TryGetValue(path, out var expected) || Hash(bytes) != expected)
            throw Stale();
    }

    private static byte[] ReadFileBounded(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxSourceFileBytes)
            throw new InvalidOperationException($"Source file exceeds {MaxSourceFileBytes} bytes; narrow the review instead of truncating source.");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = stream.Read(chunk)) != 0)
        {
            if (buffer.Length + count > MaxSourceFileBytes)
                throw new InvalidOperationException("Source file grew beyond the review limit.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static bool TryText(byte[] bytes, out string text)
    {
        text = "";
        if (bytes.Contains((byte)0))
            return false;
        try
        {
            text = StrictUtf8.GetString(bytes);
            if (text.StartsWith('\uFEFF'))
                text = text[1..];
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    private static string[] SourceLines(string source)
    {
        if (source.Length == 0)
            return Array.Empty<string>();
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}
