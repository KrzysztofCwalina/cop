// CodeQL analysis violation (mapped from SARIF finding)

using System.Text.Json;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>CodeQL analysis violation (mapped from SARIF finding)</summary>
public record CodeqlViolation(
    string File,
    int Line,
    string Severity,
    string Message,
    string Source);
