// Runs Pylint and converts Python diagnostics into provider violations.

using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'pylint --output-format=json --recursive=y .' and exposes linting violations.
/// </summary>
public class PythonPylintProvider : ToolProvider
{
    protected override string ToolName => "pylint";

    protected override ValueTask<List<object>> RunToolAsync(
        string rootPath, IReadOnlySet<string> excluded, CancellationToken cancellationToken)
        => RunJsonToolAsync(
            new("pylint", "--output-format=json --recursive=y .", rootPath) { Mode = ToolInvocationMode.Shell },
            rootPath, excluded, null, item => item.GetProperty("path").GetString() ?? string.Empty,
            ToViolation, cancellationToken);

    private static ToolViolation ToViolation(JsonElement item, string filePath)
    {
        int line = item.TryGetProperty("line", out JsonElement ln) ? ln.GetInt32() : 0;
        string? messageId = item.TryGetProperty("message-id", out JsonElement mid) ? mid.GetString() : string.Empty;
        string? message = item.TryGetProperty("message", out JsonElement m) ? m.GetString() : string.Empty;
        string severity = MapPylintType(item.TryGetProperty("type", out JsonElement t) ? t.GetString() : string.Empty);

        return new ToolViolation
        {
            File = filePath,
            Line = line,
            Severity = severity,
            Message = $"{messageId}: {message}",
            Source = "pylint"
        };
    }

    private static string MapPylintType(string? type) => type switch
    {
        "error" or "fatal" => "error",
        "warning" => "warning",
        _ => "info"
    };
}
