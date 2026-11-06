// Language-created object literal: { Name = 'foo', Age = 42 }

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Language-created object literal: { Name = 'foo', Age = 42 }
/// </summary>
public sealed class CopObject : CopValue
{
    /// <summary>
    /// The language values indexed by their object member names.
    /// </summary>
    public IReadOnlyDictionary<string, CopValue> Fields { get; }
    /// <summary>
    /// The optional declared Cop type used for object type tests.
    /// </summary>
    public string? TypeName { get; init; }
    public CopObject(IReadOnlyDictionary<string, CopValue> fields) => Fields = fields;

    /// <summary>
    /// Tests whether the object contains the named member.
    /// </summary>
    public bool HasField(string name) => Fields.ContainsKey(name);

    /// <summary>
    /// Returns the named member, or the language null value when absent.
    /// </summary>
    public CopValue GetField(string name) =>
        Fields.TryGetValue(name, out CopValue? val) ? val
        : name == "Type" && TypeName is not null ? new CopString(TypeName)
        : CopNull.Instance;

    /// <inheritdoc/>
    public override string Display()
    {
        IEnumerable<string> pairs = Fields.Select(kv => $"{kv.Key} = {kv.Value.Display()}");
        return $"{{ {string.Join(", ", pairs)} }}";
    }
    /// <inheritdoc/>
    public override async ValueTask<string> DisplayAsync(
        CancellationToken cancellationToken = default)
    {
        List<string> pairs = new(Fields.Count);
        foreach ((string name, CopValue value) in Fields)
        {
            pairs.Add($"{name} = {await value.DisplayAsync(cancellationToken).ConfigureAwait(false)}");
        }
        return $"{{ {string.Join(", ", pairs)} }}";
    }
    /// <inheritdoc/>
    public override string ToString() => Display();
}
