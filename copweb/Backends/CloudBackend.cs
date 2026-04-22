namespace Cop.Driver.Backends;

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cop.Driver.Models;

/// <summary>
/// Cloud agent backend using GitHub REST API.
/// Creates issue from spec → assigns copilot-swe-agent → polls status → checks PR.
/// </summary>
public class CloudBackend : IAgentBackend
{
    private readonly HttpClient _httpClient;
    private readonly string _githubToken;
    private readonly string _owner;
    private readonly string _repo;

    public string Name => "cloud";

    public CloudBackend(HttpClient httpClient, string githubToken, string owner, string repo)
    {
        _httpClient = httpClient;
        _githubToken = githubToken;
        _owner = owner;
        _repo = repo;

        // Configure default headers
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", githubToken);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "cop-driver");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public async Task ExecuteAsync(DriverTask task, CancellationToken ct = default)
    {
        try
        {
            // Phase 1: Set to Executing and create issue
            task.Phase = TaskPhase.Executing;
            task.AddLog("Creating GitHub issue from spec...");

            var issueNumber = await CreateIssueAsync(task, ct);
            task.AddLog($"Created issue #{issueNumber}");

            // Phase 2: Poll for PR that references this issue
            task.AddLog("Polling for PR that references the issue...");
            var (prNumber, branchName) = await PollForPRAsync(issueNumber, ct);
            task.AddLog($"Found PR #{prNumber} on branch '{branchName}'");

            // Phase 3: Verifying - checkout branch and run build
            task.Phase = TaskPhase.Verifying;
            task.AddLog($"Switching to PR branch: {branchName}");
            await FetchBranchAsync(branchName, ct);
            task.AddLog("Branch checked out successfully");

            // Run build checks
            var checksPassed = await RunVerifyAsync(task, ct);

            if (!checksPassed)
            {
                if (task.VerifyAttempts < task.MaxVerifyAttempts)
                {
                    task.VerifyAttempts++;
                    task.Phase = TaskPhase.Fixing;
                    task.AddLog($"Verification failed. Posting review comment on PR #{prNumber}...");
                    await PostReviewCommentAsync(prNumber, task.LastVerifyReport ?? "Build failed", ct);
                    // Continue polling for new PR commits
                    await ExecuteAsync(task, ct);
                }
                else
                {
                    task.Phase = TaskPhase.Failed;
                    task.ErrorMessage = "Max verify attempts exceeded";
                    task.AddLog($"Failed: {task.ErrorMessage}");
                }
            }
            else
            {
                // Checks passed
                task.Phase = TaskPhase.Completed;
                task.AddLog($"All checks passed. PR #{prNumber} approved.");
                await ApprovePRAsync(prNumber, ct);
            }
        }
        catch (OperationCanceledException)
        {
            task.Phase = TaskPhase.Cancelled;
            task.AddLog("Task cancelled");
        }
        catch (Exception ex)
        {
            task.Phase = TaskPhase.Failed;
            task.ErrorMessage = ex.Message;
            task.AddLog($"Error: {ex.Message}");
        }
    }

    public async Task SendFeedbackAsync(DriverTask task, string message, CancellationToken ct = default)
    {
        try
        {
            // Extract issue number from logs or PendingFeedback
            var issueNumber = ExtractIssueNumberFromTask(task);
            if (issueNumber == null)
            {
                task.AddLog("Cannot send feedback: issue number not found");
                return;
            }

            task.AddLog("Posting feedback comment on issue...");
            await PostCommentAsync((int)issueNumber, message, ct);
            task.AddLog("Feedback comment posted");
        }
        catch (Exception ex)
        {
            task.AddLog($"Error sending feedback: {ex.Message}");
        }
    }

    public async Task CancelAsync(DriverTask task, CancellationToken ct = default)
    {
        try
        {
            var issueNumber = ExtractIssueNumberFromTask(task);
            if (issueNumber == null)
            {
                task.AddLog("Cannot cancel: issue number not found");
                return;
            }

            task.AddLog($"Closing issue #{issueNumber}...");
            await CloseIssueAsync((int)issueNumber, ct);
            task.Phase = TaskPhase.Cancelled;
            task.AddLog("Issue closed and task cancelled");
        }
        catch (Exception ex)
        {
            task.AddLog($"Error cancelling task: {ex.Message}");
        }
    }

    private async Task<int> CreateIssueAsync(DriverTask task, CancellationToken ct)
    {
        var title = task.SpecContent.Split('\n')[0].Trim();
        var body = task.SpecContent;

        var request = new IssueCreateRequest
        {
            Title = title,
            Body = body,
            Labels = new[] { "copilot-swe-agent" }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(
            $"https://api.github.com/repos/{_owner}/{_repo}/issues",
            content,
            ct);

        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        var issue = JsonSerializer.Deserialize<IssueResponse>(responseBody);
        
        // Store issue number in PendingFeedback for later retrieval
        task.PendingFeedback = $"issue:{issue!.Number}";

        return issue.Number;
    }

    private async Task<(int prNumber, string branchName)> PollForPRAsync(int issueNumber, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(30);
        var pollInterval = TimeSpan.FromSeconds(30);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            var response = await _httpClient.GetAsync(
                $"https://api.github.com/repos/{_owner}/{_repo}/pulls?state=open",
                ct);

            response.EnsureSuccessStatusCode();
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var prs = JsonSerializer.Deserialize<List<PRResponse>>(responseBody) ?? new();

            foreach (var pr in prs)
            {
                if (pr.Body?.Contains($"#{issueNumber}") == true)
                {
                    return (pr.Number, pr.Head.Ref);
                }
            }

            await Task.Delay(pollInterval, ct);
        }

        throw new TimeoutException($"No PR found referencing issue #{issueNumber} within 30 minutes");
    }

    private async Task FetchBranchAsync(string branchName, CancellationToken ct)
    {
        // Fetch the PR branch
        var fetchProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"fetch origin pull/*/head:{branchName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        fetchProcess.Start();
        await fetchProcess.WaitForExitAsync(ct);

        if (fetchProcess.ExitCode != 0)
        {
            var error = await fetchProcess.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Failed to fetch branch: {error}");
        }

        // Switch to the branch
        var checkoutProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"checkout {branchName}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        checkoutProcess.Start();
        await checkoutProcess.WaitForExitAsync(ct);

        if (checkoutProcess.ExitCode != 0)
        {
            var error = await checkoutProcess.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Failed to checkout branch: {error}");
        }
    }

    private async Task<bool> RunVerifyAsync(DriverTask task, CancellationToken ct)
    {
        var buildProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "build",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        buildProcess.Start();
        var output = await buildProcess.StandardOutput.ReadToEndAsync();
        var error = await buildProcess.StandardError.ReadToEndAsync();
        await buildProcess.WaitForExitAsync(ct);

        var passed = buildProcess.ExitCode == 0;
        task.LastVerifyReport = passed ? output : error;

        return passed;
    }

    private async Task PostReviewCommentAsync(int prNumber, string comment, CancellationToken ct)
    {
        var request = new CommentRequest { Body = comment };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(
            $"https://api.github.com/repos/{_owner}/{_repo}/pulls/{prNumber}/reviews",
            content,
            ct);

        response.EnsureSuccessStatusCode();
    }

    private async Task ApprovePRAsync(int prNumber, CancellationToken ct)
    {
        var request = new ReviewRequest { Event = "APPROVE", Body = "Verification passed!" };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(
            $"https://api.github.com/repos/{_owner}/{_repo}/pulls/{prNumber}/reviews",
            content,
            ct);

        response.EnsureSuccessStatusCode();
    }

    private async Task PostCommentAsync(int issueNumber, string comment, CancellationToken ct)
    {
        var request = new CommentRequest { Body = comment };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(
            $"https://api.github.com/repos/{_owner}/{_repo}/issues/{issueNumber}/comments",
            content,
            ct);

        response.EnsureSuccessStatusCode();
    }

    private async Task CloseIssueAsync(int issueNumber, CancellationToken ct)
    {
        var request = new IssueCloseRequest { State = "closed" };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            System.Text.Encoding.UTF8,
            "application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Patch,
            $"https://api.github.com/repos/{_owner}/{_repo}/issues/{issueNumber}")
        {
            Content = content
        };

        var response = await _httpClient.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();
    }

    private static int? ExtractIssueNumberFromTask(DriverTask task)
    {
        if (task.PendingFeedback?.StartsWith("issue:") == true)
        {
            if (int.TryParse(task.PendingFeedback.Substring(6), out var number))
            {
                return number;
            }
        }

        // Try to extract from logs
        foreach (var logEntry in task.Log)
        {
            if (logEntry.Contains("Created issue #"))
            {
                var parts = logEntry.Split('#');
                if (parts.Length > 1 && int.TryParse(parts[^1].Split(']')[0], out var number))
                {
                    return number;
                }
            }
        }

        return null;
    }

    // DTO classes for JSON serialization
    private class IssueCreateRequest
    {
        [JsonPropertyName("title")]
        public required string Title { get; init; }

        [JsonPropertyName("body")]
        public required string Body { get; init; }

        [JsonPropertyName("labels")]
        public required string[] Labels { get; init; }
    }

    private class IssueResponse
    {
        [JsonPropertyName("number")]
        public int Number { get; init; }
    }

    private class PRResponse
    {
        [JsonPropertyName("number")]
        public int Number { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("head")]
        public required HeadRef Head { get; init; }

        public class HeadRef
        {
            [JsonPropertyName("ref")]
            public required string Ref { get; init; }
        }
    }

    private class CommentRequest
    {
        [JsonPropertyName("body")]
        public required string Body { get; init; }
    }

    private class ReviewRequest
    {
        [JsonPropertyName("body")]
        public required string Body { get; init; }

        [JsonPropertyName("event")]
        public required string Event { get; init; }
    }

    private class IssueCloseRequest
    {
        [JsonPropertyName("state")]
        public required string State { get; init; }
    }
}
