// Represents a function implemented by named field mappings.

namespace Cop.Lang.Ast;

/// <summary>
/// Function body is a set of field mappings (for transform functions):
/// fn toSummary(item: Item) -> Summary =
///     Name = item.Name
///     Count = item.Items.count()
/// </summary>
public record MappingBody(List<FieldMapping> Mappings) : FunctionBody;
