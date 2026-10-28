using System.Text;
using System.Globalization;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers;

/// <summary>Reads Cargo manifests without running Cargo, build scripts, or fetching dependencies.</summary>
public static class RustProjectDiscovery
{
    private sealed class Dependency
    {
        public string Alias = "";
        public string? Package;
        public bool Workspace;
        public bool Production;
    }

    private sealed class Manifest(string path)
    {
        public string Path { get; } = path;
        public string? Name;
        public string? WorkspacePath;
        public bool IsWorkspace;
        public Dictionary<string, Dependency> Dependencies { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dependency> WorkspaceDependencies { get; } = new(StringComparer.Ordinal);
    }

    public static List<ProjectInfo> Discover(string rootPath, IReadOnlySet<string>? excludedDirs)
    {
        var paths = new List<string>();
        CollectManifests(rootPath, excludedDirs, paths);
        var manifests = paths.Order(StringComparer.Ordinal).Select(Read).Where(m => m is not null).Cast<Manifest>().ToList();
        var results = new List<ProjectInfo>();
        foreach (var manifest in manifests)
        {
            if (manifest.Name is null) continue;
            Manifest? workspace;
            if (manifest.WorkspacePath is { } relativeWorkspace)
            {
                string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(manifest.Path)!, relativeWorkspace, "Cargo.toml"));
                workspace = manifests.FirstOrDefault(m => m.Path == path && m.IsWorkspace);
            }
            else
            {
                workspace = manifests.Where(m => m.IsWorkspace && IsInside(manifest.Path, m.Path))
                    .OrderByDescending(m => m.Path.Length).FirstOrDefault();
            }
            var references = new SortedSet<string>(StringComparer.Ordinal);
            var packages = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var dependency in manifest.Dependencies.Values)
            {
                string alias = dependency.Alias;
                string name = dependency.Package ?? alias;
                if (dependency.Workspace)
                {
                    if (workspace?.WorkspaceDependencies.TryGetValue(alias, out var inherited) == true)
                        name = dependency.Package ?? inherited.Package ?? alias;
                    else
                        ProviderErrors.Report($"{manifest.Path}: warning: workspace dependency '{alias}' could not be resolved; retaining its alias.");
                }
                packages.Add(name);
                if (dependency.Production) references.Add(name);
            }
            results.Add(new ProjectInfo(manifest.Name,
                System.IO.Path.GetRelativePath(rootPath, manifest.Path).Replace('\\', '/'), "rust",
                references.ToList(), packages.ToList(), []));
        }
        return results;
    }

    private static bool IsInside(string path, string manifestPath)
    {
        string directory = System.IO.Path.GetDirectoryName(manifestPath)!;
        string relative = System.IO.Path.GetRelativePath(directory, path);
        return !System.IO.Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static Manifest? Read(string path)
    {
        try
        {
            var manifest = new Manifest(path);
            List<string> table = [];
            foreach (var (text, line) in Entries(File.ReadAllText(path)))
            {
                if (text.StartsWith('['))
                {
                    if (!text.EndsWith(']')) throw new FormatException($"line {line}: unterminated table header");
                    bool array = text.StartsWith("[[", StringComparison.Ordinal);
                    table = KeyPath(text[(array ? 2 : 1)..^(array ? 2 : 1)]);
                    if (table.Count == 1 && table[0] == "workspace") manifest.IsWorkspace = true;
                    continue;
                }
                int equals = OutsideIndex(text, '=');
                if (equals < 0) throw new FormatException($"line {line}: expected key = value");
                var key = KeyPath(text[..equals]);
                string value = text[(equals + 1)..].Trim();
                if (key.Count == 0 || value.Length == 0) throw new FormatException($"line {line}: missing key or value");
                List<string> full = [..table, ..key];
                if (full.SequenceEqual(["package", "name"]))
                    manifest.Name = String(value) ?? throw new FormatException($"line {line}: package name must be a quoted string");
                if (full.SequenceEqual(["package", "workspace"])) manifest.WorkspacePath = String(value);
                int depIndex = full.FindIndex(k => k is "dependencies" or "dev-dependencies" or "build-dependencies");
                if (depIndex < 0 || full.Count <= depIndex + 1) continue;
                bool workspace = full[0] == "workspace";
                if (workspace) manifest.IsWorkspace = true;
                var dependencies = workspace ? manifest.WorkspaceDependencies : manifest.Dependencies;
                string alias = full[depIndex + 1];
                string identity = workspace ? alias : string.Join(".", full.Take(depIndex + 2).Select(k => k.Length + ":" + k));
                if (!dependencies.TryGetValue(identity, out var dependency))
                    dependencies.Add(identity, dependency = new Dependency { Alias = alias });
                dependency.Production |= full[depIndex] == "dependencies";
                if (full.Count > depIndex + 2)
                    Set(dependency, full[^1], value);
                else if (value.StartsWith('{'))
                {
                    foreach (string field in Split(value[1..^1], ','))
                    {
                        int separator = OutsideIndex(field, '=');
                        if (separator < 0) continue;
                        var fieldKey = KeyPath(field[..separator]);
                        if (fieldKey.Count == 1) Set(dependency, fieldKey[0], field[(separator + 1)..].Trim());
                    }
                }
            }
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            ProviderErrors.Report($"{path}: warning: failed to read Cargo manifest: {ex.Message}");
            return null;
        }
    }

    private static void Set(Dependency dependency, string key, string value)
    {
        if (key == "package")
            dependency.Package = String(value) ?? throw new FormatException("dependency package must be a quoted string");
        if (key == "workspace")
        {
            if (value is not "true" and not "false") throw new FormatException("dependency workspace must be a boolean");
            dependency.Workspace = value == "true";
        }
    }

    private static string? String(string value)
    {
        if (value.Length < 2) return null;
        if (value[0] == '\'' && value[^1] == '\'') return value[1..^1];
        if (value[0] != '"' || value[^1] != '"') return null;
        var decoded = new StringBuilder();
        for (int i = 1; i < value.Length - 1; i++)
        {
            char c = value[i];
            if (c != '\\') { decoded.Append(c); continue; }
            if (++i >= value.Length - 1) throw new FormatException("incomplete TOML string escape");
            c = value[i];
            switch (c)
            {
                case 'b': decoded.Append('\b'); break;
                case 't': decoded.Append('\t'); break;
                case 'n': decoded.Append('\n'); break;
                case 'f': decoded.Append('\f'); break;
                case 'r': decoded.Append('\r'); break;
                case '"': case '\\': decoded.Append(c); break;
                case 'u': case 'U':
                    int digits = c == 'u' ? 4 : 8;
                    if (i + digits >= value.Length - 1 || !int.TryParse(value.AsSpan(i + 1, digits),
                        NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int scalar) || !Rune.IsValid(scalar))
                        throw new FormatException("invalid TOML Unicode escape");
                    decoded.Append(char.ConvertFromUtf32(scalar));
                    i += digits;
                    break;
                default: throw new FormatException($"invalid TOML escape '\\{c}'");
            }
        }
        return decoded.ToString();
    }

    private static List<string> KeyPath(string text) => Split(text.Trim(), '.')
        .Select(p => String(p.Trim()) ?? p.Trim()).ToList();

    private static int OutsideIndex(string text, char character)
    {
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (quote == '"' && c == '\\') i++;
                else if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == character) return i;
        }
        return -1;
    }

    private static IEnumerable<string> Split(string text, char separator)
    {
        char quote = '\0';
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0')
            {
                if (quote == '"' && c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c is '{' or '[' or '(') depth++;
            else if (c is '}' or ']' or ')') depth--;
            else if (c == separator && depth == 0)
            {
                if (i > start) yield return text[start..i];
                start = i + 1;
            }
        }
        if (quote != '\0' || depth != 0) throw new FormatException("unbalanced quoted key or value");
        if (start < text.Length) yield return text[start..];
    }

    // Joins multiline arrays/inline tables, respecting both TOML quoting styles and comments.
    private static IEnumerable<(string text, int line)> Entries(string source)
    {
        var entry = new StringBuilder();
        char quote = '\0';
        bool triple = false;
        int depth = 0, line = 1, start = 1;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (quote != '\0')
            {
                entry.Append(c);
                if (c == '\\' && quote == '"' && i + 1 < source.Length)
                {
                    entry.Append(source[++i]);
                    if (source[i] == '\n') line++;
                    continue;
                }
                if (c == quote)
                {
                    if (!triple) quote = '\0';
                    else if (i + 2 < source.Length && source[i + 1] == quote && source[i + 2] == quote)
                    { entry.Append(source[++i]); entry.Append(source[++i]); quote = '\0'; triple = false; }
                }
                if (c == '\n') line++;
                continue;
            }
            if (c == '#')
            {
                while (i < source.Length && source[i] != '\n') i++;
                c = '\n';
            }
            if (c is '"' or '\'')
            {
                quote = c;
                triple = i + 2 < source.Length && source[i + 1] == c && source[i + 2] == c;
                entry.Append(c);
                if (triple) { entry.Append(source[++i]); entry.Append(source[++i]); }
                continue;
            }
            if (c is '[' or '{') depth++;
            if (c is ']' or '}') depth--;
            if (depth < 0) throw new FormatException($"line {line}: unexpected '{c}'");
            if (c == '\n')
            {
                if (depth == 0)
                {
                    if (entry.ToString().Trim() is { Length: > 0 } text) yield return (text, start);
                    entry.Clear();
                    start = line + 1;
                }
                else entry.Append(' ');
                line++;
            }
            else entry.Append(c);
        }
        if (quote != '\0' || depth != 0) throw new FormatException($"line {start}: unterminated quoted value or table");
        if (entry.ToString().Trim() is { Length: > 0 } last) yield return (last, start);
    }

    private static void CollectManifests(string directory, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            string path = System.IO.Path.Combine(directory, "Cargo.toml");
            if (File.Exists(path)) result.Add(System.IO.Path.GetFullPath(path));
            foreach (string child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                string name = System.IO.Path.GetFileName(child);
                if (name is "target" or ".git" || excluded?.Contains(name) == true
                    || File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) continue;
                CollectManifests(child, excluded, result);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProviderErrors.Report($"{directory}: warning: failed to discover Cargo manifests: {ex.Message}");
        }
    }
}
