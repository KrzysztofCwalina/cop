namespace Cop.Lang;

/// <summary>
/// A segment of a parsed template string.
/// </summary>
public abstract record TemplateSegment;

/// <summary>
/// Literal text between interpolation spans.
/// </summary>
public record LiteralSegment(string Text) : TemplateSegment;

/// <summary>
/// An interpolation span referencing a property path (e.g., Type.Name).
/// Optionally includes an annotation string after % (e.g., Type.Name%red).
/// </summary>
public record ExpressionSegment(string[] PropertyPath, string? Annotation = null) : TemplateSegment;

/// <summary>
/// A literal string inside interpolation with an annotation (e.g., @'Hello'%red@).
/// </summary>
public record AnnotatedLiteralSegment(string Text, string Annotation) : TemplateSegment;
