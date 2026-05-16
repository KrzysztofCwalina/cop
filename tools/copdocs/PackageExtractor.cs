namespace CopDocs;

using Cop.Lang;

/// <summary>
/// Extracts documentation data from a Cop package directory.
/// </summary>
public class PackageExtractor
{
    private readonly string? _repoBaseUrl;
    private readonly string? _repoRoot;

    public PackageExtractor(string? repoBaseUrl = null, string? repoRoot = null)
    {
        _repoBaseUrl = repoBaseUrl?.TrimEnd('/');
        _repoRoot = repoRoot != null ? Path.GetFullPath(repoRoot) : null;
    }

    private string? MakeSourceUrl(string filePath, int line)
    {
        if (_repoBaseUrl == null || _repoRoot == null) return null;
        var fullPath = Path.GetFullPath(filePath);
        if (!fullPath.StartsWith(_repoRoot, StringComparison.OrdinalIgnoreCase)) return null;
        var relativePath = fullPath[_repoRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        relativePath = relativePath.Replace('\\', '/');
        return $"{_repoBaseUrl}/{relativePath}#L{line}";
    }

    /// <summary>
    /// Extract package documentation from a directory containing a manifest .md and src/*.cop files.
    /// </summary>
    public PackageEntry Extract(string packageDir)
    {
        var entry = new PackageEntry();
        var dirName = Path.GetFileName(packageDir);

        // Read manifest
        var manifest = ReadManifest(packageDir, dirName);
        entry.Label = manifest.Name ?? dirName;
        entry.Overview = manifest.Description ?? "";

        // Parse all .cop files in src/
        var srcDir = Path.Combine(packageDir, "src");
        var copFiles = Directory.Exists(srcDir)
            ? Directory.GetFiles(srcDir, "*.cop")
            : [];

        var allLets = new List<(LetDeclaration Let, string FilePath)>();

        foreach (var copFile in copFiles)
        {
            var source = File.ReadAllText(copFile);

            try
            {
                var scriptFile = ScriptParser.Parse(source, copFile);
                ExtractTypes(scriptFile, entry, copFile);
                ExtractPredicates(scriptFile, entry, copFile);
                ExtractFunctions(scriptFile, entry, copFile);
                ExtractCollections(scriptFile, entry);
                ExtractEnums(scriptFile, entry, copFile);
                ExtractCommands(scriptFile, entry, copFile);
                allLets.AddRange(scriptFile.LetDeclarations.Select(l => (l, copFile)));
            }
            catch (ParseException)
            {
                // Skip files that fail to parse
            }
        }

        // Extract checks from let declarations
        ExtractChecks(allLets, entry);

        // Extract collection exports (non-check exported lets that provide data)
        ExtractCollectionExports(allLets, entry);

        // Load samples
        LoadSamples(packageDir, entry);

        return entry;
    }

    private void ExtractTypes(ScriptFile sf, PackageEntry entry, string copFile)
    {
        foreach (var td in sf.TypeDefinitions.Where(t => t.IsExported))
        {
            var typeEntry = new TypeEntry { Desc = td.DocComment, SourceUrl = MakeSourceUrl(copFile, td.Line) };
            foreach (var prop in td.Properties)
            {
                var typeName = prop.IsCollection ? $"[{prop.TypeName}]" : prop.TypeName;
                if (prop.IsOptional) typeName += "?";
                typeEntry.Props.Add(new PropEntry
                {
                    Name = prop.Name,
                    Type = typeName,
                    Desc = ""
                });
            }
            entry.Types[td.Name] = typeEntry;
        }
    }

    private void ExtractPredicates(ScriptFile sf, PackageEntry entry, string copFile)
    {
        foreach (var pd in sf.Predicates.Where(p => p.IsExported))
        {
            entry.Predicates.Add(new PredicateEntry
            {
                Name = pd.Name,
                AppliesTo = pd.ParameterType,
                Desc = pd.DocComment ?? "",
                SourceUrl = MakeSourceUrl(copFile, pd.Line)
            });
        }
    }

    private void ExtractFunctions(ScriptFile sf, PackageEntry entry, string copFile)
    {
        foreach (var fd in sf.Functions.Where(f => f.IsExported))
        {
            var parts = new List<string>();
            string? appliesTo = null;
            // Show InputType only when it's a standalone positional type (e.g., Statement),
            // not when it duplicates the first named parameter's type (e.g., data(name: string))
            if (!string.IsNullOrEmpty(fd.InputType)
                && !(fd.Parameters.Count > 0 && fd.Parameters[0].TypeName == fd.InputType))
            {
                parts.Add(fd.InputType);
                appliesTo = fd.InputType;
            }
            parts.AddRange(fd.Parameters.Select(p => $"{p.Name}: {p.TypeName}"));
            var paramStr = string.Join(", ", parts);
            entry.Functions.Add(new FunctionEntry
            {
                Name = fd.Name,
                Params = paramStr,
                Returns = fd.ReturnType,
                AppliesTo = appliesTo,
                Desc = fd.DocComment ?? "",
                SourceUrl = MakeSourceUrl(copFile, fd.Line)
            });
        }
    }

    private static void ExtractCollections(ScriptFile sf, PackageEntry entry)
    {
        foreach (var cd in sf.CollectionDeclarations.Where(c => c.IsExported))
        {
            entry.Collections.Add(cd.Name);
        }
    }

    private void ExtractEnums(ScriptFile sf, PackageEntry entry, string copFile)
    {
        if (sf.EnumDefinitions is { Count: > 0 })
        {
            entry.Enums ??= [];
            foreach (var ed in sf.EnumDefinitions.Where(e => e.IsExported))
            {
                entry.Enums.Add(new EnumEntry
                {
                    Name = ed.Name,
                    Values = string.Join("|", ed.Members),
                    SourceUrl = MakeSourceUrl(copFile, ed.Line)
                });
            }
        }

        if (sf.FlagsDefinitions is { Count: > 0 })
        {
            entry.Enums ??= [];
            foreach (var fd in sf.FlagsDefinitions.Where(f => f.IsExported))
            {
                entry.Enums.Add(new EnumEntry
                {
                    Name = fd.Name,
                    Values = string.Join("|", fd.Members),
                    SourceUrl = MakeSourceUrl(copFile, fd.Line)
                });
            }
        }
    }

    private void ExtractCommands(ScriptFile sf, PackageEntry entry, string copFile)
    {
        foreach (var cmd in sf.Commands.Where(c => c.IsExported && c.IsCommand))
        {
            entry.Commands.Add(new CommandEntry
            {
                Name = cmd.Name,
                Params = cmd.Parameters != null ? string.Join(", ", cmd.Parameters) : "",
                Desc = cmd.DocComment ?? "",
                SourceUrl = MakeSourceUrl(copFile, cmd.Line)
            });
        }
    }

    private void ExtractChecks(List<(LetDeclaration Let, string FilePath)> allLets, PackageEntry entry)
    {
        var lets = allLets.Select(l => l.Let).ToList();
        foreach (var (let, filePath) in allLets.Where(l => l.Let.IsExported))
        {
            if (IsViolationCollection(let, lets))
            {
                entry.Checks.Add(new CheckEntry
                {
                    Name = let.Name,
                    Desc = let.DocComment ?? "",
                    SourceUrl = MakeSourceUrl(filePath, let.Line)
                });
            }
        }
    }

    private static bool IsViolationCollection(LetDeclaration let, List<LetDeclaration> allLets)
    {
        if (!let.IsValueBinding && !let.IsCollectionUnion)
            return let.Filters.Count > 0 && HasViolationFilter(let.Filters);

        if (let.IsCollectionUnion && let.ValueExpression is CollectionUnionExpr union)
        {
            foreach (var element in union.Elements)
            {
                if (element is IdentifierExpr id)
                {
                    var constituent = allLets.FirstOrDefault(l => l.Name == id.Name);
                    if (constituent != null && IsViolationCollection(constituent, allLets))
                        return true;
                }
            }
        }

        return false;
    }

    private static bool HasViolationFilter(List<Expression> filters)
    {
        foreach (var filter in filters)
        {
            var name = filter switch
            {
                CallExpr c => c.Name,
                _ => null
            };
            if (name is "toError" or "toWarning" or "toInfo")
                return true;
        }
        return false;
    }

    private void ExtractCollectionExports(List<(LetDeclaration Let, string FilePath)> allLets, PackageEntry entry)
    {
        var lets = allLets.Select(l => l.Let).ToList();
        foreach (var (let, filePath) in allLets.Where(l => l.Let.IsExported))
        {
            // Skip checks (already extracted)
            if (IsViolationCollection(let, lets)) continue;

            // Skip collection unions that aggregate checks (e.g., python-checks = a + b + c)
            if (let.IsCollectionUnion) continue;

            // Collection-forwarding let: export let Types = cb.Types
            if (!let.IsValueBinding && let.Filters.Count == 0 && let.BaseCollection.Contains('.'))
            {
                entry.Exports ??= [];
                entry.Exports.Add(new CollectionExportEntry
                {
                    Name = let.Name,
                    Type = InferCollectionType(let.Name),
                    Desc = let.DocComment ?? "",
                    SourceUrl = MakeSourceUrl(filePath, let.Line)
                });
                continue;
            }

            // Intrinsic call: export let Disk = data('filesystem'), source('http'), sink('http')
            if (let.IsValueBinding && let.ValueExpression is CallExpr call
                && call.Name is "data" or "source" or "sink")
            {
                var providerArg = call.Args.Count > 0 && call.Args[0] is LiteralExpr s && s.Value is string sv ? sv : "?";
                entry.Exports ??= [];
                entry.Exports.Add(new CollectionExportEntry
                {
                    Name = let.Name,
                    Type = $"{call.Name}('{providerArg}')",
                    Desc = let.DocComment ?? "",
                    SourceUrl = MakeSourceUrl(filePath, let.Line)
                });
                continue;
            }
        }
    }

    private static string InferCollectionType(string name)
    {
        // Singularize common collection names: Types→[Type], Statements→[Statement], etc.
        if (name.EndsWith("ies"))
            return $"[{name[..^3]}y]";
        if (name.EndsWith("es") && !name.EndsWith("ses"))
            return $"[{name[..^2]}]";
        if (name.EndsWith("s") && !name.EndsWith("ss"))
            return $"[{name[..^1]}]";
        return $"[{name}]";
    }

    private static void LoadSamples(string packageDir, PackageEntry entry)
    {
        var samplesDir = Path.Combine(packageDir, "samples");
        if (!Directory.Exists(samplesDir)) return;

        foreach (var sampleFile in Directory.GetFiles(samplesDir, "*.cop").OrderBy(f => f))
        {
            var title = Path.GetFileNameWithoutExtension(sampleFile).Replace('-', ' ').Replace('_', ' ');
            // Capitalize first letter
            if (title.Length > 0)
                title = char.ToUpper(title[0]) + title[1..];

            var code = File.ReadAllText(sampleFile).ReplaceLineEndings("\n").TrimEnd();
            entry.Samples.Add(new SampleEntry { Title = title, Code = code });
        }
    }

    public record PackageManifest(string? Name, string? Description, string? Language, List<string>? Tags);

    private static PackageManifest ReadManifest(string packageDir, string dirName)
    {
        // Prefer cop.json
        var jsonFile = Path.Combine(packageDir, Cop.Core.PackageMetadata.MetadataFileName);
        if (File.Exists(jsonFile))
        {
            try
            {
                var metadata = Cop.Core.PackageMetadata.ParseFromJsonFile(jsonFile);
                return new PackageManifest(
                    metadata.Name,
                    metadata.Description,
                    metadata.Language,
                    metadata.Tags.Count > 0 ? metadata.Tags : null);
            }
            catch { /* fall through to legacy */ }
        }

        // Legacy fallback: <dirname>.md with YAML frontmatter
        var mdFile = Path.Combine(packageDir, $"{dirName}.md");
        if (!File.Exists(mdFile))
        {
            // Try any .md file in the directory
            var mdFiles = Directory.GetFiles(packageDir, "*.md");
            mdFile = mdFiles.FirstOrDefault(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase));
            if (mdFile == null) return new PackageManifest(dirName, null, null, null);
        }

        var content = File.ReadAllText(mdFile);
        return ParseFrontmatter(content, dirName);
    }

    private static PackageManifest ParseFrontmatter(string content, string fallbackName)
    {
        if (!content.StartsWith("---"))
            return new PackageManifest(fallbackName, null, null, null);

        var endIdx = content.IndexOf("---", 3);
        if (endIdx < 0) return new PackageManifest(fallbackName, null, null, null);

        var yaml = content[3..endIdx];
        string? name = null, description = null, language = null;
        List<string>? tags = null;

        foreach (var line in yaml.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("name:"))
                name = trimmed["name:".Length..].Trim();
            else if (trimmed.StartsWith("description:"))
                description = trimmed["description:".Length..].Trim();
            else if (trimmed.StartsWith("language:"))
                language = trimmed["language:".Length..].Trim();
            else if (trimmed.StartsWith("tags:"))
            {
                var tagStr = trimmed["tags:".Length..].Trim();
                tags = tagStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
        }

        return new PackageManifest(name ?? fallbackName, description, language, tags);
    }

    /// <summary>
    /// Determine category for a package based on its path and manifest.
    /// </summary>
    public static string GetCategory(string packageDir, PackageManifest? manifest)
    {
        var normalized = packageDir.Replace('\\', '/');

        if (normalized.Contains("/dotnet/")) return ".NET";
        if (normalized.Contains("/python/")) return "Python";
        if (normalized.Contains("/js/")) return "JavaScript";

        var dirName = Path.GetFileName(packageDir);
        if (dirName.StartsWith("code")) return "Code";
        if (dirName == "cop") return "Cop";
        if (dirName.StartsWith("typespec")) return "TypeSpec";
        if (dirName == "http") return "Misc";

        if (manifest?.Language != null)
        {
            return manifest.Language switch
            {
                "C#" or "csharp" => ".NET",
                "Python" or "python" => "Python",
                "JavaScript" or "javascript" or "TypeScript" or "typescript" => "JavaScript",
                "TypeSpec" or "typespec" => "TypeSpec",
                _ => "Core"
            };
        }

        return "Core";
    }

    public PackageManifest GetManifest(string packageDir)
    {
        return ReadManifest(packageDir, Path.GetFileName(packageDir));
    }
}
