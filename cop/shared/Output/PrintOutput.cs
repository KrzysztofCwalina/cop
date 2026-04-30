namespace Cop.Lang;

/// <summary>
/// Represents a single line of output from program evaluation.
/// The Content is a RichString with optional style annotations.
/// The Message property provides backward-compatible plain text access.
/// </summary>
public record PrintOutput(RichString Content)
{
    /// <summary>
    /// Creates a PrintOutput from a plain text message (no annotations).
    /// </summary>
    public PrintOutput(string message) : this(new RichString(message)) { }

    /// <summary>
    /// Plain text message with all annotations stripped.
    /// </summary>
    public string Message => Content.ToPlainText();

    public override string ToString() => Message;
}
