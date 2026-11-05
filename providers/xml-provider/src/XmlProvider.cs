// Parses XML documents into source-linked element and attribute collections.

using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Linq;
using Cop.Core;
using Cop.Providers.SourceModel;

namespace Cop.Providers.Xml;

/// <summary>
/// Provider for XML document analysis. Scans XML-like project/config files, walks each document
/// recursively, and returns flat element and attribute collections with local names, paths, and lines.
/// </summary>
public sealed class XmlProvider : DataProvider
{
    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => _schema.ToJson();

    private static readonly ProviderSchema _schema = BuildSchema();

    private static readonly HashSet<string> XmlExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".xml", ".csproj", ".props", ".targets", ".config", ".nuspec" };

    private static ProviderSchema BuildSchema()
    {
        return new ProviderSchema
        {
            Types =
            [
                TypeDef("XmlElement", null,
                    Prop("Name"), Prop("Path"), Prop("Value"),
                    Prop("Line", "int"), Opt("File", "File"), Prop("Source")),

                TypeDef("XmlAttribute", null,
                    Prop("Name"), Prop("Value"), Prop("ElementName"), Prop("ElementPath"),
                    Prop("Line", "int"), Opt("File", "File"), Prop("Source")),
            ],
            Collections =
            [
                new() { Name = "Elements", ItemType = "XmlElement" },
                new() { Name = "Attributes", ItemType = "XmlAttribute" },
            ]
        };
    }

    private static ProviderTypeSchema TypeDef(string name, string? baseType, params ProviderPropertySchema[] props)
        => new() { Name = name, Base = baseType, Properties = [.. props] };
    private static ProviderPropertySchema Prop(string name, string type = "string")
        => new() { Name = name, Type = type };
    private static ProviderPropertySchema Opt(string name, string type = "string")
        => new() { Name = name, Type = type, Optional = true };

    /// <inheritdoc />
    public override RuntimeBindings GetRuntimeBindings()
    {
        return new RuntimeBindings
        {
            ClrTypeMappings = new()
            {
                [typeof(XmlElementInfo)] = "XmlElement",
                [typeof(XmlAttributeInfo)] = "XmlAttribute",
                [typeof(SourceFile)] = "File",
            },
            Accessors = BuildAccessors(),
            TextConverters = new()
            {
                ["File"] = o => ((SourceFile)o).Path,
            },
        };
    }

    /// <inheritdoc />
    public override object? Query(ProviderQuery query)
    {
        if (query.RootPath is null)
        {
            return new Dictionary<string, List<object>>();
        }

        string rootPath = query.RootPath;
        IReadOnlySet<string>? excluded = query.ExcludedDirectories;
        List<string> filePaths = new List<string>();
        string relativeRoot = rootPath;

        if (Directory.Exists(rootPath))
        {
            CollectXmlFiles(rootPath, excluded, filePaths);
        }
        else if (File.Exists(rootPath) && XmlExtensions.Contains(Path.GetExtension(rootPath)))
        {
            filePaths.Add(rootPath);
            relativeRoot = Path.GetDirectoryName(rootPath) ?? Environment.CurrentDirectory;
        }

        ConcurrentBag<(SourceFile File, List<XmlElementInfo> Elements, List<XmlAttributeInfo> Attributes)> parsed = new ConcurrentBag<(SourceFile File, List<XmlElementInfo> Elements, List<XmlAttributeInfo> Attributes)>();
        Parallel.ForEach(filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            filePath =>
            {
                try
                {
                    string text = File.ReadAllText(filePath);
                    string relativePath = Path.GetRelativePath(relativeRoot, filePath).Replace('\\', '/');
                    SourceFile sourceFile = new SourceFile(relativePath, "xml", [], [], text);
                    (List<XmlElementInfo>? elements, List<XmlAttributeInfo>? attributes) = ParseDocument(text);
                    ExecutionStatistics.RecordInputFile(filePath);
                    parsed.Add((sourceFile, elements, attributes));
                }
                catch { }
            });

        List<(SourceFile File, List<XmlElementInfo> Elements, List<XmlAttributeInfo> Attributes)> sorted = parsed.OrderBy(p => p.File.Path, StringComparer.Ordinal).ToList();

        List<object> elements = new List<object>();
        List<object> attributes = new List<object>();
        foreach ((SourceFile? file, List<XmlElementInfo>? fileElements, List<XmlAttributeInfo>? fileAttributes) in sorted)
        {
            elements.AddRange(fileElements.Select(e => (object)(e with { File = file })));
            attributes.AddRange(fileAttributes.Select(a => (object)(a with { File = file })));
        }

        Dictionary<string, List<object>> collections = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        string? requested = query.Collection;
        if (requested is null || requested == "Elements")
        {
            collections["Elements"] = elements;
        }

        if (requested is null || requested == "Attributes")
        {
            collections["Attributes"] = attributes;
        }

        return ApplyQueryFilters(query, collections);
    }

    private static (List<XmlElementInfo> Elements, List<XmlAttributeInfo> Attributes) ParseDocument(string text)
    {
        XmlReaderSettings settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };

        using XmlReader reader = XmlReader.Create(new StringReader(text), settings);
        XDocument document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        List<XmlElementInfo> elements = new List<XmlElementInfo>();
        List<XmlAttributeInfo> attributes = new List<XmlAttributeInfo>();

        if (document.Root is not null)
        {
            WalkElement(document.Root, null, elements, attributes);
        }

        return (elements, attributes);
    }

    private static void WalkElement(XElement element, string? parentPath, List<XmlElementInfo> elements, List<XmlAttributeInfo> attributes)
    {
        string name = element.Name.LocalName;
        string path = parentPath is null ? name : $"{parentPath}.{name}";
        int line = GetLine(element);
        string value = GetDirectText(element);

        elements.Add(new XmlElementInfo(name, path, value, line));

        foreach (XAttribute attribute in element.Attributes())
        {
            int attributeLine = GetLine(attribute);
            if (attributeLine == 0)
            {
                attributeLine = line;
            }

            attributes.Add(new XmlAttributeInfo(attribute.Name.LocalName, attribute.Value, name, path, attributeLine));
        }

        foreach (XElement child in element.Elements())
        {
            WalkElement(child, path, elements, attributes);
        }
    }

    private static string GetDirectText(XElement element)
        => string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();

    private static int GetLine(IXmlLineInfo lineInfo)
        => lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;

    private static void CollectXmlFiles(string dir, IReadOnlySet<string>? excluded, List<string> result)
    {
        try
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                if (XmlExtensions.Contains(Path.GetExtension(file)))
                {
                    result.Add(file);
                }
            }
            foreach (string subDir in Directory.GetDirectories(dir))
            {
                string dirName = Path.GetFileName(subDir);
                if (excluded is not null && excluded.Contains(dirName))
                {
                    continue;
                }

                CollectXmlFiles(subDir, excluded, result);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static Dictionary<string, Dictionary<string, Func<object, object?>>> BuildAccessors()
    {
        return new()
        {
            ["XmlElement"] = new()
            {
                ["Name"] = o => ((XmlElementInfo)o).Name,
                ["Path"] = o => ((XmlElementInfo)o).Path,
                ["Value"] = o => ((XmlElementInfo)o).Value,
                ["Line"] = o => (object)((XmlElementInfo)o).Line,
                ["File"] = o => ((XmlElementInfo)o).File,
                ["Source"] = o => ((XmlElementInfo)o).Source,
            },
            ["XmlAttribute"] = new()
            {
                ["Name"] = o => ((XmlAttributeInfo)o).Name,
                ["Value"] = o => ((XmlAttributeInfo)o).Value,
                ["ElementName"] = o => ((XmlAttributeInfo)o).ElementName,
                ["ElementPath"] = o => ((XmlAttributeInfo)o).ElementPath,
                ["Line"] = o => (object)((XmlAttributeInfo)o).Line,
                ["File"] = o => ((XmlAttributeInfo)o).File,
                ["Source"] = o => ((XmlAttributeInfo)o).Source,
            },
            ["File"] = new()
            {
                ["Path"] = o => ((SourceFile)o).Path,
                ["Language"] = o => ((SourceFile)o).Language,
                ["Namespace"] = o => ((SourceFile)o).Namespace,
                ["Usings"] = o => (object)((SourceFile)o).Usings,
                ["Types"] = o => (object)((SourceFile)o).Types,
                ["Projects"] = o => (object)((SourceFile)o).Projects,
            },
        };
    }
}
