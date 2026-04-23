namespace Cop.Providers.SourceParsers;

public class SourceParserRegistry
{
    private readonly Dictionary<string, ISourceParser> _parsers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ISourceParser parser)
    {
        foreach (var ext in parser.Extensions)
            _parsers[ext] = parser;
    }

    public ISourceParser? GetParser(string fileExtension) =>
        _parsers.TryGetValue(fileExtension, out var parser) ? parser : null;

    /// <summary>
    /// Creates a registry with core parsers (C# and plain text).
    /// Language-specific parsers (Python, JavaScript) can be added via RegisterOptionalParsers().
    /// </summary>
    public static SourceParserRegistry CreateDefault()
    {
        var registry = new SourceParserRegistry();
        registry.Register(new CSharpSourceParser());
        registry.Register(new TextFileParser());
        registry.RegisterOptionalParsers();
        return registry;
    }

    /// <summary>
    /// Registers optional language parsers that are available in the runtime.
    /// These are isolated in their own provider subfolders and will eventually
    /// become separate loadable packages.
    /// </summary>
    public void RegisterOptionalParsers()
    {
        Register(new PythonSourceParser());
        Register(new JavaScriptSourceParser());
    }
}
