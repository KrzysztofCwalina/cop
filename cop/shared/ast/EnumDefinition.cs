// Records the declared members and export metadata of an extensible enum.

namespace Cop.Lang;

/// <summary>
/// An extensible enum whose members resolve to their names. Providers may return
/// additional values that were not listed in the declaration.
/// </summary>
public record EnumDefinition(
    string Name,
    List<string> Members,
    int Line,
    bool IsExported = false,
    string? DocComment = null);
