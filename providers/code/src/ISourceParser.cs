// Defines source-parser capabilities and cache identities.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>
/// The source-parser contract used to populate language-independent models.
/// </summary>
public abstract class ISourceParser
{
    /// <summary>
    /// The source-file extensions recognized by this parser.
    /// </summary>
    public abstract IReadOnlyList<string> Extensions { get; }
    /// <summary>
    /// The language identifier assigned to parsed source files.
    /// </summary>
    public abstract string Language { get; }
    /// <summary>
    /// True when parsing can attach members declared in another file.
    /// </summary>
    public virtual bool SupportsCrossFileMembers => false;
    /// <summary>
    /// Reports whether this parser can reuse cached syntax for a file.
    /// </summary>
    public virtual bool CanCache(string filePath) => true;
    /// <summary>Called on file demand before cache lookup; include backend and relevant configuration identity.</summary>
    public virtual string GetCacheIdentity(string filePath) =>
        GetType().FullName + ":" + GetType().Assembly.ManifestModule.ModuleVersionId;
    /// <summary>
    /// Parses supplied source text into a file model or returns null.
    /// </summary>
    public abstract ValueTask<SourceFile?> ParseAsync(
        string filePath, string sourceText, CancellationToken cancellationToken = default);
}
