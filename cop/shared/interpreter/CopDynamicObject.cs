// Wraps a provider-backed dynamic object.

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Wraps a provider-backed dynamic object. Fields are resolved lazily.
/// </summary>
public sealed class CopDynamicObject : CopValue
{
    private readonly IDynamicObjectAdapter _adapter;
    private readonly object _underlying;

    public CopDynamicObject(object underlying, IDynamicObjectAdapter adapter)
    {
        _underlying = underlying;
        _adapter = adapter;
    }

    public object Underlying => _underlying;
    public string? TypeName => _adapter.TypeName ?? (_underlying is DataObject dao ? dao.TypeName : null);
    public IEnumerable<string> FieldNames
    {
        get
        {
            foreach (string name in _adapter.GetFieldNames(_underlying))
            {
                yield return name;
            }

            if (_adapter.TypeName is not null)
            {
                yield return "Type";
            }
        }
    }

    public bool HasField(string name)
    {
        CopValue val = _adapter.GetField(_underlying, name);
        if (val is not CopNull)
        {
            return true;
        }

        return name == "Type" && _adapter.TypeName is not null;
    }

    public CopValue GetField(string name)
    {
        CopValue val = _adapter.GetField(_underlying, name);
        if (val is CopNull && name == "Type" && _adapter.TypeName is not null)
        {
            return new CopString(_adapter.TypeName);
        }

        return val;
    }

    public override string Display() => _adapter.Display(_underlying);
    public override string ToString() => Display();
}
