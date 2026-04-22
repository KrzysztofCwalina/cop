using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Cop.Driver.Models;

/// <summary>
/// Represents the lifecycle phase of a driver task.
/// </summary>
public enum TaskPhase
{
    Pending,       // Submitted, waiting to start
    Restoring,     // Running cop restore in worktree
    Executing,     // Agent is working on the spec
    Verifying,      // Running cop verify on results
    Fixing,        // Agent is fixing check failures (retry loop)
    Merging,       // Merging feature branch
    Completed,     // Successfully completed
    Failed,        // Failed after retries
    Cancelled      // Manually cancelled
}

/// <summary>
/// Represents a single agent task tracked by the cop driver.
/// Tracks the full lifecycle from submission through completion/failure.
/// </summary>
public class DriverTask
{
    /// <summary>
    /// Unique task ID (e.g., "task-abc123").
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Path to spec markdown file.
    /// </summary>
    public required string SpecPath { get; init; }

    /// <summary>
    /// Content of the spec file.
    /// </summary>
    public required string SpecContent { get; init; }

    /// <summary>
    /// Feature branch name.
    /// </summary>
    public string? Branch { get; set; }

    /// <summary>
    /// Git worktree path.
    /// </summary>
    public string? WorktreePath { get; set; }

    /// <summary>
    /// Current phase of the task.
    /// </summary>
    public TaskPhase Phase { get; set; } = TaskPhase.Pending;

    /// <summary>
    /// Timestamp when the task was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when the task started execution.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// Timestamp when the task completed or failed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Number of check-fix retry attempts completed.
    /// </summary>
    public int VerifyAttempts { get; set; }

    /// <summary>
    /// Maximum number of retries before failure.
    /// </summary>
    public int MaxVerifyAttempts { get; set; } = 3;

    /// <summary>
    /// Last cop check output.
    /// </summary>
    public string? LastVerifyReport { get; set; }

    /// <summary>
    /// Error details if the task failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Whether the task is currently paused.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Feedback queued while the agent is working.
    /// </summary>
    public string? PendingFeedback { get; set; }

    /// <summary>
    /// Task log entries.
    /// </summary>
    public List<string> Log { get; } = [];

    /// <summary>
    /// SHA256 hash of spec content for deduplication.
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// Add a timestamped entry to the task log.
    /// </summary>
    /// <param name="message">The message to log.</param>
    public void AddLog(string message)
    {
        Log.Add($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
    }

    /// <summary>
    /// Gets a value indicating whether the task is in a terminal phase
    /// (Completed, Failed, or Cancelled).
    /// </summary>
    public bool IsTerminal => Phase is TaskPhase.Completed or TaskPhase.Failed or TaskPhase.Cancelled;

    /// <summary>
    /// Gets the elapsed time since the task was created,
    /// or until completion if in a terminal phase.
    /// </summary>
    public TimeSpan Elapsed
    {
        get
        {
            var endTime = IsTerminal
                ? CompletedAt ?? DateTime.UtcNow
                : DateTime.UtcNow;
            return endTime - CreatedAt;
        }
    }

    /// <summary>
    /// Generate a unique task ID.
    /// </summary>
    /// <returns>A new task ID in the format "task-{8 hex chars}".</returns>
    public static string GenerateId() => $"task-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// Compute the SHA256 hash of the given content string.
    /// </summary>
    /// <param name="content">The content to hash.</param>
    /// <returns>The SHA256 hash as a hex string.</returns>
    public static string ComputeContentHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash);
    }
}
