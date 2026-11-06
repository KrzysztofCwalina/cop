// Captures changes asynchronously only when a change-scoped analysis demands them.

namespace Cop.Core;

/// <summary>Per-run context; ordinary full-scope runs never need to discover Git.</summary>
public sealed class AnalysisContext
{
    private readonly System.Threading.Lock _gate = new();
    private readonly Action<string> _logger;
    private Lazy<Task<ChangeSnapshot>>? _changes;

    public AnalysisContext(string rootPath, ReviewOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RootPath = Path.GetFullPath(rootPath);
        Options = options;
        _logger = log ?? (_ => { });
    }

    /// <summary>The absolute target directory used for analysis and change discovery.</summary>
    public string RootPath { get; }

    /// <summary>The review scope and configuration selected for this analysis.</summary>
    public ReviewOptions Options { get; }

    /// <summary>Awaits lazy, shared capture of the validated change snapshot.</summary>
    public ValueTask<ChangeSnapshot> GetChangesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Lazy<Task<ChangeSnapshot>> changes;
        lock (_gate)
        {
            changes = _changes ??= new(() =>
                ChangeSnapshot.CaptureAsync(RootPath, Options, _logger, cancellationToken).AsTask());
        }
        return new(changes.Value.WaitAsync(cancellationToken));
    }

    /// <summary>Rejects results if an already-demanded snapshot has become stale.</summary>
    public async ValueTask AssertUnchangedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Lazy<Task<ChangeSnapshot>>? changes;
        lock (_gate)
        {
            changes = _changes;
        }
        if (changes is { IsValueCreated: true })
        {
            ChangeSnapshot snapshot = await changes.Value.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            await snapshot.AssertUnchangedAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
