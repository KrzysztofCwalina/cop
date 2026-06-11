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
    private static readonly ReadOnlyMemory<byte> SchemaJson = BuildSchema();

    private static ReadOnlyMemory<byte> BuildSchema()
    {
        var schema = new ProviderSchema
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

    public override ReadOnlyMemory<byte> GetSchema() => SchemaJson;

    public override object? Query(ProviderQuery query)
    {
        var rootPath = query.RootPath ?? Directory.GetCurrentDirectory();
        var excluded = query.ExcludedDirectories ?? new HashSet<string>();

        try
        {
            var violations = RunTool(rootPath, excluded);
            return new Dictionary<string, List<object>> { ["Violations"] = violations };
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine($"Error: {ToolName} not found. Is it installed and on PATH?");
            return new Dictionary<string, List<object>> { ["Violations"] = [] };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error running {ToolName}: {ex.Message}");
            return new Dictionary<string, List<object>> { ["Violations"] = [] };
        }
    }

    protected abstract string ToolName { get; }

    protected abstract List<object> RunTool(string rootPath, IReadOnlySet<string> excluded);

    protected static (string Stdout, string Stderr, int ExitCode) RunProcess(
        string fileName, string arguments, string workingDir, bool shell = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = shell ? GetShellExecutable() : fileName,
            Arguments = shell ? GetShellArgs(fileName, arguments) : arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {fileName}");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (stdout, stderr, process.ExitCode);
    }

    protected static string NormalizePath(string filePath, string rootPath)
    {
        if (string.IsNullOrEmpty(filePath)) return "";
        if (Path.IsPathRooted(filePath))
            filePath = Path.GetRelativePath(rootPath, filePath);
        return filePath.Replace('\\', '/');
    }

    protected static bool IsExcluded(string filePath, IReadOnlySet<string> excluded)
    {
        foreach (var dir in excluded)
        {
            if (filePath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) ||
                filePath.Equals(dir, StringComparison.OrdinalIgnoreCase))
                return true;
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

/// <summary>
/// Represents a single violation record returned by tool providers.
/// </summary>
public class ToolViolation
{
    public string File { get; init; } = "";
    public int Line { get; init; }
    public string Severity { get; init; } = "warning";
    public string Message { get; init; } = "";
    public string Source { get; init; } = "";
}
