// Carries named file content produced by a Cop command.

namespace Cop.Lang;

/// <summary>
/// Represents a file to be written by a SAVE command.
/// </summary>
public record FileOutput(string Path, string Content);
