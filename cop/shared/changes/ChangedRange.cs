// The old and new line ranges represented by a diff hunk.

namespace Cop.Core;

/// <summary>
/// The old and new line ranges represented by a diff hunk.
/// </summary>
public sealed record ChangedRange(int OldStart, int OldCount, int NewStart, int NewCount);
