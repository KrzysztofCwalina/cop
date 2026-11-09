// Captures the execution counters reported at a point in a program run.

namespace Cop.Core;

/// <summary>
/// Captures demanded inputs, evaluated bindings and entry commands, and elapsed run time.
/// </summary>
public sealed record ExecutionSummary(int InputFiles, int CommandsExecuted, TimeSpan Elapsed)
{
    /// <summary>Counts of distinct explicitly identified evaluations, grouped by category.</summary>
    public IReadOnlyDictionary<string, int> Evaluations { get; init; } = new Dictionary<string, int>();
}
