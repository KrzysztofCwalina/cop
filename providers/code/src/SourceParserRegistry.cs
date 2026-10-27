namespace Cop.Providers.SourceParsers;

public class SourceParserRegistry
{
    private readonly Dictionary<string, ISourceParser> _parsers = new(StringComparer.OrdinalIgnoreCase);
    internal System.Reflection.Assembly CacheAssembly =>
        _parsers.Values.FirstOrDefault()?.GetType().Assembly ?? typeof(SourceParserRegistry).Assembly;

    public void Register(ISourceParser parser)
    {
        foreach (var ext in parser.Extensions)
            _parsers[ext] = parser;
    }

    public ISourceParser? GetParser(string fileExtension) =>
        _parsers.TryGetValue(fileExtension, out var parser) ? parser : null;
}
