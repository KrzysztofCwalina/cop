// Evaluates provider filter expressions against interpreter values.

using Cop.Core;

namespace Cop.Lang.Interpreter;

/// <summary>
/// Evaluates provider filter expressions against interpreter values.
/// </summary>
internal static class CopFilter
{
    public static bool Matches(FilterExpression filter, CopValue item) => filter switch
    {
        AndFilter and => and.Conditions.All(f => Matches(f, item)),
        OrFilter or => or.Conditions.Any(f => Matches(f, item)),
        NotFilter not => !Matches(not.Inner, item),
        CollectionAnyFilter any => CollectionValues.Enumerate(Field(item, any.Property))
            .Any(child => Matches(any.ItemFilter, child)),
        _ => FilterEvaluator.Matches(filter, name => Raw(Field(item, name)))
    };

    private static CopValue Field(CopValue value, string path)
    {
        foreach (string name in path.Split('.'))
        {
            value = Evaluator.ForceValue(value);
            value = value switch
            {
                CopDynamicObject obj => obj.GetField(name),
                CopObject obj => obj.GetField(name),
                CopNull => CopNull.Instance,
                _ => throw new CopEvaluationException($"Unknown query property '{path}'.")
            };
        }
        return Evaluator.ForceValue(value);
    }

    private static object? Raw(CopValue value) => value switch
    {
        CopNull => null,
        CopString s => s.Value,
        CopInt i => i.Value,
        CopNumber n => n.Value,
        CopBool b => b.Value,
        CopList or CopLazyCollection or CopQueryable => CollectionValues.Enumerate(value).Select(Raw).ToList(),
        _ => value.Display()
    };
}
