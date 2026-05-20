namespace Cop.Lang.Interpreter;

/// <summary>
/// Adapts DataObject instances to the clean evaluator's value system.
/// This bridges the gap between the provider layer (DataObject with dictionary fields)
/// and the evaluator (CopValue hierarchy with typed access).
///
/// The adapter lazily converts fields on access — no up-front marshaling cost.
/// </summary>
public sealed class DataObjectAdapter : IDynamicObjectAdapter
{
    public static readonly DataObjectAdapter Instance = new();

    private DataObjectAdapter() { }

    public CopValue GetField(object obj, string name)
    {
        if (obj is not DataObject dataObj)
            return CopNull.Instance;

        var raw = dataObj.GetField(name);
        return Marshal(raw);
    }

    public string Display(object obj)
    {
        if (obj is DataObject dataObj)
            return dataObj.GetField("Name")?.ToString()
                ?? dataObj.GetField("Path")?.ToString()
                ?? dataObj.TypeName;
        return obj.ToString() ?? "";
    }

    /// <summary>
    /// Convert a raw CLR value (from DataObject fields) to a CopValue.
    /// </summary>
    public static CopValue Marshal(object? raw)
    {
        return raw switch
        {
            null => CopNull.Instance,
            bool b => CopBool.Of(b),
            int i => new CopInt(i),
            long l => new CopInt((int)l),
            double d => new CopNumber(d),
            float f => new CopNumber(f),
            string s => new CopString(s),
            DataObject nested => new CopDynamicObject(nested, Instance),
            IReadOnlyList<DataObject> list => new CopList(
                list.Select(item => (CopValue)new CopDynamicObject(item, Instance)).ToList()),
            System.Collections.IList list => MarshalList(list),
            _ => new CopString(raw.ToString() ?? "")
        };
    }

    private static CopList MarshalList(System.Collections.IList list)
    {
        var items = new List<CopValue>(list.Count);
        foreach (var item in list)
            items.Add(Marshal(item));
        return new CopList(items);
    }
}
