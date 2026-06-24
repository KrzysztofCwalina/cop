namespace Cop.Cli.Commands;

/// <summary>
/// Decides whether the interactive startup notices (the once-a-day "update available" reminder
/// and the post-update "what's new" summary, both produced by <see cref="VersionNotifier"/>)
/// should be shown for a given invocation.
///
/// The notices must never appear for a command that produces its own output (help, version,
/// update) or — crucially — for an unknown/misspelled command. Showing them before resolving the
/// command meant a typo like <c>cop updater</c> printed a stale "what's new" summary above the
/// "Unknown command" error; gating on this predicate keeps a typo's output to just the error.
/// </summary>
internal static class StartupNotices
{
    // Commands that print their own output and should never carry the version notices.
    private static readonly HashSet<string> SelfDescribing = new(StringComparer.OrdinalIgnoreCase)
    {
        "update", "-v", "--version", "-h", "-help", "--help", "help"
    };

    /// <summary>
    /// True when the startup version notices should be shown for <paramref name="args"/>.
    /// </summary>
    /// <param name="args">The raw command-line arguments.</param>
    /// <param name="knownVerbs">Recognized subcommands (run, test, verify, …) that will execute.</param>
    /// <param name="resolvesToRunnable">
    /// Resolves a bare leading token to whether cop can actually run it (a .cop file, a URL, or a
    /// command defined in a local .cop file). An unknown token resolves to false.
    /// </param>
    public static bool ShouldShow(string[] args, IReadOnlySet<string> knownVerbs, Func<string, bool> resolvesToRunnable)
    {
        if (args.Length == 0) return true;                          // bare `cop` runs local files / getting-started
        var first = args[0];
        if (SelfDescribing.Contains(first)) return false;           // help/version/update print their own output
        if (knownVerbs.Contains(first)) return true;                // a recognized verb will run
        if (first.StartsWith('-') || first.StartsWith('/')) return true; // option-led (root-command options)
        // A bare token only runs if it names something runnable; an unknown/misspelled command
        // must show just the "unknown command" error, never the version notices.
        return resolvesToRunnable(first);
    }
}
