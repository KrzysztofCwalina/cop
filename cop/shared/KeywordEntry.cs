// Associates a reserved word with its editor presentation category.

namespace Cop.Lang;

/// <summary>
/// A reserved word and its description and syntax-highlighting category.
/// </summary>
public readonly record struct KeywordEntry(string Name, string Detail, string Category);
