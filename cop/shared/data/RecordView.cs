// Provides a lightweight row view over a provider data table.

namespace Cop.Core;

/// <summary>
/// Lightweight evaluator bridge: references a single record in a <see cref="DataTable"/>
/// by index. The cop evaluator works with <c>object</c> items — this class provides
/// identity and access to the underlying flat data without boxing large structs.
/// </summary>
public sealed class RecordView
{
    /// <summary>The provider table containing the referenced record.</summary>
    public DataTable Table { get; }

    /// <summary>The zero-based row index within the table.</summary>
    public int Index { get; }

    /// <summary>
    /// References a row in the supplied table without copying its field data.
    /// </summary>
    public RecordView(DataTable table, int index)
    {
        Table = table;
        Index = index;
    }
}
