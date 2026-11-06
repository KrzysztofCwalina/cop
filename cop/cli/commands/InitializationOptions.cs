// Selects instruction formats, hooks, and the command embedded in generated files.

namespace Cop.Cli.Commands;

/// <summary>Configures repository coding-agent integration.</summary>
public sealed record InitializationOptions
{
    /// <summary>Agent whose instruction format should be generated.</summary>
    public InstructionAgent Agent { get; init; }

    /// <summary>Whether to generate local Claude settings and the companion Copilot hook.</summary>
    public bool IsLocalHookEnabled { get; init; }

    /// <summary>Whether to generate shared Claude settings.</summary>
    public bool IsGlobalHookEnabled { get; init; }

    /// <summary>Whether to generate a GitHub Copilot CLI hook.</summary>
    public bool IsCopilotHookEnabled { get; init; }

    /// <summary>Command embedded in generated instructions, defaulting to cop.</summary>
    public string? CopCommand { get; init; }
}
