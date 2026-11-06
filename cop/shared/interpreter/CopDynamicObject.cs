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

    /// <summary>
    /// The provider or CLR object wrapped without copying its members.
    /// </summary>
    public object Underlying => _underlying;
    /// <summary>
    /// The adapter's Cop type name, falling back to the data object's declared type.
    /// </summary>
    public string? TypeName => _adapter.TypeName ?? (_underlying is DataObject dao ? dao.TypeName : null);
    /// <summary>
    /// The member names exposed by the adapter or wrapped data object.
    /// </summary>
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

    /// <summary>
    /// Tests whether the wrapped object exposes a member with the supplied name.
    /// </summary>
    public bool HasField(string name)
    {
        CopValue val = _adapter.GetField(_underlying, name);
        if (val is not CopNull)
        {
            return true;
        }

        return name == "Type" && _adapter.TypeName is not null;
    }

    /// <summary>
    /// Resolves a wrapped member and converts its value into a language value.
    /// </summary>
    public CopValue GetField(string name)
    {
        CopValue val = _adapter.GetField(_underlying, name);
        if (val is CopNull && name == "Type" && _adapter.TypeName is not null)
        {
            return new CopString(_adapter.TypeName);
        }

        return val;
    }

    /// <summary>Reads a member after awaiting provider preparation for that member.</summary>
    public async ValueTask<CopValue> GetFieldAsync(string name,
        CancellationToken cancellationToken = default)
    {
        CopValue value = await _adapter.GetFieldAsync(_underlying, name, cancellationToken)
            .ConfigureAwait(false);
        return value is CopNull && name == "Type" && _adapter.TypeName is not null
            ? new CopString(_adapter.TypeName) : value;
    }

    /// <summary>Tests member availability after any demand-driven asynchronous preparation.</summary>
    public async ValueTask<bool> HasFieldAsync(string name,
        CancellationToken cancellationToken = default) =>
        await GetFieldAsync(name, cancellationToken).ConfigureAwait(false) is not CopNull;

    /// <inheritdoc/>
    public override string Display() => _adapter.Display(_underlying);
    /// <inheritdoc/>
    public override ValueTask<string> DisplayAsync(
        CancellationToken cancellationToken = default) =>
        _adapter.DisplayAsync(_underlying, cancellationToken);
    /// <inheritdoc/>
    public override string ToString() => Display();
}
