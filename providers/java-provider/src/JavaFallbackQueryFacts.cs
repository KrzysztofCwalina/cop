// Recognizes a deliberately small, balanced-token grammar when javac is unavailable.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal sealed class JavaFallbackQueryFacts
{
    private readonly List<JavaToken> _tokens;
    private readonly Dictionary<int, int> _close = [];

    private JavaFallbackQueryFacts(List<JavaToken> tokens)
    {
        _tokens = tokens.Where(t => t.Kind is not (JavaTokenKind.LineComment
            or JavaTokenKind.BlockComment or JavaTokenKind.DocComment)).ToList();
        Stack<int> openings = [];
        for (int i = 0; i < _tokens.Count; i++)
        {
            if (Value(i) is "(" or "[" or "{")
            {
                openings.Push(i);
            }
            else if (Value(i) is ")" or "]" or "}")
            {
                if (openings.Count == 0)
                {
                    continue;
                }

                int open = openings.Pop();
                if ((Value(open), Value(i)) is ("(", ")") or ("[", "]") or ("{", "}"))
                {
                    _close[open] = i;
                }
            }
        }
    }

    internal static void Enrich(SourceFile file, List<JavaToken> tokens)
    {
        JavaFallbackQueryFacts grammar = new(tokens);
        grammar.Apply(file);
    }

    private string Value(int index) => index >= 0 && index < _tokens.Count ? _tokens[index].Value : "";
    private int Close(int index) => _close.GetValueOrDefault(index, -1);

    private void Apply(SourceFile file)
    {
        Dictionary<int, JavaStatementInfo> calls = file.Statements.OfType<JavaStatementInfo>()
            .Where(s => s.Kind == "call" && s.SyntaxStart >= 0)
            .GroupBy(s => s.SyntaxStart).ToDictionary(g => g.Key, g => g.First());
        Dictionary<int, JavaStatementInfo> catches = file.Statements.OfType<JavaStatementInfo>()
            .Where(s => s.Kind == "catch" && s.SyntaxStart >= 0)
            .GroupBy(s => s.SyntaxStart).ToDictionary(g => g.Key, g => g.First());
        // The fallback does not build cleanup regions. Reject rather than approximate them.
        bool hasCleanup = _tokens.Any(t => t.Value == "finally");
        for (int i = 0; i < _tokens.Count; i++)
        {
            if (!hasCleanup && Value(i) == "catch" && catches.TryGetValue(_tokens[i].Start, out JavaStatementInfo? caught))
            {
                int parameterEnd = Close(i + 1);
                if (parameterEnd > i + 2 && _tokens[parameterEnd - 1].Kind == JavaTokenKind.Identifier)
                {
                    int body = parameterEnd + 1;
                    int bodyEnd = Close(body);
                    caught.IsOriginalExceptionAlwaysRethrown = Value(body) == "{" && bodyEnd > body
                        && Rethrows(body, bodyEnd + 1, Value(parameterEnd - 1));
                }
            }

            if (!hasCleanup && Value(i) == "return")
            {
                int end = StatementEnd(i + 1);
                JavaStatementInfo? call = DirectCall(i + 1, end, calls);
                if (call?.MemberName == "<init>")
                {
                    call.IsOwnershipTransferred = true;
                }
            }

            int headerEnd = Close(i + 1);
            if (Value(i) != "try" || Value(i + 1) != "(" || headerEnd < 0)
            {
                continue;
            }

            int first = i + 2;
            for (int j = first; j <= headerEnd; j++)
            {
                if (j != headerEnd && Value(j) != ";")
                {
                    if (Close(j) > j)
                    {
                        j = Close(j);
                    }

                    continue;
                }

                int equals = first;
                while (equals < j && Value(equals) != "=")
                {
                    equals++;
                }

                // A resource declaration has a variable immediately before its initializer.
                if (equals > first && _tokens[equals - 1].Kind == JavaTokenKind.Identifier)
                {
                    JavaStatementInfo? resource = DirectCall(equals + 1, j, calls);
                    if (resource is not null)
                    {
                        resource.IsUsingResource = true;
                    }
                }

                first = j + 1;
            }
        }
    }

    private JavaStatementInfo? DirectCall(int start, int end, Dictionary<int, JavaStatementInfo> calls)
    {
        while (Value(start) == "(" && Close(start) == end - 1)
        {
            start++;
            end--;
        }

        return start < end && calls.TryGetValue(_tokens[start].Start, out JavaStatementInfo? call)
            && call.SyntaxEnd == _tokens[end - 1].End ? call : null;
    }

    private int StatementEnd(int start)
    {
        for (int i = start; i < _tokens.Count; i++)
        {
            if (Value(i) is ";" or "}")
            {
                return i;
            }

            if (Close(i) > i)
            {
                i = Close(i);
            }
        }

        return _tokens.Count;
    }

    private bool Rethrows(int start, int end, string variable)
    {
        if (Value(start) == "{" && Close(start) == end - 1)
        {
            return Rethrows(start + 1, end - 1, variable);
        }

        if (Value(start) == "throw" && Value(end - 1) == ";")
        {
            int first = start + 1, last = end - 1;
            while (Value(first) == "(" && Close(first) == last - 1)
            {
                first++;
                last--;
            }

            return last == first + 1 && Value(first) == variable;
        }

        if (Value(start) != "if" || Value(start + 1) != "(")
        {
            return false;
        }

        int conditionEnd = Close(start + 1);
        if (conditionEnd < 0 || !PureCondition(start + 2, conditionEnd))
        {
            return false;
        }

        int thenStart = conditionEnd + 1;
        // Only braced branches are supported by this smaller parser.
        int thenEnd = Value(thenStart) == "{" ? Close(thenStart) + 1 : -1;
        return thenEnd > thenStart && Value(thenEnd) == "else"
            && Rethrows(thenStart, thenEnd, variable)
            && Rethrows(thenEnd + 1, end, variable);
    }

    private bool PureCondition(int start, int end) =>
        start < end && _tokens.GetRange(start, end - start).All(t =>
            t.Kind is JavaTokenKind.Identifier or JavaTokenKind.NumberLiteral
            || t.Value is "true" or "false" or "null" or "(" or ")" or "!"
                or "&&" or "||" or "==" or "!=" or "<" or ">" or "<=" or ">=")
        && !Enumerable.Range(start, end - start).Any(i =>
            _tokens[i].Kind == JavaTokenKind.Identifier && Value(i + 1) == "(");
}
