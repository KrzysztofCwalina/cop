// Runs external toolchain processes with captured output and cancellation support.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Cop.Core;

/// <summary>Runs installed optional toolchains without a shell, downloads, or configuration changes.</summary>
public static class ToolchainProcess
{
    /// <summary>
    /// Searches only absolute PATH directories, not the analyzed project's working directory.
    /// An explicit searchPath (including an empty string) isolates discovery in tests.
    /// Discovery does not prove that a launcher/shim is usable; callers must inspect RunAsync.
    /// </summary>
    public static string? FindExecutable(string name, string? searchPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Path.IsPathFullyQualified(name))
        {
            return IsExecutable(name) ? Path.GetFullPath(name) : null;
        }

        if (name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("Tool names must be bare names or absolute paths.", nameof(name));
        }

        string[] extensions = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? new[] { ".exe", ".com" }
            : new[] { "" };
        foreach (string entry in (searchPath ?? HostEnvironment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            foreach (string? extension in extensions)
            {
                string candidate = Path.Combine(directory, name + extension);
                if (IsExecutable(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }
        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".com", StringComparison.OrdinalIgnoreCase);
        }

        return (File.GetUnixFileMode(path) &
            (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    /// <summary>
    /// The deadline covers stdin, process exit, and both output streams. Output is bounded across
    /// stdout and stderr together. Only Unavailable permits a simpler-analysis fallback; a nonzero
    /// exit (including syntax errors) is Failed and must be interpreted by the calling provider.
    /// Pass an absolute discovered path to avoid searching PATH a second time.
    /// </summary>
    public static async Task<ToolchainResult> RunAsync(
        ToolchainCommand command,
        ToolchainRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string executable = command.Executable;
        IReadOnlyList<string> arguments = command.Arguments;
        string workingDirectory = command.WorkingDirectory;
        IReadOnlyDictionary<string, string?>? environment = command.Environment;
        string? standardInput = options?.StandardInput;
        TimeSpan? timeout = options?.Timeout;
        int maxOutputCharacters = options?.MaxOutputCharacters ?? 4_194_304;
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        TimeSpan duration = timeout ?? TimeSpan.FromSeconds(30);
        if (duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (maxOutputCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputCharacters));
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Toolchain working directory does not exist: {workingDirectory}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        string? path = FindExecutable(executable);
        if (path is null)
        {
            return new(ToolchainStatus.Unavailable, null, null, "", "", $"Toolchain '{executable}' is not installed on PATH.");
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        using Process process = new Process
        {
            StartInfo = new ProcessStartInfo(path)
            {
                WorkingDirectory = Path.GetFullPath(workingDirectory),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach ((string? name, string? value) in environment)
            {
                process.StartInfo.Environment[name] = value;
            }
        }

        await using ToolchainProcessContainment containment = new ToolchainProcessContainment();
        try
        {
            if (!process.Start())
            {
                return new(ToolchainStatus.Failed, path, null, "", "", $"Could not start toolchain '{path}'.");
            }

            containment.Attach(process);
        }
        catch (Win32Exception ex)
        {
            try { if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
            return new(ex.NativeErrorCode is 2 or 3 ? ToolchainStatus.Unavailable : ToolchainStatus.Failed,
                path, null, "", "", $"Cannot start toolchain '{path}': {ex.Message}");
        }

        StringBuilder stdout = new StringBuilder();
        StringBuilder stderr = new StringBuilder();
        int outputCharacters = 0;
        async Task ReadAsync(StreamReader reader, StringBuilder destination)
        {
            char[] buffer = new char[4096];
            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    return;
                }

                int total = Interlocked.Add(ref outputCharacters, count);
                int accepted = Math.Clamp(maxOutputCharacters - (total - count), 0, count);
                destination.Append(buffer, 0, accepted);
                if (total > maxOutputCharacters)
                {
                    deadline.Cancel();
                    throw new OutputLimitException();
                }
            }
        }
        async Task WriteAsync()
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), deadline.Token).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }

        Task work = Task.WhenAll(ReadAsync(process.StandardOutput, stdout),
            ReadAsync(process.StandardError, stderr), WriteAsync(), process.WaitForExitAsync(deadline.Token));
        ToolchainStatus status;
        string? diagnostic = null;
        try
        {
            await work.WaitAsync(deadline.Token).ConfigureAwait(false);
            status = process.ExitCode == 0 ? ToolchainStatus.Success : ToolchainStatus.Failed;
            if (status == ToolchainStatus.Failed)
            {
                diagnostic = $"Toolchain '{path}' exited with code {process.ExitCode}.";
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or OutputLimitException or IOException)
        {
            status = outputCharacters > maxOutputCharacters ? ToolchainStatus.OutputLimitExceeded
                : ex is IOException ? ToolchainStatus.Failed : ToolchainStatus.TimedOut;
            diagnostic = status switch
            {
                ToolchainStatus.OutputLimitExceeded => $"Toolchain '{path}' exceeded the {maxOutputCharacters} character output limit.",
                ToolchainStatus.TimedOut => $"Toolchain '{path}' exceeded its {duration.TotalSeconds:g} second deadline.",
                _ => $"Toolchain '{path}' communication failed: {ex.Message}"
            };
            deadline.Cancel();
            try
            {
                await containment.TerminateAsync().ConfigureAwait(false);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception cleanup) when (cleanup is Win32Exception or InvalidOperationException or TimeoutException)
            {
                diagnostic += $" Process cleanup failed: {cleanup.Message}";
            }
            // Closing the pipes also releases reads when an exited tool's child inherited them.
            process.StandardOutput.Close();
            process.StandardError.Close();
            process.StandardInput.BaseStream.Close();
            try { await work.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception pending) when (pending is OperationCanceledException or IOException
                or ObjectDisposedException or OutputLimitException or TimeoutException) { }
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new(status, path, process.HasExited ? process.ExitCode : null,
            stdout.ToString(), stderr.ToString(), diagnostic);
    }

    private sealed class OutputLimitException : Exception;
}
