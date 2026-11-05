// Discovers Python project manifests, environments, and dependency references.

using System.Text.Json;
using Cop.Core;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;

namespace Cop.Providers;

/// <summary>Reads Python project metadata without executing setup scripts or importing packages.</summary>
public static class PythonProjectDiscovery
{
    /// <summary>
    /// Discovers Python projects and optionally resolves toolchain context.
    /// </summary>
    public static List<ProjectInfo> Discover(string rootPath, IReadOnlySet<string>? excludedDirs, bool useToolchain = true)
    {
        PythonToolchain toolchain = new PythonToolchain(useToolchain);
        List<ProjectInfo> result = new List<ProjectInfo>();
        foreach (string manifest in CollectManifests(rootPath, excludedDirs))
        {
            try
            {
                string source = File.ReadAllText(manifest);
                string relative = Path.GetRelativePath(rootPath, manifest).Replace('\\', '/');
                bool toml = Path.GetFileName(manifest) == "pyproject.toml";
                using JsonDocument? document = toolchain.Analyze(manifest, source, toml ? "toml" : "setup");
                ProjectInfo? project;
                if (document is null)
                {
                    project = ParseSimple(source, relative, toml);
                }
                else
                {
                    JsonElement data = document.RootElement;
                    if (data.TryGetProperty("unsupported", out JsonElement unsupported))
                    {
                        ProviderWarnings.ReportOnce("python", "metadata", $"{unsupported.GetString()}. Using simpler Python project metadata analysis.");
                        project = ParseSimple(source, relative, toml);
                    }
                    else if (data.TryGetProperty("error", out JsonElement error))
                    {
                        ProviderErrors.Report($"{manifest}({data.GetProperty("line")},1): error: {error.GetString()}");
                        continue;
                    }
                    else
                    {
                        if (data.TryGetProperty("unresolved", out JsonElement unresolved) && unresolved.GetArrayLength() > 0)
                        {
                            ProviderWarnings.ReportOnce("python", manifest,
                                $"Python project '{manifest}' has dynamic metadata ({string.Join(", ", unresolved.EnumerateArray().Select(v => v.GetString()))}); these values are unknown and are not executed.");
                        }

                        JsonElement name = data.GetProperty("name");
                        List<string> dependencies = data.GetProperty("dependencies").EnumerateArray()
                            .Where(d => d.ValueKind == JsonValueKind.String).Select(d => NormalizeDependency(d.GetString()!))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        project = name.ValueKind == JsonValueKind.String
                            ? new(name.GetString()!, relative, "python", dependencies, dependencies, []) : null;
                    }
                }
                if (project is not null)
                {
                    result.Add(project);
                }
                else
                {
                    ProviderWarnings.ReportOnce("python", manifest,
                    $"Python project '{manifest}' has no statically readable project name; dynamic setup metadata is not executed.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            {
                ProviderErrors.Report($"Cannot analyze Python project '{manifest}': {ex.Message}");
            }
        }
        return result;
    }

    private static ProjectInfo? ParseSimple(string source, string path, bool toml)
    {
        string? name = null;
        List<string> dependencies = new List<string>();
        if (toml)
        {
            bool inProject = false;
            bool optional = false;
            bool collecting = false;
            foreach (string raw in source.Split('\n'))
            {
                string line = StripComment(raw).Trim();
                if (collecting)
                {
                    dependencies.AddRange(QuotedValues(line).Select(NormalizeDependency));
                    if (line.EndsWith(']'))
                    {
                        collecting = false;
                    }

                    continue;
                }
                if (line.StartsWith('['))
                {
                    inProject = line == "[project]";
                    optional = line == "[project.optional-dependencies]";
                    collecting = false;
                    continue;
                }
                int equal = line.IndexOf('=');
                string key = equal >= 0 ? line[..equal].Trim() : "";
                string value = equal >= 0 ? line[(equal + 1)..].Trim() : line;
                if (inProject && key == "name")
                {
                    name = QuotedValues(value).FirstOrDefault();
                }

                if ((inProject && key == "dependencies") || (optional && equal >= 0))
                {
                    collecting = true;
                }

                if (collecting)
                {
                    dependencies.AddRange(QuotedValues(value).Select(NormalizeDependency));
                    if (value.EndsWith(']'))
                    {
                        collecting = false;
                    }
                }
            }
        }
        else
        {
            // The no-interpreter path deliberately accepts literal setup keyword values only.
            List<Tok> tokens = new PythonLexer(source, path).Tokenize();
            for (int i = 0; i + 2 < tokens.Count; i++)
            {
                if (tokens[i + 1].Kind != TK.Eq)
                {
                    continue;
                }

                if (tokens[i].Is("name") && tokens[i + 2].Kind == TK.Str)
                {
                    name = tokens[i + 2].Text.Trim('\'', '"');
                }

                if (!tokens[i].Is("install_requires") || tokens[i + 2].Kind != TK.LBrack)
                {
                    continue;
                }

                for (int j = i + 3; j < tokens.Count && tokens[j].Kind != TK.RBrack; j++)
                {
                    if (tokens[j].Kind == TK.Str)
                    {
                        dependencies.Add(NormalizeDependency(tokens[j].Text.Trim('\'', '"')));
                    }
                }
            }
        }
        dependencies = dependencies.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return name is null ? null : new(name, path, "python", dependencies, dependencies, []);
    }

    private static string StripComment(string text)
    {
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && quote == '"') { i++; continue; }
            if (quote != '\0') { if (text[i] == quote)
                {
                    quote = '\0';
                }
            }
            else if (text[i] is '\'' or '"')
            {
                quote = text[i];
            }
            else if (text[i] == '#')
            {
                return text[..i];
            }
        }
        return text;
    }

    private static IEnumerable<string> QuotedValues(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\'' or '"'))
            {
                continue;
            }

            char quote = text[i];
            int start = ++i;
            while (i < text.Length && text[i] != quote)
            {
                i++;
            }

            if (i < text.Length)
            {
                yield return text[start..i];
            }
        }
    }

    private static string NormalizeDependency(string dependency)
    {
        dependency = dependency.TrimStart();
        int length = 0;
        while (length < dependency.Length && (char.IsAsciiLetterOrDigit(dependency[length]) || dependency[length] is '_' or '-' or '.'))
        {
            length++;
        }

        return length > 0 ? dependency[..length] : dependency;
    }

    private static IEnumerable<string> CollectManifests(string directory, IReadOnlySet<string>? excluded)
    {
        List<string> result = new List<string>();
        try
        {
            string pyproject = Path.Combine(directory, "pyproject.toml");
            string setup = Path.Combine(directory, "setup.py");
            if (File.Exists(pyproject))
            {
                result.Add(pyproject);
            }
            else if (File.Exists(setup))
            {
                result.Add(setup);
            }

            foreach (string? child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                if (excluded?.Contains(Path.GetFileName(child)) == true)
                {
                    continue;
                }

                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                result.AddRange(CollectManifests(child, excluded));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProviderErrors.Report($"Cannot discover Python projects in '{directory}': {ex.Message}");
        }
        return result;
    }
}
