// Resolves optional GitHub credentials without blocking or leaking child processes.

using Cop.Core;

namespace Cop.Cli;

internal static class GitHubAuthentication
{
    internal static async Task<string?> ResolveTokenAsync(
        CancellationToken cancellationToken = default)
    {
        string? token = HostEnvironment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        string? executable = ToolchainProcess.FindExecutable("gh");
        return executable is null ? null : await ReadTokenAsync(
            new ToolchainCommand(executable, ["auth", "token"],
                Directory.GetCurrentDirectory()), cancellationToken);
    }

    internal static async Task<string?> ReadTokenAsync(
        ToolchainCommand command, CancellationToken cancellationToken = default)
    {
        ToolchainResult result = await ToolchainProcess.RunAsync(command,
            new ToolchainRunOptions
            {
                Timeout = TimeSpan.FromSeconds(5),
                MaxOutputCharacters = 65_536
            }, cancellationToken);
        if (result.Status == ToolchainStatus.Success)
        {
            string token = result.StandardOutput.Trim();
            return token.Length == 0 ? null : token;
        }

        if (result.Status is ToolchainStatus.TimedOut or ToolchainStatus.OutputLimitExceeded)
        {
            Console.Error.WriteLine(
                $"Warning: GitHub authentication did not complete ({result.Status}); using unauthenticated requests.");
        }
        else if (CopDiagnostics.Level > 0)
        {
            Console.Error.WriteLine(
                $"[diag] GitHub authentication unavailable ({result.Status}, exit {result.ExitCode}); using unauthenticated requests.");
        }
        return null;
    }
}
