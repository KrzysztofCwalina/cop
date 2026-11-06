// Adapts DataObject instances to the clean evaluator's value system.

using Cop.Core;
using Cop.Providers;

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

    /// <inheritdoc/>
    public override IEnumerable<string> GetFieldNames(object obj) =>
        obj is DataObject dataObj ? dataObj.Fields.Keys : [];

    /// <inheritdoc/>
    public override CopValue GetField(object obj, string name)
    {
        if (obj is not DataObject dataObj)
        {
            return CopNull.Instance;
        }

        object? raw = dataObj.GetField(name);
        return Marshal(raw);
    }

    /// <inheritdoc/>
    public override async ValueTask<CopValue> GetFieldAsync(object obj, string name,
        CancellationToken cancellationToken = default) =>
        obj is DataObject data
            ? Marshal(await data.GetFieldAsync(name, cancellationToken).ConfigureAwait(false))
            : CopNull.Instance;

    /// <inheritdoc/>
    public override string Display(object obj)
    {
        if (obj is DataObject dataObj)
        {
            return dataObj.GetField("Name")?.ToString()
                ?? dataObj.GetField("Path")?.ToString()
                ?? dataObj.TypeName;
        }

        return obj.ToString() ?? "";
    }

    /// <inheritdoc/>
    public override async ValueTask<string> DisplayAsync(object obj,
        CancellationToken cancellationToken = default)
    {
        if (obj is not DataObject data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return obj.ToString() ?? "";
        }
        object? name = await data.GetFieldAsync("Name", cancellationToken).ConfigureAwait(false);
        if (name is not null)
        {
            return name.ToString() ?? "";
        }
        object? path = await data.GetFieldAsync("Path", cancellationToken).ConfigureAwait(false);
        return path?.ToString() ?? data.TypeName;
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
        List<CopValue> items = new List<CopValue>(list.Count);
        foreach (object? item in list)
        {
            items.Add(Marshal(item));
        }

        return new CopList(items);
    }
}
