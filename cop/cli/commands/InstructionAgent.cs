// Identifies the coding agent targeted by generated instructions or checks.

namespace Cop.Cli.Commands;

/// <summary>Supported coding-agent instruction formats.</summary>
public enum InstructionAgent
{
    /// <summary>GitHub Copilot instruction files and command-line agent.</summary>
    Copilot,

    /// <summary>Claude Code instruction files and command-line agent.</summary>
    Claude
}
