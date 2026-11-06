// Selects registered source parsers by file extension.

namespace Cop.Providers.SourceParsers;

/// <summary>
/// Selects registered source parsers by file extension.
/// </summary>
public class SourceParserRegistry
{
    private readonly Dictionary<string, ISourceParser> _parsers = new(StringComparer.OrdinalIgnoreCase);
    internal System.Reflection.Assembly CacheAssembly =>
        _parsers.Values.FirstOrDefault()?.GetType().Assembly ?? typeof(SourceParserRegistry).Assembly;

    /// <summary>
    /// Registers a parser for its advertised file extensions.
    /// </summary>
    public void Register(ISourceParser parser)
    {
        foreach (string ext in parser.Extensions)
        {
            _parsers[ext] = parser;
        }
    }

    /// <summary>
    /// Finds the registered parser for a source-file extension.
    /// </summary>
    public ISourceParser? GetParser(string fileExtension) =>
        _parsers.TryGetValue(fileExtension, out ISourceParser? parser) ? parser : null;
}
