using System.Collections.Concurrent;
using Cop.Core;
using Cop.Lang;
using Cop.Providers.SourceModel;

namespace Cop.Providers.Markdown;

/// <summary>
/// Parses Markdown files into document trees and flat analysis collections.
/// </summary>
public class MarkdownProvider : DataProvider
{
    public override string QueryResultType => "MarkdownContent";
    public override ReadOnlyMemory<byte> GetSchema() => _schema.ToJson();

    private static readonly ProviderSchema _schema = BuildSchema();
    private static readonly RuntimeBindings _bindings = BuildBindings();

    public override Dictionary<string, Func<List<object?>, Task<object?>>>? GetFunctions() => new()
    {
        ["toContent"] = args => Task.FromResult<object?>(ToContent(args.Count == 1 ? args[0] : null))
    };

    // Markdown file extensions
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".markdown", ".mdown", ".mkd" };

    private static ProviderSchema BuildSchema()
    {
        return new ProviderSchema
        {
            Types =
            [
                TypeDef("Heading", null,
                    Prop("Text"), Prop("Level", "int"), Prop("Line", "int"),
                    Opt("File", "File"), Prop("Source")),

                TypeDef("Link", null,
                    Prop("Url"), Opt("Text"), Prop("Line", "int"),
                    Opt("File", "File"), Prop("Source")),

                TypeDef("Section", null,
                    Prop("Heading"), Prop("Level", "int"),
                    Prop("Content"), Prop("StartLine", "int"), Prop("EndLine", "int"),
                    Opt("File", "File"), Prop("Source")),

                TypeDef("FenceBlock", null,
                    Opt("Language"), Opt("Tag"),
                    Prop("StartLine", "int"), Prop("EndLine", "int"),
                    Prop("Content"), Prop("ContentHash"),
                    Opt("File", "File"), Prop("Source")),

                TypeDef("MarkdownNode", null,
                    Prop("Kind"), Prop("Text"), Prop("Content"), Prop("Level", "int"),
                    Prop("StartLine", "int"), Prop("EndLine", "int"),
                    Prop("Children", "[MarkdownNode]"), Opt("File", "File"), Prop("Source")),
            ],
            Collections =
            [
                new() { Name = "Headings", ItemType = "Heading" },
                new() { Name = "Links", ItemType = "Link" },
                new() { Name = "Sections", ItemType = "Section" },
                new() { Name = "FenceBlocks", ItemType = "FenceBlock" },
                new() { Name = "Documents", ItemType = "MarkdownNode" },
            ]
        };
    }

    private static ProviderTypeSchema TypeDef(string name, string? baseType, params ProviderPropertySchema[] props)
        => new() { Name = name, Base = baseType, Properties = [.. props] };
    private static ProviderPropertySchema Prop(string name, string type = "string")
        => new() { Name = name, Type = type };
    private static ProviderPropertySchema Opt(string name, string type = "string")
        => new() { Name = name, Type = type, Optional = true };

    public override RuntimeBindings GetRuntimeBindings() => _bindings;

    private static RuntimeBindings BuildBindings()
    {
        var bindings = CodeBindings.Build();
        bindings.ClrTypeMappings![typeof(HeadingInfo)] = "Heading";
        bindings.ClrTypeMappings[typeof(LinkInfo)] = "Link";
        bindings.ClrTypeMappings[typeof(SectionInfo)] = "Section";
        bindings.ClrTypeMappings[typeof(FenceBlockInfo)] = "FenceBlock";
        bindings.ClrTypeMappings[typeof(MarkdownNodeInfo)] = "MarkdownNode";
        foreach (var (name, accessors) in BuildAccessors())
            bindings.Accessors![name] = accessors;
        foreach (var (name, extractor) in BuildExtractors())
            bindings.CollectionExtractors![name] = extractor;
        return bindings;
    }

    /// <summary>
    /// Reads one Markdown file or scans a directory, returning trees and flat collections.
    /// </summary>
    public override object? Query(ProviderQuery query)
    {
        if (query.RootPath is null)
            return new Dictionary<string, List<object>>();

        var rootPath = query.RootPath;
        var excluded = query.ExcludedDirectories;

        // Collect markdown file paths
        var filePaths = new List<string>();
        if (File.Exists(rootPath))
        {
            if (!MarkdownExtensions.Contains(Path.GetExtension(rootPath)))
                throw new InvalidOperationException($"Not a Markdown file: '{rootPath}'.");
            filePaths.Add(rootPath);
            rootPath = Path.GetDirectoryName(Path.GetFullPath(rootPath))!;
        }
        else
        {
            if (!Directory.Exists(rootPath))
                throw new FileNotFoundException($"Markdown path not found: '{rootPath}'.", rootPath);
            CollectMarkdownFiles(rootPath, excluded, filePaths);
        }

        // Parse files and build SourceFile wrappers for File backlinks
        var parsedFiles = new ConcurrentBag<(SourceFile File, MarkdownDocument Doc)>();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                var text = File.ReadAllText(filePath);
                var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');

                var sourceFile = new SourceFile(relativePath, "markdown", [], [], text)
                {
                    FullPath = Path.GetFullPath(filePath),
                    SourceRoot = Path.GetFullPath(rootPath)
                };

                var doc = MarkdownParser.Parse(text);
                SetFile(doc.Root, sourceFile);
                parsedFiles.Add((sourceFile, doc));
            });

        // Sort for deterministic order
        var sorted = parsedFiles.OrderBy(p => p.File.Path, StringComparer.Ordinal).ToList();

        // Build flat collections
        var headings = new List<object>();
        var links = new List<object>();
        var sections = new List<object>();
        var fenceBlocks = new List<object>();
        var documents = new List<object>();

        foreach (var (file, doc) in sorted)
        {
            headings.AddRange(doc.Headings.Select(h => (object)(h with { File = file })));
            links.AddRange(doc.Links.Select(l => (object)(l with { File = file })));
            sections.AddRange(doc.Sections.Select(s => (object)(s with { File = file })));
            fenceBlocks.AddRange(doc.FenceBlocks.Select(fb => (object)(fb with { File = file })));
            documents.Add(doc.Root);
        }

        var collections = new Dictionary<string, List<object>>();
        var requested = query.Collection;
        if (requested is null || requested == "Headings") collections["Headings"] = headings;
        if (requested is null || requested == "Links") collections["Links"] = links;
        if (requested is null || requested == "Sections") collections["Sections"] = sections;
        if (requested is null || requested == "FenceBlocks") collections["FenceBlocks"] = fenceBlocks;
        if (requested is null || requested == "Documents") collections["Documents"] = documents;

        return ApplyQueryFilters(query, collections);
    }

    private static void CollectMarkdownFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        foreach (var file in Directory.GetFiles(dir))
        {
            if (MarkdownExtensions.Contains(Path.GetExtension(file)))
                result.Add(file);
        }

        foreach (var subDir in Directory.GetDirectories(dir))
        {
            var dirName = Path.GetFileName(subDir);
            if (excluded is not null && excluded.Contains(dirName)) continue;
            if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0) continue;
            CollectMarkdownFiles(subDir, excluded, result);
        }
    }

    private static void SetFile(MarkdownNodeInfo node, SourceFile file)
    {
        node.File = file;
        foreach (var child in node.Children) SetFile(child, file);
    }

    private static DataObject ToContent(object? value)
    {
        if (value is System.Collections.IList { Count: 1 } list) value = list[0];
        if (value is not DataObject parsed)
            throw new ArgumentException("Markdown parsing requires a provider result.");
        var content = new DataObject("MarkdownContent");
        foreach (var name in new[] { "Headings", "Links", "Sections", "FenceBlocks", "Documents" })
            content.Set(name, Wrap(parsed.GetField(name)) ?? new List<object>());
        return content;
    }

    private static object? Wrap(object? value)
    {
        if (value is null or string or bool or int or long or double or DataObject) return value;
        if (value is System.Collections.IEnumerable sequence)
            return sequence.Cast<object?>().Select(Wrap).ToList();
        if (!_bindings.ClrTypeMappings!.TryGetValue(value.GetType(), out var type)
            || !_bindings.Accessors!.TryGetValue(type, out var accessors))
            throw new InvalidOperationException($"Unsupported Markdown model value: {value.GetType().Name}.");
        return new DataObject(type).WithFieldResolver(name =>
        {
            var accessor = accessors.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            return accessor is null ? null : Wrap(accessor(value));
        });
    }

    private static Dictionary<string, Dictionary<string, Func<object, object?>>> BuildAccessors()
    {
        return new()
        {
            ["Heading"] = new()
            {
                ["Text"] = o => ((HeadingInfo)o).Text,
                ["Level"] = o => (object)((HeadingInfo)o).Level,
                ["Line"] = o => (object)((HeadingInfo)o).Line,
                ["File"] = o => ((HeadingInfo)o).File,
                ["Source"] = o => ((HeadingInfo)o).Source,
            },
            ["Link"] = new()
            {
                ["Url"] = o => ((LinkInfo)o).Url,
                ["Text"] = o => ((LinkInfo)o).Text,
                ["Line"] = o => (object)((LinkInfo)o).Line,
                ["File"] = o => ((LinkInfo)o).File,
                ["Source"] = o => ((LinkInfo)o).Source,
            },
            ["Section"] = new()
            {
                ["Heading"] = o => ((SectionInfo)o).Heading,
                ["Level"] = o => (object)((SectionInfo)o).Level,
                ["Content"] = o => ((SectionInfo)o).Content,
                ["StartLine"] = o => (object)((SectionInfo)o).StartLine,
                ["EndLine"] = o => (object)((SectionInfo)o).EndLine,
                ["File"] = o => ((SectionInfo)o).File,
                ["Source"] = o => ((SectionInfo)o).Source,
            },
            ["FenceBlock"] = new()
            {
                ["Language"] = o => ((FenceBlockInfo)o).Language,
                ["Tag"] = o => ((FenceBlockInfo)o).Tag,
                ["StartLine"] = o => (object)((FenceBlockInfo)o).StartLine,
                ["EndLine"] = o => (object)((FenceBlockInfo)o).EndLine,
                ["Content"] = o => ((FenceBlockInfo)o).Content,
                ["ContentHash"] = o => ((FenceBlockInfo)o).ContentHash,
                ["File"] = o => ((FenceBlockInfo)o).File,
                ["Source"] = o => ((FenceBlockInfo)o).Source,
            },
            ["MarkdownNode"] = new()
            {
                ["Kind"] = o => ((MarkdownNodeInfo)o).Kind,
                ["Text"] = o => ((MarkdownNodeInfo)o).Text,
                ["Content"] = o => ((MarkdownNodeInfo)o).Content,
                ["Level"] = o => ((MarkdownNodeInfo)o).Level,
                ["StartLine"] = o => ((MarkdownNodeInfo)o).StartLine,
                ["EndLine"] = o => ((MarkdownNodeInfo)o).EndLine,
                ["Children"] = o => ((MarkdownNodeInfo)o).Children,
                ["File"] = o => ((MarkdownNodeInfo)o).File,
                ["Source"] = o => ((MarkdownNodeInfo)o).Source,
            },
        };
    }

    /// <summary>
    /// Per-document extractors. Kept for Load() backward compatibility.
    /// </summary>
    private static Dictionary<string, Func<object, List<object>>> BuildExtractors()
    {
        return new()
        {
            ["Headings"] = doc =>
            {
                var file = (SourceFile)doc;
                if (file.Language != "markdown") return [];
                var md = MarkdownParser.Parse(file.RawText);
                return md.Headings.Select(h => (object)(h with { File = file })).ToList();
            },
            ["Links"] = doc =>
            {
                var file = (SourceFile)doc;
                if (file.Language != "markdown") return [];
                var md = MarkdownParser.Parse(file.RawText);
                return md.Links.Select(l => (object)(l with { File = file })).ToList();
            },
            ["Sections"] = doc =>
            {
                var file = (SourceFile)doc;
                if (file.Language != "markdown") return [];
                var md = MarkdownParser.Parse(file.RawText);
                return md.Sections.Select(s => (object)(s with { File = file })).ToList();
            },
            ["FenceBlocks"] = doc =>
            {
                var file = (SourceFile)doc;
                if (file.Language != "markdown") return [];
                var md = MarkdownParser.Parse(file.RawText);
                return md.FenceBlocks.Select(fb => (object)(fb with { File = file })).ToList();
            },
            ["Documents"] = doc =>
            {
                var file = (SourceFile)doc;
                if (file.Language != "markdown") return [];
                var root = MarkdownParser.Parse(file.RawText).Root;
                SetFile(root, file);
                return [(object)root];
            },
        };
    }
}
