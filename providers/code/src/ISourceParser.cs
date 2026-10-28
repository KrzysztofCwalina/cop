using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public abstract class ISourceParser
{
    public abstract IReadOnlyList<string> Extensions { get; }
    public abstract string Language { get; }
    /// <summary>Called on file demand before cache lookup; include backend and relevant configuration identity.</summary>
    public virtual string GetCacheIdentity(string filePath) =>
        GetType().FullName + ":" + GetType().Assembly.ManifestModule.ModuleVersionId;
    public abstract SourceFile? Parse(string filePath, string sourceText);
}
