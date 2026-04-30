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

    private string? InferTypeFromContext(string text)
    {
        // Find the identifier before the dot
        int dotPos = text.LastIndexOf('.');
        if (dotPos < 0) return null;

        int identStart = dotPos - 1;
        while (identStart >= 0 && IsIdentifierChar(text[identStart]))
            identStart--;
        identStart++;

        string collectionName = text[identStart..dotPos];
        if (string.IsNullOrEmpty(collectionName)) return null;

        // Try to resolve the collection's item type
        return _evaluator.GetCollectionItemType(collectionName);
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
