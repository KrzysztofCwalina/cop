// Per-run context. Ordinary, full-scope runs never need to discover Git.

namespace Cop.Core;

/// <summary>Per-run context. Ordinary, full-scope runs never need to discover Git.</summary>
public sealed class AnalysisContext
{
    private readonly Lazy<ChangeSnapshot> changes;

    public AnalysisContext(string rootPath, ReviewOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RootPath = Path.GetFullPath(rootPath);
        Options = options;
        Action<string> logger = log ?? (_ => { });
        changes = new Lazy<ChangeSnapshot>(() => new ChangeSnapshot(RootPath, Options, logger));
    }

    public string RootPath { get; }
    public ReviewOptions Options { get; }
    public ChangeSnapshot Changes => changes.Value;

    public void AssertUnchanged()
    {
        if (changes.IsValueCreated)
        {
            changes.Value.AssertUnchanged();
        }
    }
}
