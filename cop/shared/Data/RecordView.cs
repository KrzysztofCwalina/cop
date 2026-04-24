namespace Cop.Core;

/// <summary>
/// Lightweight evaluator bridge: references a single record in a <see cref="DataTable"/>
/// by index. The cop evaluator works with <c>object</c> items — this class provides
/// identity and access to the underlying flat data without boxing large structs.
/// </summary>
public sealed class RecordView
{
    public readonly DataTable Table;
    public readonly int Index;

    public RecordView(DataTable table, int index)
    {
        Table = table;
        Index = index;
    }
}
