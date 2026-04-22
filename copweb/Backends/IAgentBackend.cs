namespace Cop.Driver.Backends;

using Cop.Driver.Models;

/// <summary>
/// Abstraction over how the copweb driver executes agent tasks.
/// Two implementations: Local (Copilot SDK, in-process) and Cloud (GitHub REST API, issue-based).
/// </summary>
public interface IAgentBackend
{
    /// <summary>
    /// Execute a task through the full lifecycle:
    /// create worktree → restore → run agent → check → retry → merge
    /// </summary>
    Task ExecuteAsync(DriverTask task, CancellationToken ct = default);
    
    /// <summary>
    /// Send feedback message to the running agent session.
    /// </summary>
    Task SendFeedbackAsync(DriverTask task, string message, CancellationToken ct = default);
    
    /// <summary>
    /// Cancel a running task.
    /// </summary>
    Task CancelAsync(DriverTask task, CancellationToken ct = default);
    
    /// <summary>
    /// Name of this backend (e.g., "local", "cloud").
    /// </summary>
    string Name { get; }
}
