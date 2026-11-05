// Language-created object literal: { Name = 'foo', Age = 42 }

using Cop.Core;
using Cop.Lang.Ast;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Language-created object literal: { Name = 'foo', Age = 42 }
/// </summary>
public sealed class CopObject : CopValue
{
    public IReadOnlyDictionary<string, CopValue> Fields { get; }
    public string? TypeName { get; init; }
    public CopObject(IReadOnlyDictionary<string, CopValue> fields) => Fields = fields;

    public bool HasField(string name) => Fields.ContainsKey(name);

    public CopValue GetField(string name) =>
        Fields.TryGetValue(name, out CopValue? val) ? val
        : name == "Type" && TypeName is not null ? new CopString(TypeName)
        : CopNull.Instance;

    public override string Display()
    {
        IEnumerable<string> pairs = Fields.Select(kv => $"{kv.Key} = {kv.Value.Display()}");
        return $"{{ {string.Join(", ", pairs)} }}";
    }
    public override string ToString() => Display();
}
