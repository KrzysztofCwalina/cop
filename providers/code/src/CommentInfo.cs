// Represents parser-identified comments without their delimiters.

namespace Cop.Providers.SourceModel;

/// <summary>A parsed comment, with its delimiter removed from the content.</summary>
public sealed record CommentInfo(string Kind, string Content, int Line);
