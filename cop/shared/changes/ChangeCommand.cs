// No shell, no interactive authentication, bounded execution and bounded output.

using System.Diagnostics;
using System.Text;

namespace Cop.Core;

/// <summary>No shell, no interactive authentication, bounded execution and bounded output.</summary>
internal sealed class ChangeCommand : ChangeCommandRunner
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    internal const int MaxOutputCharacters = 16 * 1024 * 1024;
    private static readonly TimeSpan s_stopTimeout = TimeSpan.FromSeconds(2);

    /// <inheritdoc/>
    public override async ValueTask<ChangeCommandResult> RunAsync(string directory, string executable,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Process process = new Process
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
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        process.StartInfo.Environment["GIT_PAGER"] = "cat";
        using CancellationTokenSource timeout = new(Timeout);
        using CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        Task<string[]>? readers = null;
        bool started = false;
        try
        {
            process.Start();
            started = true;
            Task<string> output = ReadBoundedAsync(process.StandardOutput, cancellation);
            Task<string> error = ReadBoundedAsync(process.StandardError, cancellation);
            readers = Task.WhenAll(output, error);
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            string[] streams = await readers.WaitAsync(cancellation.Token).ConfigureAwait(false);
            return new(process.ExitCode, streams[0], streams[1]);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception or OperationCanceledException
            or DecoderFallbackException)
        {
            cancellation.Cancel();
            string cleanup = started ? await StopAsync(process).ConfigureAwait(false) : string.Empty;
            if (readers is not null)
            {
                try { await readers.ConfigureAwait(false); }
                catch (Exception readerException) when (readerException is IOException
                    or InvalidOperationException or OperationCanceledException
                    or DecoderFallbackException) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            string reason = exception is OperationCanceledException
                ? "timed out or exceeded its output limit"
                : "could not complete";
            throw new InvalidOperationException(
                $"{executable} {reason}. Ensure it is installed, authentication is configured, and the repository is accessible. {exception.Message}{cleanup}",
                exception);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader,
        CancellationTokenSource cancellation)
    {
        StringBuilder text = new StringBuilder();
        char[] buffer = new char[8192];
        int count;
        try
        {
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation.Token)
                .ConfigureAwait(false)) != 0)
            {
                if (text.Length + count > MaxOutputCharacters)
                {
                    throw new InvalidOperationException("Command output exceeded the review limit.");
                }
                text.Append(buffer, 0, count);
            }
        }
        catch (Exception)
        {
            cancellation.Cancel();
            throw;
        }
        return text.ToString();
    }

    /// <summary>Stops the process tree; returns a note describing any cleanup failure, or an empty string.</summary>
    private static async ValueTask<string> StopAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return $" The process could not be stopped: {exception.Message}";
        }

        Task exited = process.WaitForExitAsync();
        Task completed = await Task.WhenAny(exited, Task.Delay(s_stopTimeout)).ConfigureAwait(false);
        return completed == exited ? string.Empty : $" The process did not exit within {s_stopTimeout.TotalSeconds} seconds of being stopped.";
    }
}
