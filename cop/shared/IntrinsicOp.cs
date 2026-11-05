// Describes a built-in operation's names, category, and execution capabilities.

namespace Cop.Lang;

/// <summary>
/// One built-in operation surface, including its aliases, tooling detail,
/// and flags controlling filtering, collection composition, and provider pushdown.
/// </summary>
public sealed record IntrinsicOp(
    string Name,
    IntrinsicKind Kind,
    string Detail,
    string[] Aliases,
    bool Pushable = false,
    bool IsBuiltinFilter = false,
    bool IsCollectionCombinator = false)
{
    /// <summary>The canonical operation name followed by each declared alias.</summary>
    public IEnumerable<string> AllNames
    {
        get
        {
            yield return Name;
            foreach (string a in Aliases)
            {
                yield return a;
            }
        }
    }
}
