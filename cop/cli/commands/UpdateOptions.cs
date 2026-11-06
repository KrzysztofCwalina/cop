// Selects the installation source and replacement policy for a Cop update.

namespace Cop.Cli.Commands;

internal sealed class UpdateOptions
{
    /// <summary>Whether to reinstall even when the running version is current.</summary>
    public bool IsForced { get; init; }

    /// <summary>Whether to install the running published executable without downloading.</summary>
    public bool IsLocal { get; init; }
}
