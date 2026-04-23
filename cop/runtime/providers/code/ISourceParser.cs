using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public interface ISourceParser
{
    IReadOnlyList<string> Extensions { get; }
    string Language { get; }
    SourceFile? Parse(string filePath, string sourceText);
}
