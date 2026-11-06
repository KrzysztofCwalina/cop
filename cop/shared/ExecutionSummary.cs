// Captures the execution counters reported at a point in a program run.

namespace Cop.Core;

/// <summary>
/// Captures demanded input-file count, completed commands, and elapsed run time.
/// </summary>
public sealed record ExecutionSummary(int InputFiles, int CommandsExecuted, TimeSpan Elapsed);
