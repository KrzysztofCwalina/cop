// Describes a flags declaration whose members receive successive power-of-two values.

namespace Cop.Lang;

/// <summary>
/// A flags declaration with members assigned power-of-two values starting at one.
/// </summary>
public record FlagsDefinition(
    string Name,
    List<string> Members,
    int Line,
    bool IsExported = false,
    string? DocComment = null);
