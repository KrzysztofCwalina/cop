using Cop.Providers;

namespace Cop.Repl;

/// <summary>
/// Context-aware completion for the REPL. Determines what completions to offer
/// based on cursor position and preceding text.
/// </summary>
public class ReplCompleter
{
    private readonly ReplEvaluator _evaluator;

    // Built-in predicate names
    private static readonly string[] BuiltinPredicates =
    [
        "equals", "notEquals", "startsWith", "endsWith", "contains", "containsAny",
        "matches", "sameAs", "empty", "in", "greaterThan", "lessThan",
        "greaterOrEqual", "lessOrEqual"
    ];

    // Built-in transforms
    private static readonly string[] BuiltinTransforms =
    [
        "Trim", "Replace", "Words", "Where", "First", "Last",
        "Single", "ElementAt", "Select", "Text", "Count", "Any", "All"
    ];

    // Built-in properties
    private static readonly string[] BuiltinProperties =
    [
        "Length", "Count", "Lower", "Upper", "Normalized"
    ];

    public ReplCompleter(ReplEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    /// <summary>
    /// Gets completion candidates for the given input at the cursor position.
    /// Returns (candidates, replacementStart) where replacementStart is the index
    /// in the input where the completed text should replace from.
    /// </summary>
    public (List<string> Candidates, int ReplacementStart) GetCompletions(string input, int cursorPos)
    {
        if (string.IsNullOrEmpty(input) || cursorPos == 0)
            return (_evaluator.GetCompletionCandidates(), 0);

        string textUpToCursor = input[..cursorPos];

        // Determine context based on the character before cursor
        var (context, prefix, start) = DetermineContext(textUpToCursor);

        var candidates = context switch
        {
            CompletionContext.Start => _evaluator.GetCompletionCandidates(),
            CompletionContext.AfterColon => GetPredicateCandidates(),
            CompletionContext.AfterDot => GetPropertyAndTransformCandidates(textUpToCursor),
            CompletionContext.InParens => [], // Could offer string values later
            _ => _evaluator.GetCompletionCandidates()
        };

        // Filter by prefix
        if (!string.IsNullOrEmpty(prefix))
        {
            candidates = candidates
                .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return (candidates, start);
    }

    private static (CompletionContext Context, string Prefix, int Start) DetermineContext(string text)
    {
        // Walk backwards from end to find context
        int i = text.Length - 1;

        // Collect the current word (prefix)
        while (i >= 0 && IsIdentifierChar(text[i]))
            i--;

        string prefix = text[(i + 1)..];
        int start = i + 1;

        if (i < 0)
            return (CompletionContext.Start, prefix, start);

        char contextChar = text[i];
        return contextChar switch
        {
            ':' => (CompletionContext.AfterColon, prefix, start),
            '.' => (CompletionContext.AfterDot, prefix, start),
            '(' => (CompletionContext.InParens, prefix, start),
            _ => (CompletionContext.Start, prefix, start)
        };
    }

    private List<string> GetPredicateCandidates()
    {
        var candidates = new List<string>(BuiltinPredicates);
        candidates.AddRange(_evaluator.GetPredicateNames());
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
    }

    private List<string> GetPropertyAndTransformCandidates(string textUpToCursor)
    {
        // Check if the text before the dot is a provider namespace (e.g., "csharp.")
        int dotPos = textUpToCursor.LastIndexOf('.');
        if (dotPos >= 0)
        {
            int identStart = dotPos - 1;
            while (identStart >= 0 && IsIdentifierChar(textUpToCursor[identStart]))
                identStart--;
            identStart++;

            string beforeDot = textUpToCursor[identStart..dotPos];
            if (!string.IsNullOrEmpty(beforeDot))
            {
                var nsCandidates = _evaluator.GetNamespaceCollections(beforeDot);
                if (nsCandidates.Count > 0)
                    return nsCandidates;
            }
        }

        var candidates = new List<string>(BuiltinTransforms);
        candidates.AddRange(BuiltinProperties);

        // Try to determine the type from context
        string? typeName = InferTypeFromContext(textUpToCursor);
        if (typeName is not null)
        {
            var props = _evaluator.GetPropertyNames(typeName);
            candidates.AddRange(props);
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
    }

    // Element-extracting transforms that unwrap a collection to its item type
    private static readonly HashSet<string> ElementTransforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "First", "Last", "Single", "ElementAt"
    };

    private string? InferTypeFromContext(string text)
    {
        // Extract the full expression before the trailing dot
        int trailingDot = text.LastIndexOf('.');
        if (trailingDot < 0) return null;

        string fullExpr = text[..trailingDot];

        // Strip all parenthesized args: "csharp.Types('path')" → "csharp.Types"
        fullExpr = StripParenArgs(fullExpr);
        if (string.IsNullOrEmpty(fullExpr)) return null;

        // Split by dots and walk the chain
        var parts = fullExpr.Split('.');
        if (parts.Length == 0) return null;

        // Try progressively longer prefixes as the base collection
        // e.g., for ["csharp", "Types", "First"]:  try "csharp.Types" then "csharp"
        string? itemType = null;
        int chainStart = 0;

        for (int prefixLen = Math.Min(parts.Length, 2); prefixLen >= 1; prefixLen--)
        {
            string candidate = string.Join(".", parts[..prefixLen]);
            itemType = _evaluator.GetCollectionItemType(candidate);
            if (itemType is not null)
            {
                chainStart = prefixLen;
                break;
            }
        }

        if (itemType is null) return null;

        // Walk remaining parts as property/transform steps
        for (int i = chainStart; i < parts.Length; i++)
        {
            string step = parts[i];

            if (ElementTransforms.Contains(step))
                continue; // still the same item type, just unwrapped

            if (step.Equals("Count", StringComparison.OrdinalIgnoreCase))
                return "Int"; // numeric, no properties to show

            // Look up as a property on the current type
            var props = _evaluator.GetPropertyNames(itemType);
            if (props.Contains(step))
            {
                // We'd need the property's type to continue — for now just return the item type
                // since most chains are Collection.First.Property (one transform then properties)
                return null;
            }
        }

        return itemType;
    }

    private static string StripParenArgs(string expr)
    {
        // Remove all ('...') segments
        while (true)
        {
            int open = expr.IndexOf('(');
            if (open < 0) break;
            int close = expr.IndexOf(')', open);
            if (close < 0) break;
            expr = expr[..open] + expr[(close + 1)..];
        }
        return expr;
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '-';

    private enum CompletionContext
    {
        Start,
        AfterColon,
        AfterDot,
        InParens
    }
}
