using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public abstract class ISourceParser
{
    public abstract IReadOnlyList<string> Extensions { get; }
    public abstract string Language { get; }
    public abstract SourceFile? Parse(string filePath, string sourceText);
}
