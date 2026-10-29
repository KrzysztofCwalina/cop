using System.Text;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal static class JavaStructuralFacts
{
    public static void Enrich(List<JavaToken> tokens, int start, int end, List<StatementInfo> statements, bool inMethod = true)
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
            if (token.Kind is JavaTokenKind.LineComment or JavaTokenKind.BlockComment or JavaTokenKind.DocComment) continue;
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
            if (token.Kind == JavaTokenKind.Keyword &&
                token.Value is "if" or "while" or "return" or "switch" or "else" or "do" or "break" or "continue")
            {
                var statement = new JavaStatementInfo(token.Value, [], null, null, [], token.Line, inMethod)
                { IsBraced = i + 1 < end && tokens[i + 1].Value == "{" };
                Attach(statement, parent);
                statements.Add(statement);
                if (token.Value is "if" or "while" or "switch" or "else" or "do") pending = statement;
            }
            var control = statements.FirstOrDefault(s => s.Line == token.Line && s.Kind == token.Value && s.Kind != "call");
            if (control is not null)
            {
                Attach(control, parent);
                if (token.Value is "for" or "try" or "catch" or "synchronized") pending = control;
            }
            bool constructor = token.Value == "new";
            if (!constructor && token.Kind != JavaTokenKind.Identifier && token.Value is not "this" and not "super") continue;
            bool chained = !constructor && i >= start + 2 && tokens[i - 1].Value == "."
                && callStarts.ContainsKey(i - 2);
            if (!constructor && i > start && tokens[i - 1].Value is "." or "new" && !chained) continue;
            int first = constructor ? i + 1 : i;
            if (chained) first = callStarts[i - 2];
            int targetEnd = i + (constructor ? 2 : 1);
            while (targetEnd + 1 < end && tokens[targetEnd].Value == "." &&
                   tokens[targetEnd + 1].Kind == JavaTokenKind.Identifier)
                targetEnd += 2;
            int open = targetEnd;
            if (open < end && tokens[open].Value == "<")
            {
                int depth = 1;
                for (open++; open < end && depth > 0; open++)
                {
                    if (tokens[open].Value == "<") depth++;
                    else if (tokens[open].Value is ">" or ">>" or ">>>") depth -= tokens[open].Value.Length;
                }
            }
            if (open >= end || tokens[open].Value != "(") continue;
            int close = Matching(tokens, open, end);
            if (close >= end) continue;
            var args = Arguments(tokens, open + 1, close);
            var target = Expression(tokens, first, targetEnd);
            string member = constructor ? "<init>" : tokens[targetEnd - 1].Value;
            string? receiver = constructor ? Text(tokens, first, open)
                : targetEnd > first + 1 ? Text(tokens, first, targetEnd - 2) : null;
            var call = new JavaStatementInfo("call", [], receiver, member,
                args.Select(a => Text(tokens, a.Start, a.End)).ToList(), token.Line, inMethod)
            {
                CallTarget = constructor ? new("memberAccess") { Name = "<init>", Target = target } : target,
                ArgumentExpressions = args.Select(a => Expression(tokens, a.Start, a.End)).ToList()
            };
            Attach(call, callScopes.Count > 0 ? callScopes.Peek().Call : pending ?? parent);
            statements.Add(call);
            callScopes.Push((close, call));
            callStarts[close] = constructor ? i : first;
        }
        statements.Sort((a, b) => a.Line.CompareTo(b.Line));
    }

    private static void Attach(StatementInfo statement, StatementInfo? parent)
    {
        statement.Parent = parent;
        if (parent is not null && !parent._children.Contains(statement)) parent._children.Add(statement);
    }

    private static int Matching(List<JavaToken> tokens, int start, int end)
    {
        int depth = 0;
        for (int i = start; i < end; i++)
        {
            if (tokens[i].Kind is JavaTokenKind.StringLiteral or JavaTokenKind.LineComment or JavaTokenKind.BlockComment or JavaTokenKind.DocComment) continue;
            if (tokens[i].Value == "(") depth++;
            if (tokens[i].Value == ")" && --depth == 0) return i;
        }
        return end;
    }

    private static List<(int Start, int End)> Arguments(List<JavaToken> tokens, int start, int end)
    {
        var result = new List<(int, int)>();
        int depth = 0, first = start;
        for (int i = start; i < end; i++)
        {
            if (tokens[i].Kind is JavaTokenKind.StringLiteral or JavaTokenKind.LineComment or JavaTokenKind.BlockComment or JavaTokenKind.DocComment) continue;
            if (tokens[i].Value is "(" or "[" or "{") depth++;
            if (tokens[i].Value is ")" or "]" or "}") depth--;
            if (tokens[i].Value == "," && depth == 0) { result.Add((first, i)); first = i + 1; }
        }
        if (first < end) result.Add((first, end));
        return result;
    }

    private static string Text(List<JavaToken> tokens, int start, int end) =>
        string.Concat(tokens.GetRange(start, end - start).Select(t => t.Value));

    private static ExpressionInfo Expression(List<JavaToken> tokens, int start, int end)
    {
        while (start < end && tokens[start].Kind is JavaTokenKind.LineComment or JavaTokenKind.BlockComment or JavaTokenKind.DocComment) start++;
        if (start >= end) return new("other");
        if (end == start + 1)
        {
            var token = tokens[start];
            if (token.Kind == JavaTokenKind.StringLiteral && token.Value.StartsWith('"'))
                return token.Value.StartsWith("\"\"\"") ? new("other")
                    : new("stringLiteral") { StringValue = Decode(token.Value) };
            if (token.Kind == JavaTokenKind.Identifier || token.Value is "this" or "super")
                return new("identifier") { Name = token.Value };
            return new("other");
        }
        if (tokens[start].Value == "(" && Matching(tokens, start, end) == end - 1)
            return Expression(tokens, start + 1, end - 1);
        if (tokens[end - 1].Value == ")")
        {
            for (int i = start + 1; i < end; i++)
                if (tokens[i].Value == "(" && Matching(tokens, i, end) == end - 1)
                    return new("invocation")
                    {
                        Target = tokens[start].Value == "new"
                            ? new("memberAccess") { Name = "<init>", Target = Expression(tokens, start + 1, i) }
                            : Expression(tokens, start, i),
                        Children = Arguments(tokens, i + 1, end - 1).Select(a => Expression(tokens, a.Start, a.End)).ToList()
                    };
        }
        if (tokens[end - 2].Value == "." && tokens[end - 1].Kind == JavaTokenKind.Identifier)
            return new("memberAccess") { Name = tokens[end - 1].Value, Target = Expression(tokens, start, end - 2) };
        return new("other");
    }

    private static string Decode(string text)
    {
        var result = new StringBuilder();
        for (int i = 1; i < text.Length - 1; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length - 1) { result.Append(text[i]); continue; }
            char escape = text[++i];
            if (escape == 'u')
            {
                while (i + 1 < text.Length - 1 && text[i + 1] == 'u') i++;
                if (i + 4 < text.Length && ushort.TryParse(text.AsSpan(i + 1, 4),
                    System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value))
                { result.Append((char)value); i += 4; continue; }
            }
            if (escape is >= '0' and <= '7')
            {
                int value = escape - '0', remaining = escape <= '3' ? 2 : 1;
                while (remaining-- > 0 && i + 1 < text.Length - 1 && text[i + 1] is >= '0' and <= '7')
                    value = value * 8 + text[++i] - '0';
                result.Append((char)value);
                continue;
            }
            result.Append(escape switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 's' => ' ', _ => escape });
        }
        return result.ToString();
    }
}
