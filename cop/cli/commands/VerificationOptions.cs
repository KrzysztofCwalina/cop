// Selects verification output and structural-check approval policy.

namespace Cop.Cli.Commands;

/// <summary>Configures static verification without executing a Cop program.</summary>
public sealed record VerificationOptions
{
    /// <summary>Whether diagnostics should be emitted as machine-readable JSON.</summary>
    public bool IsJsonOutputEnabled { get; init; }

    /// <summary>Whether structural checks must avoid unapproved raw-text dependencies.</summary>
    public bool IsStrictStructureEnabled { get; init; }

    /// <summary>Individual text-check names explicitly approved by the caller.</summary>
    public IReadOnlyList<string>? AllowedTextChecks { get; init; }
}
