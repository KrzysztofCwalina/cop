// Process execution contract used by source-change discovery.

using System.Diagnostics;
using System.Text;

namespace Cop.Core;

/// <summary>
/// Process execution contract used by source-change discovery.
/// </summary>
internal abstract class ChangeCommandRunner
{
    public abstract ChangeCommandResult Run(string directory, string executable, params string[] arguments);
}
