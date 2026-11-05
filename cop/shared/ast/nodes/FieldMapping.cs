// A single field mapping in a MappingBody.

namespace Cop.Lang.Ast;

/// <summary>
/// A single field mapping in a MappingBody.
/// </summary>
public record FieldMapping(string FieldName, Expression Value, int Line = 0) : AstNode(Line);
