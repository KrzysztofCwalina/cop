namespace CopDocs;

using Cop.Lang;

/// <summary>
/// Extracts documentation data from a Cop package directory.
/// </summary>
public class PackageExtractor
{
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

        var allLets = new List<LetDeclaration>();
        var docComments = new Dictionary<string, string>();

        foreach (var copFile in copFiles)
        {
            var source = File.ReadAllText(copFile);

            // Extract doc comments (## lines) associated with export let declarations
            ExtractDocComments(source, docComments);

            try
            {
                var scriptFile = ScriptParser.Parse(source, copFile);
                ExtractTypes(scriptFile, entry);
                ExtractPredicates(scriptFile, entry);
                ExtractFunctions(scriptFile, entry);
                ExtractCollections(scriptFile, entry);
                ExtractEnums(scriptFile, entry);
                ExtractCommands(scriptFile, entry);
                allLets.AddRange(scriptFile.LetDeclarations);
            }
            catch (ParseException)
            {
                // Skip files that fail to parse
            }
        }

        // Extract checks from let declarations
        ExtractChecks(allLets, docComments, entry);

        // Load samples
        LoadSamples(packageDir, entry);

        return entry;
    }

    private static void ExtractTypes(ScriptFile sf, PackageEntry entry)
    {
        foreach (var td in sf.TypeDefinitions.Where(t => t.IsExported))
        {
            var typeEntry = new TypeEntry();
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

    private static void ExtractPredicates(ScriptFile sf, PackageEntry entry)
    {
        foreach (var pd in sf.Predicates.Where(p => p.IsExported))
        {
            entry.Predicates.Add(new PredicateEntry
            {
                Name = pd.Name,
                AppliesTo = pd.ParameterType,
                Desc = ""
            });
        }
    }

    private static void ExtractFunctions(ScriptFile sf, PackageEntry entry)
    {
        foreach (var fd in sf.Functions.Where(f => f.IsExported))
        {
            var paramStr = string.Join(", ", fd.Parameters.Select(p => $"{p.Name}: {p.TypeName}"));
            entry.Functions.Add(new FunctionEntry
            {
                Name = fd.Name,
                Params = paramStr,
                Returns = fd.ReturnType,
                Desc = ""
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

    private static void ExtractEnums(ScriptFile sf, PackageEntry entry)
    {
        if (sf.EnumDefinitions is { Count: > 0 })
        {
            entry.Enums ??= [];
            foreach (var ed in sf.EnumDefinitions.Where(e => e.IsExported))
            {
                entry.Enums.Add(new EnumEntry
                {
                    Name = ed.Name,
                    Values = string.Join("|", ed.Members)
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
                    Values = string.Join("|", fd.Members)
                });
            }
        }
    }

    private static void ExtractCommands(ScriptFile sf, PackageEntry entry)
    {
        foreach (var cmd in sf.Commands.Where(c => c.IsExported && c.IsCommand))
        {
            entry.Commands.Add(new CommandEntry
            {
                Name = cmd.Name,
                Params = cmd.Parameters != null ? string.Join(", ", cmd.Parameters) : "",
                Desc = cmd.DocComment ?? ""
            });
        }
    }

    private static void ExtractChecks(List<LetDeclaration> allLets, Dictionary<string, string> docComments, PackageEntry entry)
    {
        foreach (var let in allLets.Where(l => l.IsExported && !l.IsRuntime))
        {
            if (IsViolationCollection(let, allLets))
            {
                var desc = docComments.TryGetValue(let.Name, out var d) ? d : "";
                entry.Checks.Add(new CheckEntry { Name = let.Name, Desc = desc });
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
                PredicateCallExpr pc => pc.Name,
                FunctionCallExpr fc => fc.Name,
                _ => null
            };
            if (name is "toError" or "toWarning" or "toInfo")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Scan source for ## doc comments that precede "export let" declarations.
    /// </summary>
    private static void ExtractDocComments(string source, Dictionary<string, string> docComments)
    {
        var lines = source.Split('\n');
        string? pendingComment = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("## "))
            {
                pendingComment = line[3..].Trim();
            }
            else if (pendingComment != null && line.StartsWith("export let "))
            {
                // Extract the name: "export let name = ..."
                var rest = line["export let ".Length..];
                var eqIdx = rest.IndexOf('=');
                var spIdx = rest.IndexOf(' ');
                var endIdx = eqIdx >= 0 ? eqIdx : (spIdx >= 0 ? spIdx : rest.Length);
                var name = rest[..endIdx].Trim();
                if (!string.IsNullOrEmpty(name))
                    docComments.TryAdd(name, pendingComment);
                pendingComment = null;
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                pendingComment = null;
            }
        }
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

            var code = File.ReadAllText(sampleFile).TrimEnd();
            entry.Samples.Add(new SampleEntry { Title = title, Code = code });
        }
    }

    public record PackageManifest(string? Name, string? Description, string? Language, List<string>? Tags);

    private static PackageManifest ReadManifest(string packageDir, string dirName)
    {
        // Look for <dirname>.md with YAML frontmatter
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
