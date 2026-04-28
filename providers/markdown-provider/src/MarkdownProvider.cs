using System.Collections.Concurrent;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.Markdown;

/// <summary>
/// Provider for markdown document analysis (Headings, Links, Sections, FenceBlocks).
/// Scans .md files, parses them, and returns flat CLR object collections.
/// </summary>
public class MarkdownProvider : DataProvider
{
    public override DataFormat SupportedFormats => DataFormat.ObjectCollections;

    public override ReadOnlyMemory<byte> GetSchema() => _schema.ToJson();

    private static readonly ProviderSchema _schema = BuildSchema();

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
            ],
            Collections =
            [
                new() { Name = "Headings", ItemType = "Heading" },
                new() { Name = "Links", ItemType = "Link" },
                new() { Name = "Sections", ItemType = "Section" },
                new() { Name = "FenceBlocks", ItemType = "FenceBlock" },
            ]
        };
    }

    private static ProviderTypeSchema TypeDef(string name, string? baseType, params ProviderPropertySchema[] props)
        => new() { Name = name, Base = baseType, Properties = [.. props] };
    private static ProviderPropertySchema Prop(string name, string type = "string")
        => new() { Name = name, Type = type };
    private static ProviderPropertySchema Opt(string name, string type = "string")
        => new() { Name = name, Type = type, Optional = true };

    public override RuntimeBindings GetRuntimeBindings()
    {
        return new RuntimeBindings
        {
            ClrTypeMappings = new()
            {
                [typeof(HeadingInfo)] = "Heading",
                [typeof(LinkInfo)] = "Link",
                [typeof(SectionInfo)] = "Section",
                [typeof(FenceBlockInfo)] = "FenceBlock",
            },
            Accessors = BuildAccessors(),
            CollectionExtractors = BuildExtractors(),
        };
    }

    /// <summary>
    /// Scans for .md files, parses them, and returns flat collections.
    /// </summary>
    public override Dictionary<string, List<object>>? QueryCollections(ProviderQuery query)
    {
        if (query.RootPath is null)
            return new();

        var rootPath = query.RootPath;
        var excluded = query.ExcludedDirectories;

        // Collect markdown file paths
        var filePaths = new List<string>();
        CollectMarkdownFiles(rootPath, excluded, filePaths);

        // Parse files and build SourceFile wrappers for File backlinks
        var parsedFiles = new ConcurrentBag<(SourceFile File, MarkdownDocument Doc)>();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                try
                {
                    var text = File.ReadAllText(filePath);
                    var relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');

                    var sourceFile = new SourceFile(relativePath, "markdown", [], [], text);

                    var doc = MarkdownParser.Parse(text);
                    parsedFiles.Add((sourceFile, doc));
                }
                catch { }
            });

        // Sort for deterministic order
        var sorted = parsedFiles.OrderBy(p => p.File.Path, StringComparer.Ordinal).ToList();

        // Build flat collections
        var headings = new List<object>();
        var links = new List<object>();
        var sections = new List<object>();
        var fenceBlocks = new List<object>();

        foreach (var (file, doc) in sorted)
        {
            headings.AddRange(doc.Headings.Select(h => (object)(h with { File = file })));
            links.AddRange(doc.Links.Select(l => (object)(l with { File = file })));
            sections.AddRange(doc.Sections.Select(s => (object)(s with { File = file })));
            fenceBlocks.AddRange(doc.FenceBlocks.Select(fb => (object)(fb with { File = file })));
        }

        var collections = new Dictionary<string, List<object>>();
        var requested = query.RequestedCollections;
        if (requested is null || requested.Contains("Headings")) collections["Headings"] = headings;
        if (requested is null || requested.Contains("Links")) collections["Links"] = links;
        if (requested is null || requested.Contains("Sections")) collections["Sections"] = sections;
        if (requested is null || requested.Contains("FenceBlocks")) collections["FenceBlocks"] = fenceBlocks;

        return collections;
    }

    private static void CollectMarkdownFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
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
                CollectMarkdownFiles(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
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
        };
    }
}
