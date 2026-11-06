// Configures one program execution without coupling callers to positional flags.

using Cop.Core;

namespace Cop.Providers;

/// <summary>Controls command selection, package resolution, and execution diagnostics.</summary>
public sealed record EngineRunOptions
{
    /// <summary>Gets the command to run, or null to use the program's default command.</summary>
    public string? CommandName { get; init; }

    /// <summary>Gets arguments forwarded to the selected Cop command.</summary>
    public IReadOnlyList<string>? ProgramArgs { get; init; }

    /// <summary>Gets an optional subset of command names to execute.</summary>
    public IReadOnlyList<string>? CommandFilter { get; init; }

    /// <summary>Gets the sink for diagnostic and timing messages.</summary>
    public Action<string>? DiagnosticLog { get; init; }

    /// <summary>Gets whether test assertions are evaluated during execution.</summary>
    public bool IsAssertMode { get; init; }

    /// <summary>Gets additional local package feeds in resolution order.</summary>
    public IReadOnlyList<string>? AdditionalFeedPaths { get; init; }

    /// <summary>Gets explicit script files in command-priority order.</summary>
    public IReadOnlyList<string>? ScriptFiles { get; init; }

    /// <summary>Gets provider packages already resolved by the caller.</summary>
    public IReadOnlyList<(string Dir, PackageMetadata Meta)>? ProviderPackages { get; init; }

    /// <summary>Gets extra provider package names to resolve during a package run.</summary>
    public IReadOnlyList<string>? ProviderNames { get; init; }

    /// <summary>Gets optional review settings for changed-code analysis.</summary>
    public ReviewOptions? ReviewOptions { get; init; }

    /// <summary>Gets the token that cancels program evaluation and provider operations.</summary>
    public CancellationToken CancellationToken { get; init; }

    internal Dictionary<string, List<Engine.ParsedModule>>? PackageModules { get; init; }
}
