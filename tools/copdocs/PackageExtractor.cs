// Extracts reference entries from package manifests, source declarations, and samples.

namespace Cop.Tools.Docs;

using Cop.Core;
using Cop.Lang;

/// <summary>
/// Extracts documentation data from a Cop package directory.
/// </summary>
public class PackageExtractor
{
    private readonly string? _repoBaseUrl;
    private readonly string? _repoRoot;

    /// <summary>Creates an extractor with optional links to source files in a repository.</summary>
    /// <param name="repoBaseUrl">The repository URL prefix used for source links.</param>
    /// <param name="repoRoot">The local repository root used to resolve source-relative paths.</param>
    public PackageExtractor(string? repoBaseUrl = null, string? repoRoot = null)
    {
        _repoBaseUrl = repoBaseUrl?.TrimEnd('/');
        _repoRoot = repoRoot != null ? Path.GetFullPath(repoRoot) : null;
    }

    private string? MakeSourceUrl(string filePath, int line)
    {
        if (_repoBaseUrl == null || _repoRoot == null)
        {
            return null;
        }

        string fullPath = Path.GetFullPath(filePath);
        if (!fullPath.StartsWith(_repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string relativePath = fullPath[_repoRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        relativePath = relativePath.Replace('\\', '/');
        return $"{_repoBaseUrl}/{relativePath}#L{line}";
    }

    /// <summary>
    /// Extracts package documentation from a directory containing a manifest .md and src/*.cop files.
    /// </summary>
    /// <param name="packageDir">The package directory to inspect.</param>
    /// <returns>The package's reference entry, including declarations and samples.</returns>
    public PackageEntry Extract(string packageDir)
    {
        PackageEntry entry = new PackageEntry();
        string dirName = Path.GetFileName(packageDir);

        // Read manifest
        PackageManifest manifest = ReadManifest(packageDir, dirName);
        entry.Label = manifest.Name ?? dirName;
        entry.Overview = manifest.Description ?? "";

        // Parse all .cop files in src/
        string srcDir = Path.Combine(packageDir, "src");
        string[] copFiles = Directory.Exists(srcDir)
            ? Directory.GetFiles(srcDir, "*.cop")
            : [];

        List<(LetDeclaration Let, string FilePath)> allLets = new List<(LetDeclaration Let, string FilePath)>();

        foreach (string copFile in copFiles)
        {
            string source = File.ReadAllText(copFile);

            try
            {
                ScriptFile scriptFile = Cop.Lang.Parser.CopParser.ParseFile(source, copFile);
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
                // Skip files that fail to parse; remaining package files still contribute docs.
            }
        }

        // Extract checks from let declarations
        ExtractChecks(allLets, entry);

        // Load samples
        LoadSamples(packageDir, entry);

        return entry;
    }

    private void ExtractTypes(ScriptFile sf, PackageEntry entry, string copFile)
    {
        foreach (TypeDefinition? td in sf.TypeDefinitions.Where(t => t.IsExported))
        {
            // Conformance-only declarations (type int : Comparable = {}) — add to conformers list
            if (td.BaseType is not null && td.Properties.Count == 0
                && entry.Types.TryGetValue(td.BaseType, out TypeEntry? baseEntry) && baseEntry.IsTrait)
            {
                baseEntry.Conformers ??= [];
                baseEntry.Conformers.Add(td.Name);
                // Also record the trait on the concrete type
                if (entry.Types.TryGetValue(td.Name, out TypeEntry? concreteEntry))
                {
                    concreteEntry.Traits ??= [];
                    concreteEntry.Traits.Add(td.BaseType);
                }
                // Handle additional traits from Traits list
                if (td.Traits is not null)
                {
                    foreach (string traitName in td.Traits)
                    {
                        if (entry.Types.TryGetValue(traitName, out TypeEntry? traitEntry) && traitEntry.IsTrait)
                        {
                            traitEntry.Conformers ??= [];
                            traitEntry.Conformers.Add(td.Name);
                            if (entry.Types.TryGetValue(td.Name, out TypeEntry? ce))
                            {
                                ce.Traits ??= [];
                                ce.Traits.Add(traitName);
                            }
                        }
                    }
                }
                continue;
            }

            // Subtype declarations (type string : object = {}) — record base type on the sub
            if (td.BaseType is not null && td.Properties.Count == 0 && entry.Types.ContainsKey(td.Name))
            {
                // Already exists — just set the base type
                entry.Types[td.Name].BaseType = td.BaseType;
                // Handle traits from Traits list
                if (td.Traits is not null)
                {
                    foreach (string traitName in td.Traits)
                    {
                        if (entry.Types.TryGetValue(traitName, out TypeEntry? traitEntry) && traitEntry.IsTrait)
                        {
                            traitEntry.Conformers ??= [];
                            traitEntry.Conformers.Add(td.Name);
                            entry.Types[td.Name].Traits ??= [];
                            entry.Types[td.Name].Traits!.Add(traitName);
                        }
                    }
                }
                continue;
            }

            TypeEntry typeEntry = new TypeEntry { Desc = td.DocComment, SourceUrl = MakeSourceUrl(copFile, td.Line) };
            // Detect traits: types whose properties are all function-typed (contain =>)
            bool hasFunctionProps = td.Properties.Count > 0 && td.Properties.All(p => p.TypeName.Contains("=>"));
            typeEntry.IsTrait = hasFunctionProps;
            if (td.BaseType is not null)
            {
                typeEntry.BaseType = td.BaseType;
            }
            // Handle traits from Traits list on new type definitions
            if (td.Traits is not null)
            {
                typeEntry.Traits ??= [];
                typeEntry.Traits.AddRange(td.Traits);
            }

            foreach (PropertyDefinition prop in td.Properties)
            {
                string typeName = prop.IsCollection ? $"[{prop.TypeName}]" : prop.TypeName;
                if (prop.IsOptional)
                {
                    typeName += "?";
                }

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
        foreach (PredicateDefinition? pd in sf.Predicates.Where(p => p.IsExported))
        {
            entry.Predicates.Add(new PredicateEntry
            {
                Name = pd.Name,
                AppliesTo = pd.ParameterType,
                Narrows = pd.NarrowedType,
                Desc = pd.DocComment ?? "",
                SourceUrl = MakeSourceUrl(copFile, pd.Line)
            });
        }
    }

    private void ExtractFunctions(ScriptFile sf, PackageEntry entry, string copFile)
    {
        foreach (FunctionDefinition? fd in sf.Functions.Where(f => f.IsExported))
        {
            List<string> parts = new List<string>();
            string? appliesTo = null;
            // The first parameter is stored as InputType/InputName in the AST.
            // Show it as a collection method target (appliesTo) only when it's a collection type.
            // Otherwise include it as a regular named parameter.
            if (!string.IsNullOrEmpty(fd.InputType) && fd.InputType.StartsWith('['))
            {
                string inputLabel = !string.IsNullOrEmpty(fd.InputName)
                    ? $"{fd.InputName}: {fd.InputType}"
                    : fd.InputType;
                parts.Add(inputLabel);
                appliesTo = fd.InputType;
            }
            else if (!string.IsNullOrEmpty(fd.InputType))
            {
                string inputLabel = !string.IsNullOrEmpty(fd.InputName)
                    ? $"{fd.InputName}: {fd.InputType}"
                    : fd.InputType;
                parts.Add(inputLabel);
            }
            parts.AddRange(fd.Parameters.Select(p => $"{p.Name}: {p.TypeName}"));
            string paramStr = string.Join(", ", parts);
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
        foreach (CollectionDeclaration? cd in sf.CollectionDeclarations.Where(c => c.IsExported))
        {
            entry.Collections.Add(cd.Name);
        }
    }

    private void ExtractEnums(ScriptFile sf, PackageEntry entry, string copFile)
    {
        if (sf.EnumDefinitions is { Count: > 0 })
        {
            entry.Enums ??= [];
            foreach (EnumDefinition? ed in sf.EnumDefinitions.Where(e => e.IsExported))
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
            foreach (FlagsDefinition? fd in sf.FlagsDefinitions.Where(f => f.IsExported))
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
        foreach (CommandBlock? cmd in sf.Commands.Where(c => c.IsExported && c.IsCommand))
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
        List<LetDeclaration> lets = allLets.Select(l => l.Let).ToList();
        foreach ((LetDeclaration? let, string? filePath) in allLets.Where(l => l.Let.IsExported))
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
        => IsViolationCollection(let, allLets, []);

    private static bool IsViolationCollection(LetDeclaration let, List<LetDeclaration> allLets, HashSet<string> visited)
    {
        if (!visited.Add(let.Name))
        {
            return false; // guard against reference cycles
        }

        // Common form: `let x = cb.Calls:isFoo:toWarning('...')` — the whole filter/transform
        // chain is the ValueExpression and ends in a toError/toWarning/toInfo call.
        if (let.ValueExpression is not null)
        {
            return ContainsViolationCall(let.ValueExpression, allLets, visited);
        }

        // Legacy filter-chain form where filters are stored separately.
        return let.Filters.Any(f => ContainsViolationCall(f, allLets, visited));
    }

    /// <summary>
    /// True if the expression is (or contains) a toError/toWarning/toInfo call, following
    /// references to other exported violation collections (for `a + b + c` unions).
    /// </summary>
    private static bool ContainsViolationCall(Expression expr, List<LetDeclaration> allLets, HashSet<string> visited)
    {
        switch (expr)
        {
            case AstExpressionWrapper wrapper:
                return ContainsViolationCallAst(wrapper.AstExpression, allLets, visited);
            case CallExpr call:
                if (call.Name is "toError" or "toWarning" or "toInfo")
                {
                    return true;
                }

                if (call.Target is not null && ContainsViolationCall(call.Target, allLets, visited))
                {
                    return true;
                }

                return call.Args.Any(a => ContainsViolationCall(a, allLets, visited));
            case CollectionUnionExpr union:
                return union.Elements.Any(e => ContainsViolationCall(e, allLets, visited));
            case BinaryExpr bin:
                return ContainsViolationCall(bin.Left, allLets, visited) || ContainsViolationCall(bin.Right, allLets, visited);
            case UnaryExpr u:
                return ContainsViolationCall(u.Operand, allLets, visited);
            case MemberAccessExpr m:
                return ContainsViolationCall(m.Target, allLets, visited);
            case PathScopedExpr p:
                return ContainsViolationCall(p.Inner, allLets, visited);
            case IdentifierExpr id:
                LetDeclaration? constituent = allLets.FirstOrDefault(l => l.Name == id.Name);
                return constituent != null && IsViolationCollection(constituent, allLets, visited);
            default:
                return false;
        }
    }

    /// <summary>
    /// Scans the raw parser AST (Cop.Lang.Ast.Expression) produced by the new parser and
    /// wrapped in AstExpressionWrapper. Detects toError/toWarning/toInfo filter-calls and
    /// follows identifier references to other exported violation collections (`a + b + c`).
    /// </summary>
    private static bool ContainsViolationCallAst(Cop.Lang.Ast.Expression expr, List<LetDeclaration> allLets, HashSet<string> visited)
    {
        switch (expr)
        {
            case Cop.Lang.Ast.CallExpr call:
                if (call.Callee is Cop.Lang.Ast.IdentifierExpr cid && cid.Name is "toError" or "toWarning" or "toInfo")
                {
                    return true;
                }

                if (call.Callee is Cop.Lang.Ast.MemberExpr cm && cm.Member is "toError" or "toWarning" or "toInfo")
                {
                    return true;
                }

                if (ContainsViolationCallAst(call.Callee, allLets, visited))
                {
                    return true;
                }

                return call.Args.Any(a => ContainsViolationCallAst(a, allLets, visited));
            case Cop.Lang.Ast.FilterExpr filter:
                return ContainsViolationCallAst(filter.Collection, allLets, visited)
                    || ContainsViolationCallAst(filter.Predicate, allLets, visited);
            case Cop.Lang.Ast.BinaryExpr bin:
                return ContainsViolationCallAst(bin.Left, allLets, visited) || ContainsViolationCallAst(bin.Right, allLets, visited);
            case Cop.Lang.Ast.UnaryExpr u:
                return ContainsViolationCallAst(u.Operand, allLets, visited);
            case Cop.Lang.Ast.MemberExpr m:
                return ContainsViolationCallAst(m.Object, allLets, visited);
            case Cop.Lang.Ast.IndexExpr ix:
                return ContainsViolationCallAst(ix.Object, allLets, visited) || ContainsViolationCallAst(ix.Index, allLets, visited);
            case Cop.Lang.Ast.ConditionalExpr c:
                return ContainsViolationCallAst(c.Then, allLets, visited) || ContainsViolationCallAst(c.Else, allLets, visited);
            case Cop.Lang.Ast.ListExpr list:
                return list.Elements.Any(e => ContainsViolationCallAst(e, allLets, visited));
            case Cop.Lang.Ast.IdentifierExpr id:
                LetDeclaration? constituent = allLets.FirstOrDefault(l => l.Name == id.Name);
                return constituent != null && IsViolationCollection(constituent, allLets, visited);
            default:
                return false;
        }
    }

    private static void LoadSamples(string packageDir, PackageEntry entry)
    {
        string samplesDir = Path.Combine(packageDir, "samples");
        if (!Directory.Exists(samplesDir))
        {
            return;
        }

        foreach (string? sampleFile in Directory.GetFiles(samplesDir, "*.cop").OrderBy(f => f))
        {
            string title = Path.GetFileNameWithoutExtension(sampleFile).Replace('-', ' ').Replace('_', ' ');
            // Capitalize first letter
            if (title.Length > 0)
            {
                title = char.ToUpper(title[0]) + title[1..];
            }

            string code = File.ReadAllText(sampleFile).ReplaceLineEndings("\n").TrimEnd();
            entry.Samples.Add(new SampleEntry { Title = title, Code = code });
        }
    }

    /// <summary>The manifest fields used to label and describe a package in the reference.</summary>
    /// <param name="Name">The manifest's package name.</param>
    /// <param name="Description">The package overview text.</param>
    /// <param name="Language">The source language associated with the package.</param>
    /// <param name="Tags">The package's discovery tags.</param>
    public record PackageManifest(string? Name, string? Description, string? Language, List<string>? Tags);

    private static PackageManifest ReadManifest(string packageDir, string dirName)
    {
        // Prefer cop.json
        string jsonFile = Path.Combine(packageDir, Cop.Core.PackageMetadata.MetadataFileName);
        if (File.Exists(jsonFile))
        {
            try
            {
                PackageMetadata metadata = Cop.Core.PackageMetadata.ParseFromJsonFile(jsonFile);
                return new PackageManifest(
                    metadata.Name,
                    metadata.Description,
                    metadata.Language,
                    metadata.Tags.Count > 0 ? metadata.Tags : null);
            }
            catch { /* fall through to legacy */ }
        }

        // Legacy fallback: <dirname>.md with YAML frontmatter
        string? mdFile = Path.Combine(packageDir, $"{dirName}.md");
        if (!File.Exists(mdFile))
        {
            // Try any .md file in the directory
            string[] mdFiles = Directory.GetFiles(packageDir, "*.md");
            mdFile = mdFiles.FirstOrDefault(f => !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase));
            if (mdFile == null)
            {
                return new PackageManifest(dirName, null, null, null);
            }
        }

        string content = File.ReadAllText(mdFile);
        return ParseFrontmatter(content, dirName);
    }

    private static PackageManifest ParseFrontmatter(string content, string fallbackName)
    {
        if (!content.StartsWith("---"))
        {
            return new PackageManifest(fallbackName, null, null, null);
        }

        int endIdx = content.IndexOf("---", 3);
        if (endIdx < 0)
        {
            return new PackageManifest(fallbackName, null, null, null);
        }

        string yaml = content[3..endIdx];
        string? name = null, description = null, language = null;
        List<string>? tags = null;

        foreach (string line in yaml.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("name:"))
            {
                name = trimmed["name:".Length..].Trim();
            }
            else if (trimmed.StartsWith("description:"))
            {
                description = trimmed["description:".Length..].Trim();
            }
            else if (trimmed.StartsWith("language:"))
            {
                language = trimmed["language:".Length..].Trim();
            }
            else if (trimmed.StartsWith("tags:"))
            {
                string tagStr = trimmed["tags:".Length..].Trim();
                tags = tagStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
        }

        return new PackageManifest(name ?? fallbackName, description, language, tags);
    }

    /// <summary>
    /// Determines the category for a package from the top-level folder it lives in under
    /// packages/ (e.g. checks, core, formats, languages, tools), so the reference doc's
    /// grouping mirrors the repository's folder layout. Returns a title-cased display name.
    /// </summary>
    /// <param name="packageDir">The package directory whose parent supplies the category.</param>
    /// <returns>The title-cased category name, or Misc when no parent group exists.</returns>
    public static string GetCategory(string packageDir)
    {
        string? parent = Path.GetDirectoryName(packageDir);
        string? group = parent != null ? Path.GetFileName(parent) : null;
        if (string.IsNullOrEmpty(group))
        {
            return "Misc";
        }

        return char.ToUpperInvariant(group[0]) + group[1..];
    }

    /// <summary>Reads the package's metadata from its JSON or legacy Markdown manifest.</summary>
    /// <param name="packageDir">The package directory containing its manifest.</param>
    /// <returns>The parsed manifest fields, with the directory name as a fallback package name.</returns>
    public PackageManifest GetManifest(string packageDir)
    {
        return ReadManifest(packageDir, Path.GetFileName(packageDir));
    }
}
