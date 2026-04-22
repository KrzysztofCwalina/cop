namespace Cop.Driver.Backends;

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cop.Driver.Models;

/// <summary>
/// Local agent backend using Copilot SDK.
/// Creates git worktree → cop restore → start Copilot session → check-fix retry loop → merge.
/// </summary>
public class LocalBackend : IAgentBackend
{
    private readonly string _repoRoot;

    public string Name => "local";

    /// <summary>
    /// Initialize the local backend with the root repository path.
    /// </summary>
    /// <param name="repoRoot">Path to the main repository.</param>
    public LocalBackend(string repoRoot)
    {
        _repoRoot = repoRoot ?? throw new ArgumentNullException(nameof(repoRoot));
    }

    /// <summary>
    /// Execute a task through the full lifecycle.
    /// </summary>
    public async Task ExecuteAsync(DriverTask task, CancellationToken ct = default)
    {
        try
        {
            task.StartedAt = DateTime.UtcNow;

            // Phase 1: Restoring
            await RestorePhaseAsync(task, ct);
            if (task.Phase == TaskPhase.Failed || task.Phase == TaskPhase.Cancelled) return;

            // Phase 2: Executing
            await ExecutingPhaseAsync(task, ct);
            if (task.Phase == TaskPhase.Failed || task.Phase == TaskPhase.Cancelled) return;

            // Phase 3: Verifying with retry loop
            await VerifyPhaseWithRetryAsync(task, ct);
            if (task.Phase == TaskPhase.Failed || task.Phase == TaskPhase.Cancelled) return;

            // Phase 4: Merging
            await MergingPhaseAsync(task, ct);
            if (task.Phase == TaskPhase.Failed || task.Phase == TaskPhase.Cancelled) return;

            // Phase 5: Cleanup
            await CleanupAsync(task, ct);

            if (task.Phase != TaskPhase.Cancelled)
            {
                task.Phase = TaskPhase.Completed;
                task.AddLog("Task completed successfully");
            }
        }
        catch (OperationCanceledException)
        {
            task.Phase = TaskPhase.Cancelled;
            task.AddLog("Task was cancelled");
            await CleanupAsync(task, CancellationToken.None);
        }
        catch (Exception ex)
        {
            task.Phase = TaskPhase.Failed;
            task.ErrorMessage = ex.Message;
            task.AddLog($"Task failed with error: {ex.Message}");
            await CleanupAsync(task, CancellationToken.None);
        }
        finally
        {
            task.CompletedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Send feedback to the running agent session.
    /// </summary>
    public Task SendFeedbackAsync(DriverTask task, string message, CancellationToken ct = default)
    {
        task.PendingFeedback = message;
        task.AddLog($"Feedback queued: {message}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancel a running task.
    /// </summary>
    public async Task CancelAsync(DriverTask task, CancellationToken ct = default)
    {
        task.Phase = TaskPhase.Cancelled;
        task.AddLog("Cancel requested");
        await CleanupAsync(task, ct);
    }

    // ============ Private helper methods ============

    /// <summary>
    /// Phase 1: Create worktree, feature branch, and run cop restore.
    /// </summary>
    private async Task RestorePhaseAsync(DriverTask task, CancellationToken ct)
    {
        task.Phase = TaskPhase.Restoring;
        task.AddLog("Starting restore phase");

        try
        {
            // Create feature branch name and worktree path
            var branchName = $"cop/{task.Id}";
            var worktreePath = Path.Combine(_repoRoot, ".cop", "worktrees", task.Id);

            task.Branch = branchName;
            task.WorktreePath = worktreePath;

            // Ensure worktree directory exists
            var worktreeDir = Path.GetDirectoryName(worktreePath);
            if (!string.IsNullOrEmpty(worktreeDir) && !Directory.Exists(worktreeDir))
            {
                Directory.CreateDirectory(worktreeDir);
                task.AddLog($"Created worktree directory: {worktreeDir}");
            }

            // Create git worktree
            task.AddLog($"Creating worktree: {worktreePath}");
            var (exitCode, output) = await RunProcessAsync("git", $"worktree add \"{worktreePath}\" -b {branchName}", _repoRoot, ct);
            if (exitCode != 0)
            {
                task.ErrorMessage = $"Failed to create worktree: {output}";
                task.Phase = TaskPhase.Failed;
                task.AddLog($"Error creating worktree: {output}");
                return;
            }

            task.AddLog($"Worktree created: {worktreePath}");

            // Look for .cop project file
            var copFiles = Directory.GetFiles(worktreePath, "*.cop");
            if (copFiles.Length > 0)
            {
                task.AddLog($"Found {copFiles.Length} .cop file(s), running cop restore");
                // For now, just log it - Copilot SDK integration will be added later
                task.AddLog("cop restore placeholder - Copilot SDK integration pending");
            }
            else
            {
                task.AddLog("No .cop files found in worktree");
            }
        }
        catch (Exception ex)
        {
            task.Phase = TaskPhase.Failed;
            task.ErrorMessage = ex.Message;
            task.AddLog($"Error in restore phase: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 2: Write spec content and execute agent.
    /// </summary>
    private async Task ExecutingPhaseAsync(DriverTask task, CancellationToken ct)
    {
        task.Phase = TaskPhase.Executing;
        task.AddLog("Starting execution phase");

        try
        {
            if (string.IsNullOrEmpty(task.WorktreePath))
            {
                throw new InvalidOperationException("WorktreePath is not set");
            }

            // Write spec content to a temp file in the worktree
            var specFileName = Path.GetFileName(task.SpecPath) ?? "spec.md";
            var specFilePath = Path.Combine(task.WorktreePath, specFileName);

            await File.WriteAllTextAsync(specFilePath, task.SpecContent, ct);
            task.AddLog($"Wrote spec file: {specFilePath}");

            // Log placeholder for agent execution
            task.AddLog("Agent execution placeholder — Copilot SDK integration pending");

            // Simulate async work
            await Task.Delay(100, ct);
        }
        catch (Exception ex)
        {
            task.Phase = TaskPhase.Failed;
            task.ErrorMessage = ex.Message;
            task.AddLog($"Error in execution phase: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 3: Verify build and implement retry loop.
    /// </summary>
    private async Task VerifyPhaseWithRetryAsync(DriverTask task, CancellationToken ct)
    {
        task.Phase = TaskPhase.Verifying;
        task.AddLog("Starting checking phase");

        try
        {
            if (string.IsNullOrEmpty(task.WorktreePath))
            {
                throw new InvalidOperationException("WorktreePath is not set");
            }

            while (true)
            {
                task.Phase = TaskPhase.Verifying;
                task.AddLog($"Running verify attempt {task.VerifyAttempts + 1}/{task.MaxVerifyAttempts}");

                // Run dotnet build in the worktree
                var (exitCode, output) = await RunProcessAsync("dotnet", "build", task.WorktreePath, ct);

                task.LastVerifyReport = output;

                if (exitCode == 0)
                {
                    task.AddLog("Verification passed - build successful");
                    break;
                }

                // Build failed
                task.AddLog($"Verification failed: {output}");

                if (task.VerifyAttempts >= task.MaxVerifyAttempts)
                {
                    task.Phase = TaskPhase.Failed;
                    task.ErrorMessage = "Max verify attempts reached without successful build";
                    task.AddLog("Max verify attempts reached - task failed");
                    return;
                }

                // Retry: increment attempts and move to fixing phase
                task.VerifyAttempts++;
                task.Phase = TaskPhase.Fixing;
                task.AddLog($"Incrementing verify attempts to {task.VerifyAttempts}, moving to fixing phase");

                // TODO: Agent would fix the issue here (Copilot SDK integration)
                task.AddLog("Agent fixing placeholder — Copilot SDK integration pending");

                // Simulate async work before retry
                await Task.Delay(100, ct);
            }
        }
        catch (Exception ex)
        {
            task.Phase = TaskPhase.Failed;
            task.ErrorMessage = ex.Message;
            task.AddLog($"Error in checking phase: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 4: Merge feature branch back to main/default branch.
    /// </summary>
    private async Task MergingPhaseAsync(DriverTask task, CancellationToken ct)
    {
        task.Phase = TaskPhase.Merging;
        task.AddLog("Starting merge phase");

        try
        {
            if (string.IsNullOrEmpty(task.Branch) || string.IsNullOrEmpty(task.WorktreePath))
            {
                throw new InvalidOperationException("Branch or WorktreePath is not set");
            }

            // Get the default branch (usually "main" or "master")
            var (exitCode, output) = await RunProcessAsync("git", "symbolic-ref refs/remotes/origin/HEAD", _repoRoot, ct);
            var defaultBranch = output.Trim().Split('/').Last() ?? "main";
            task.AddLog($"Detected default branch: {defaultBranch}");

            // Switch to the default branch
            task.AddLog($"Switching to {defaultBranch}");
            (exitCode, output) = await RunProcessAsync("git", $"checkout {defaultBranch}", _repoRoot, ct);
            if (exitCode != 0)
            {
                task.ErrorMessage = $"Failed to checkout {defaultBranch}: {output}";
                task.Phase = TaskPhase.Failed;
                task.AddLog($"Error Switching to {defaultBranch}: {output}");
                return;
            }

            // Pull latest changes
            task.AddLog($"Pulling latest changes from {defaultBranch}");
            (exitCode, output) = await RunProcessAsync("git", "pull origin", _repoRoot, ct);
            if (exitCode != 0)
            {
                task.AddLog($"Warning: Pull failed: {output}");
            }

            // Merge the feature branch with --no-ff flag
            task.AddLog($"Merging branch {task.Branch} into {defaultBranch}");
            (exitCode, output) = await RunProcessAsync("git", $"merge --no-ff {task.Branch} -m \"Merge {task.Branch} - Task {task.Id}\"", _repoRoot, ct);
            if (exitCode != 0)
            {
                task.ErrorMessage = $"Merge failed: {output}";
                task.Phase = TaskPhase.Failed;
                task.AddLog($"Error during merge: {output}");
                return;
            }

            task.AddLog("Merge completed successfully");
        }
        catch (Exception ex)
        {
            task.Phase = TaskPhase.Failed;
            task.ErrorMessage = ex.Message;
            task.AddLog($"Error in merge phase: {ex.Message}");
        }
    }

    /// <summary>
    /// Cleanup: Remove git worktree.
    /// </summary>
    private async Task CleanupAsync(DriverTask task, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(task.WorktreePath))
            {
                return;
            }

            if (!Directory.Exists(task.WorktreePath))
            {
                task.AddLog("Worktree directory already cleaned up");
                return;
            }

            task.AddLog("Starting cleanup");

            // Remove the git worktree
            task.AddLog($"Removing worktree: {task.WorktreePath}");
            var (exitCode, output) = await RunProcessAsync("git", $"worktree remove \"{task.WorktreePath}\"", _repoRoot, ct);

            if (exitCode != 0)
            {
                // Try force removal
                task.AddLog($"Standard removal failed, attempting force removal: {output}");
                (exitCode, output) = await RunProcessAsync("git", $"worktree remove --force \"{task.WorktreePath}\"", _repoRoot, ct);

                if (exitCode != 0)
                {
                    task.AddLog($"Warning: Could not remove worktree: {output}");
                    return;
                }
            }

            task.AddLog("Worktree removed successfully");

            // Clean up empty directories
            try
            {
                var worktreeDir = Path.GetDirectoryName(task.WorktreePath);
                if (Directory.Exists(worktreeDir) && !Directory.EnumerateFileSystemEntries(worktreeDir).Any())
                {
                    Directory.Delete(worktreeDir);
                    task.AddLog("Cleaned up empty worktree directory");
                }
            }
            catch
            {
                // Ignore errors in cleanup
            }
        }
        catch (Exception ex)
        {
            task.AddLog($"Warning: Cleanup error: {ex.Message}");
        }
    }

    /// <summary>
    /// Run a process and return exit code and output.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        string command, 
        string args, 
        string workingDirectory,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };

        using (var process = new Process { StartInfo = psi })
        {
            process.Start();

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            // Capture output asynchronously
            var readOutputTask = process.StandardOutput.ReadToEndAsync();
            var readErrorTask = process.StandardError.ReadToEndAsync();

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // Set a reasonable timeout (e.g., 5 minutes for long operations)
                cts.CancelAfter(TimeSpan.FromMinutes(5));

                try
                {
                    if (!process.WaitForExit(TimeSpan.FromMinutes(5).Milliseconds))
                    {
                        process.Kill(true);
                        return (-1, "Process timed out");
                    }
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    throw;
                }
            }

            var output = await readOutputTask;
            var error = await readErrorTask;

            var fullOutput = string.IsNullOrEmpty(error) 
                ? output 
                : $"{output}\n{error}";

            return (process.ExitCode, fullOutput);
        }
    }
}
