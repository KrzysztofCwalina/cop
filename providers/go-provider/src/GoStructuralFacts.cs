using System.Text;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal static class GoStructuralFacts
{
    public static void Enrich(List<GoToken> tokens, int start, int end, List<StatementInfo> statements, bool inMethod = true)
    {
        statements.RemoveAll(s => s.Kind == "call");
        var scopes = new Stack<StatementInfo?>();
        StatementInfo? parent = null;
        StatementInfo? pending = null;
        var callScopes = new Stack<(int End, StatementInfo Call)>();
        var callStarts = new Dictionary<int, int>();
        for (int i = start; i < end; i++)
        {
            var token = tokens[i];
            while (callScopes.Count > 0 && callScopes.Peek().End <= i) callScopes.Pop();
            if (token.Kind is GoTokenKind.LineComment or GoTokenKind.BlockComment) continue;
            if (token.Value == "{")
            {
                scopes.Push(parent);
                parent = pending ?? parent;
                pending = null;
                continue;
            }
            if (token.Value == "}")
            {
                if (scopes.Count > 0) parent = scopes.Pop();
                pending = null;
                continue;
            }
            var control = statements.FirstOrDefault(s => s.Line == token.Line &&
                (s.Kind == token.Value || token.Value == "for" && s.Kind == "range"
                    || token.Value == "switch" && s.Kind == "type-switch"
                    || token.Value == "panic" && s.Kind == "throw"
                    || token.Value == "recover" && s.Kind == "catch"));
            if (control is not null)
            {
                Attach(control, parent);
                if (token.Value is "if" or "for" or "switch" or "select") pending = control;
            }
            bool chained = i >= start + 2 && tokens[i - 1].Value == "." && callStarts.ContainsKey(i - 2);
            if (token.Kind != GoTokenKind.Identifier || i > start && tokens[i - 1].Value == "." && !chained) continue;
            int first = chained ? callStarts[i - 2] : i;
            int targetEnd = i + 1;
            while (targetEnd + 1 < end && tokens[targetEnd].Value == "." &&
                   tokens[targetEnd + 1].Kind == GoTokenKind.Identifier)
                targetEnd += 2;
            int open = targetEnd;
            if (open < end && tokens[open].Value == "[")
                open = Matching(tokens, open, end, "[", "]") + 1;
            if (open >= end || tokens[open].Value != "(") continue;
            int close = Matching(tokens, open, end, "(", ")");
            if (close >= end) continue;
            var target = Expression(tokens, first, targetEnd);
            var args = Arguments(tokens, open + 1, close);
            string member = tokens[targetEnd - 1].Value;
            string? receiver = targetEnd > first + 1
                ? Text(tokens, first, targetEnd - 2) : null;
            var call = new GoStatementInfo("call", [], receiver, member,
                args.Select(a => Text(tokens, a.Start, a.End)).ToList(), token.Line, inMethod)
            {
                CallTarget = target,
                ArgumentExpressions = args.Select(a => Expression(tokens, a.Start, a.End)).ToList()
            };
            Attach(call, callScopes.Count > 0 ? callScopes.Peek().Call : pending ?? parent);
            statements.Add(call);
            callScopes.Push((close, call));
            callStarts[close] = first;
        }
        statements.Sort((a, b) => a.Line.CompareTo(b.Line));
    }

    private static void Attach(StatementInfo statement, StatementInfo? parent)
    {
        statement.Parent = parent;
        if (parent is not null && !parent._children.Contains(statement)) parent._children.Add(statement);
    }

    private static int Matching(List<GoToken> tokens, int start, int end, string open, string close)
    {
        int depth = 0;
        for (int i = start; i < end; i++)
        {
            if (tokens[i].Kind is GoTokenKind.StringLiteral or GoTokenKind.LineComment or GoTokenKind.BlockComment) continue;
            if (tokens[i].Value == open) depth++;
            if (tokens[i].Value == close && --depth == 0) return i;
        }
        return end;
    }

    private static List<(int Start, int End)> Arguments(List<GoToken> tokens, int start, int end)
    {
        var result = new List<(int, int)>();
        int depth = 0, first = start;
        for (int i = start; i < end; i++)
        {
            if (tokens[i].Kind is GoTokenKind.StringLiteral or GoTokenKind.LineComment or GoTokenKind.BlockComment) continue;
            if (tokens[i].Value is "(" or "[" or "{") depth++;
            if (tokens[i].Value is ")" or "]" or "}") depth--;
            if (tokens[i].Value == "," && depth == 0) { result.Add((first, i)); first = i + 1; }
        }
        if (first < end) result.Add((first, end));
        return result;
    }

    private static string Text(List<GoToken> tokens, int start, int end) =>
        string.Concat(tokens.GetRange(start, end - start).Select(t => t.Value));

    private static ExpressionInfo Expression(List<GoToken> tokens, int start, int end)
    {
        while (start < end && tokens[start].Kind is GoTokenKind.LineComment or GoTokenKind.BlockComment) start++;
        if (start >= end) return new("other");
        if (end == start + 1)
        {
            var token = tokens[start];
            if (token.Kind == GoTokenKind.StringLiteral && token.Value.StartsWith('"'))
                return new("stringLiteral") { StringValue = Decode(token.Value) };
            if (token.Kind == GoTokenKind.StringLiteral && token.Value.StartsWith('`'))
                return new("stringLiteral") { StringValue = token.Value[1..^1].Replace("\r", "") };
            if (token.Kind == GoTokenKind.Identifier) return new("identifier") { Name = token.Value };
            return new("other");
        }
        if (tokens[start].Value == "(" && Matching(tokens, start, end, "(", ")") == end - 1)
            return Expression(tokens, start + 1, end - 1);
        if (tokens[end - 1].Value == ")")
        {
            for (int i = start + 1; i < end; i++)
                if (tokens[i].Value == "(" && Matching(tokens, i, end, "(", ")") == end - 1)
                    return new("invocation")
                    {
                        Target = Expression(tokens, start, i),
                        Children = Arguments(tokens, i + 1, end - 1).Select(a => Expression(tokens, a.Start, a.End)).ToList()
                    };
        }
        if (tokens[end - 2].Value == "." && tokens[end - 1].Kind == GoTokenKind.Identifier)
            return new("memberAccess") { Name = tokens[end - 1].Value, Target = Expression(tokens, start, end - 2) };
        return new("other");
    }

    private static string Decode(string text)
    {
        var result = new StringBuilder();
        for (int i = 1; i < text.Length - 1; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length - 1) { result.Append(c); continue; }
            char escape = text[++i];
            int digits = escape switch { 'x' => 2, 'u' => 4, 'U' => 8, >= '0' and <= '7' => 3, _ => 0 };
            if (digits > 0)
            {
                int offset = escape is >= '0' and <= '7' ? i : i + 1;
                if (offset + digits <= text.Length - 1 &&
                    int.TryParse(text.AsSpan(offset, digits), escape is >= '0' and <= '7'
                        ? System.Globalization.NumberStyles.Integer : System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out int value))
                {
                    if (escape is >= '0' and <= '7') value = Convert.ToInt32(text.Substring(offset, digits), 8);
                    if (System.Text.Rune.IsValid(value)) result.Append(char.ConvertFromUtf32(value));
                    i = offset + digits - 1;
                    continue;
                }
            }
            result.Append(escape switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', _ => escape });
        }
        return result.ToString();
    }
}
