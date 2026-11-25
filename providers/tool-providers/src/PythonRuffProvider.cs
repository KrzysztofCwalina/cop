// Runs Ruff and converts Python lint findings into provider violations.

using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Runs 'ruff check --output-format=json .' and exposes linting violations.
/// </summary>
public class PythonRuffProvider : ToolProvider
{
    protected override string ToolName => "ruff";

    protected override ValueTask<List<object>> RunToolAsync(
        string rootPath, IReadOnlySet<string> excluded, CancellationToken cancellationToken)
        => RunJsonToolAsync(
            new("ruff", "check --output-format=json .", rootPath) { Mode = ToolInvocationMode.Shell },
            rootPath, excluded, null, item => item.GetProperty("filename").GetString() ?? string.Empty,
            ToViolation, cancellationToken);

    private static ToolViolation ToViolation(JsonElement item, string filePath)
    {
        int line = item.TryGetProperty("location", out JsonElement loc) && loc.TryGetProperty("row", out JsonElement row)
            ? row.GetInt32() : 0;
        string? code = item.TryGetProperty("code", out JsonElement c) ? c.GetString() : string.Empty;
        string? message = item.TryGetProperty("message", out JsonElement m) ? m.GetString() : string.Empty;

        return new ToolViolation
        {
            File = filePath,
            Line = line,
            Severity = "warning",
            Message = $"{code}: {message}",
            Source = "ruff"
        };
    }
}
