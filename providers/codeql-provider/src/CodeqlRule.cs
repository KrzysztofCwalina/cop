// CodeQL rule definition from SARIF

using System.Text.Json;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers;

/// <summary>CodeQL rule definition from SARIF</summary>
public record CodeqlRule(
    string Id,
    string Name,
    string Description,
    string Severity,
    string Tags,
    string Precision);
