using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>
/// Discovers Rust projects by scanning for Cargo.toml files,
/// extracting project name and dependencies.
/// </summary>
public static class RustProjectDiscovery
{
    /// <summary>
    /// Discovers Rust projects under rootPath by finding Cargo.toml files.
    /// </summary>
    public static List<ProjectInfo> Discover(string rootPath, IReadOnlySet<string>? excludedDirs)
    {
        var manifestPaths = new List<string>();
        CollectManifests(rootPath, excludedDirs, manifestPaths);

        var result = new List<ProjectInfo>();
        foreach (var manifestPath in manifestPaths)
        {
            var info = ParseCargoToml(manifestPath, rootPath);
            if (info is not null)
                result.Add(info);
        }
        return result;
    }

    private static ProjectInfo? ParseCargoToml(string filePath, string rootPath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            string? name = null;
            var prodDeps = new List<string>();   // [dependencies] (+ target normal) — used for layering
            var allDeps = new List<string>();    // prod + dev + build
            bool inPackage = false;
            bool inDeps = false;       // any dependency table
            bool inProdDeps = false;   // a normal (non dev/build) dependency table

            // Multi-line inline-table state: a `key = { ... }` that spans lines. We suppress
            // its continuation lines (so version/features aren't read as deps) and look for a
            // `package = "real"` rename to record the real crate name.
            int inlineDepth = 0;
            string? pendingKey = null;
            string? pendingPackage = null;
            bool pendingProd = false;

            void Flush()
            {
                if (pendingKey != null)
                {
                    var crate = pendingPackage ?? pendingKey;
                    allDeps.Add(crate);
                    if (pendingProd) prodDeps.Add(crate);
                }
                pendingKey = null;
                pendingPackage = null;
                pendingProd = false;
            }

            foreach (var rawLine in lines)
            {
                var line = StripComment(rawLine).Trim();
                if (line.Length == 0) continue;

                // Inside a multi-line inline table: suppress dep parsing, but capture a rename.
                if (inlineDepth > 0)
                {
                    if (TryReadPackageRename(line, out var pkgCont)) pendingPackage = pkgCont;
                    inlineDepth += BraceDelta(line);
                    if (inlineDepth <= 0) { inlineDepth = 0; Flush(); }
                    continue;
                }

                if (line.StartsWith("["))
                {
                    Flush();
                    int close = line.IndexOf(']');
                    var header = (close >= 0 ? line[1..close] : line.TrimStart('[')).Trim();

                    inPackage = header == "package";
                    inDeps = false;
                    inProdDeps = false;

                    bool isWorkspace = header.StartsWith("workspace");
                    if (!isWorkspace && TryReadSubTableDependency(header, out var subDep, out var subProd))
                    {
                        allDeps.Add(subDep);
                        if (subProd) prodDeps.Add(subDep);
                    }
                    else if (!isWorkspace && IsDependencyTable(header, out bool prod))
                    {
                        inDeps = true;
                        inProdDeps = prod;
                    }
                    continue;
                }

                if (inPackage)
                {
                    if (TryReadQuotedName(line, out var packageName)) name = packageName;
                }
                else if (inDeps)
                {
                    if (TryReadDependencyKey(line, out var depKey))
                    {
                        int braces = BraceDelta(line);
                        if (braces > 0)
                        {
                            // Opens an inline table that doesn't close on this line.
                            pendingKey = depKey;
                            pendingProd = inProdDeps;
                            if (TryReadPackageRename(line, out var pkg)) pendingPackage = pkg;
                            inlineDepth = braces;
                        }
                        else
                        {
                            var crate = depKey;
                            if (line.Contains('{') && TryReadPackageRename(line, out var pkg)) crate = pkg;
                            allDeps.Add(crate);
                            if (inProdDeps) prodDeps.Add(crate);
                        }
                    }
                }
            }
            Flush();

            if (name is null)
                return null;

            return new ProjectInfo(name, relativePath, "rust", prodDeps, allDeps, []);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // Removes a TOML line comment (`#...`) that is outside any quoted string.
    private static string StripComment(string line)
    {
        bool inString = false;
        char quote = '"';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inString) { if (c == quote) inString = false; }
            else if (c is '"' or '\'') { inString = true; quote = c; }
            else if (c == '#') return line[..i];
        }
        return line;
    }

    // Net `{` minus `}` outside strings (comments already stripped).
    private static int BraceDelta(string line)
    {
        int depth = 0;
        bool inString = false;
        char quote = '"';
        foreach (char c in line)
        {
            if (inString) { if (c == quote) inString = false; }
            else if (c is '"' or '\'') { inString = true; quote = c; }
            else if (c == '{') depth++;
            else if (c == '}') depth--;
        }
        return depth;
    }

    // Finds a `package = "real_crate"` rename inside an inline dependency table.
    private static bool TryReadPackageRename(string text, out string pkg)
    {
        pkg = "";
        int i = text.IndexOf("package", StringComparison.Ordinal);
        if (i < 0) return false;
        int j = i + "package".Length;
        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
        if (j >= text.Length || text[j] != '=') return false;
        j++;
        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
        if (j >= text.Length || text[j] != '"') return false;
        j++;
        int start = j;
        while (j < text.Length && text[j] != '"') j++;
        if (j <= start || j >= text.Length) return false;
        pkg = text[start..j];
        return true;
    }

    // Classifies a table header (brackets stripped) as a dependency table.
    // prod = true for a normal [dependencies] (or target normal) table; false for dev/build.
    private static bool IsDependencyTable(string header, out bool prod)
    {
        prod = false;
        if (header == "dependencies" || header.EndsWith(".dependencies")) { prod = true; return true; }
        if (header == "dev-dependencies" || header.EndsWith(".dev-dependencies")) return true;
        if (header == "build-dependencies" || header.EndsWith(".build-dependencies")) return true;
        return false;
    }

    private static bool TryReadQuotedName(string text, out string name)
    {
        name = "";
        const string keyword = "name";
        if (!text.StartsWith(keyword, StringComparison.Ordinal))
            return false;

        var index = keyword.Length;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        if (index >= text.Length || text[index] != '=')
            return false;

        index++;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        if (index >= text.Length || text[index] != '"')
            return false;

        index++;
        var nameStart = index;
        while (index < text.Length && text[index] != '"')
            index++;

        if (index == nameStart || index >= text.Length)
            return false;

        name = text[nameStart..index];
        return true;
    }

    private static bool TryReadDependencyKey(string text, out string key)
    {
        key = "";
        var index = 0;
        while (index < text.Length && IsDependencyKeyCharacter(text[index]))
            index++;

        if (index == 0)
            return false;

        var keyEnd = index;

        // Dotted single-line form: `dep.workspace = true`, `dep.path = "..."`,
        // `dep.version = "1.0"`. The dependency is on `dep`.
        if (index < text.Length && text[index] == '.')
        {
            key = text[..keyEnd];
            return true;
        }

        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        if (index >= text.Length || text[index] != '=')
            return false;

        key = text[..keyEnd];
        return true;
    }

    /// <summary>
    /// Recognizes a dependency sub-table header body (brackets already stripped), e.g.
    /// "dependencies.serde", "dev-dependencies.tokio", or
    /// "target.'cfg(unix)'.dependencies.nix", and extracts the trailing crate name.
    /// </summary>
    private static bool TryReadSubTableDependency(string header, out string name, out bool prod)
    {
        name = "";
        prod = true;
        const string marker = "dependencies.";
        int idx = header.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return false;

        var remainder = header[(idx + marker.Length)..].Trim();
        if (remainder.Length == 0)
            return false;

        foreach (var ch in remainder)
        {
            if (!IsDependencyKeyCharacter(ch))
                return false;
        }

        name = remainder;
        // [dev-dependencies.X] / [build-dependencies.X] are not production dependencies.
        string before = header[..idx];
        prod = !(before.EndsWith("dev-") || before.EndsWith("build-"));
        return true;
    }

    private static bool IsDependencyKeyCharacter(char ch) =>
        (ch >= 'a' && ch <= 'z') ||
        (ch >= 'A' && ch <= 'Z') ||
        (ch >= '0' && ch <= '9') ||
        ch is '_' or '-';

    private static void CollectManifests(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            var cargoToml = Path.Combine(dir, "Cargo.toml");
            if (File.Exists(cargoToml))
            {
                result.Add(cargoToml);
                // Don't return — Rust workspaces have nested Cargo.toml files
            }

            foreach (var subDir in Directory.GetDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName)) continue;
                // Skip target directory (Rust build output)
                if (dirName == "target") continue;
                CollectManifests(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
