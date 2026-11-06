// Represents a single violation record returned by tool providers.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cop.Core;

namespace Cop.Providers.Tools;

/// <summary>
/// Represents a single violation record returned by tool providers.
/// </summary>
public class ToolViolation
{
    /// <summary>Gets the source path reported by the external tool.</summary>
    public string File { get; init; } = string.Empty;
    /// <summary>Gets the source line reported by the external tool.</summary>
    public int Line { get; init; }
    /// <summary>Gets the normalized diagnostic severity.</summary>
    public string Severity { get; init; } = "warning";
    /// <summary>Gets the diagnostic text reported by the external tool.</summary>
    public string Message { get; init; } = string.Empty;
    /// <summary>Gets the diagnostic's tool or source identity.</summary>
    public string Source { get; init; } = string.Empty;
}
