// Process execution contract used by source-change discovery.

using System.Diagnostics;
using System.Text;

namespace Cop.Core;

/// <summary>
/// Process execution contract used by source-change discovery.
/// </summary>
internal abstract class ChangeCommandRunner
{
    /// <summary>
    /// Runs a command in the given directory and captures its exit code and streams.
    /// </summary>
    public abstract ValueTask<ChangeCommandResult> RunAsync(string directory, string executable,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}
