// Executes bounded external analysis tools and provides a common violation schema.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Base class for tool-wrapper providers that run an external CLI tool
/// and parse its output into a Violations collection.
/// </summary>
public abstract class ToolProvider : DataProvider
{
    private static readonly ReadOnlyMemory<byte> s_schemaJson = BuildSchema();

    private static ReadOnlyMemory<byte> BuildSchema()
    {
        ProviderSchema schema = new ProviderSchema
        {
            Types =
            [
                new ProviderTypeSchema
                {
                    Name = "Violation",
                    Properties =
                    [
                        new ProviderPropertySchema { Name = "File" },
                        new ProviderPropertySchema { Name = "Line", Type = "int" },
                        new ProviderPropertySchema { Name = "Severity" },
                        new ProviderPropertySchema { Name = "Message" },
                        new ProviderPropertySchema { Name = "Source" },
                    ]
                }
            ],
            Collections = [new ProviderCollectionSchema { Name = "Violations", ItemType = "Violation" }]
        };
        return schema.ToJson();
    }

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema() => s_schemaJson;

    /// <inheritdoc />
    public override async ValueTask<object?> QueryAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        string rootPath = query.RootPath ?? Directory.GetCurrentDirectory();
        IReadOnlySet<string> excluded = query.ExcludedDirectories ?? new HashSet<string>();

        List<object> violations = await RunToolAsync(rootPath, excluded, cancellationToken)
            .ConfigureAwait(false);
        return new Dictionary<string, List<object>> { ["Violations"] = violations };
    }

    protected abstract string ToolName { get; }

    protected abstract ValueTask<List<object>> RunToolAsync(
        string rootPath, IReadOnlySet<string> excluded, CancellationToken cancellationToken);

    /// <summary>Controls whether the platform shell resolves a tool command.</summary>
    protected enum ToolInvocationMode { Direct, Shell }

    /// <summary>Preserves a tool's raw argument string and execution policy.</summary>
    protected sealed record ToolInvocation(string FileName, string Arguments, string WorkingDirectory)
    {
        /// <summary>Gets the process launcher policy.</summary>
        public ToolInvocationMode Mode { get; init; }
        /// <summary>Gets the maximum duration of the owned tool process.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    }

    protected static async ValueTask<(string Stdout, string Stderr, int ExitCode)> RunProcessAsync(
        ToolInvocation invocation, CancellationToken cancellationToken)
    {
        string fileName = invocation.FileName;
        bool shell = invocation.Mode == ToolInvocationMode.Shell;
        ProcessStartInfo psi = new ProcessStartInfo
        {
            FileName = shell ? GetShellExecutable() : fileName,
            Arguments = shell ? GetShellArgs(fileName, invocation.Arguments) : invocation.Arguments,
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        Console.Error.Write($"  Running {fileName}...");
        Stopwatch sw = Stopwatch.StartNew();
        int maxLineLen = 0;

        cancellationToken.ThrowIfCancellationRequested();
        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {fileName}");

        // Stream stderr to console in real-time for progress visibility
        StringBuilder stderrBuilder = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                return;
            }

            stderrBuilder.AppendLine(e.Data);
            string line = e.Data.TrimEnd();
            if (line.Length > 0)
            {
                string msg = $"\r  Running {fileName}... {Truncate(line, 60)}";
                // Pad with spaces to overwrite any remnant text from longer previous lines
                if (msg.Length > maxLineLen)
                {
                    maxLineLen = msg.Length;
                }

                Console.Error.Write(msg.PadRight(maxLineLen));
            }
        };
        process.BeginErrorReadLine();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();

        using CancellationTokenSource timeout = new(invocation.Timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            string timeoutMsg =
                $"\r  Running {fileName}... timed out after {invocation.Timeout.TotalSeconds}s";
            Console.Error.WriteLine(timeoutMsg.PadRight(maxLineLen));
            throw new TimeoutException(
                $"{fileName} timed out after {invocation.Timeout.TotalSeconds}s", ex);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(process).ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        sw.Stop();
        string doneMsg = $"\r  Running {fileName}... done ({sw.Elapsed.TotalSeconds:F1}s)";
        Console.Error.WriteLine(doneMsg.PadRight(maxLineLen));
        if (process.ExitCode is 127 or 9009)
        {
            throw new FileNotFoundException(
                $"Tool '{fileName}' could not be found by the command launcher: {stderrBuilder}");
        }
        return (stdout, stderrBuilder.ToString(), process.ExitCode);
    }

    private static async ValueTask StopProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException ex) when (process.HasExited)
            {
                if (CopDiagnostics.IsTraceEnabled)
                {
                    Console.Error.WriteLine($"Tool exited before cancellation cleanup: {ex.Message}");
                }
            }
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    protected static string NormalizePath(string filePath, string rootPath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(filePath))
        {
            filePath = Path.GetRelativePath(rootPath, filePath);
        }

        return filePath.Replace('\\', '/');
    }

    protected static bool IsExcluded(string filePath, IReadOnlySet<string> excluded)
    {
        foreach (string dir in excluded)
        {
            if (filePath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) ||
                filePath.Equals(dir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string GetShellExecutable() =>
        OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";

    private static string GetShellArgs(string fileName, string arguments) =>
        OperatingSystem.IsWindows()
            ? $"/c \"{fileName} {arguments}\""
            : $"-c \"{fileName} {arguments}\"";
}
