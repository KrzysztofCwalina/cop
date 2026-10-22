using System.Diagnostics;
using System.Text;

namespace Cop.Core;

internal sealed record ChangeCommandResult(int ExitCode, string Output, string Error);

internal interface IChangeCommand
{
    ChangeCommandResult Run(string directory, string executable, params string[] arguments);
}

/// <summary>No shell, no interactive authentication, bounded execution and bounded output.</summary>
internal sealed class ChangeCommand : IChangeCommand
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    internal const int MaxOutputCharacters = 16 * 1024 * 1024;

    public ChangeCommandResult Run(string directory, string executable, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false, true),
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        process.StartInfo.Environment["GIT_PAGER"] = "cat";
        using var cancellation = new CancellationTokenSource(Timeout);
        try
        {
            process.Start();
            var output = ReadBounded(process.StandardOutput, cancellation.Token);
            var error = ReadBounded(process.StandardError, cancellation.Token);
            // Kill immediately on a reader failure, rather than waiting for a blocked writer.
            var readers = Task.WhenAll(output, error);
            _ = readers.ContinueWith(_ => cancellation.Cancel(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            process.WaitForExitAsync(cancellation.Token).GetAwaiter().GetResult();
            readers.WaitAsync(cancellation.Token).GetAwaiter().GetResult();
            return new(process.ExitCode, output.Result, error.Result);
        }
        catch (Exception exception)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            var reason = exception is OperationCanceledException
                ? "timed out or exceeded its output limit"
                : "could not complete";
            throw new InvalidOperationException(
                $"{executable} {reason}. Ensure it is installed, authentication is configured, and the repository is accessible. {exception.Message}",
                exception);
        }
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken cancellation)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
        {
            if (text.Length + count > MaxOutputCharacters)
                throw new InvalidOperationException("Command output exceeded the review limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
