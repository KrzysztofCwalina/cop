// Derives Go expression and control-flow structure from parsed token ranges.

using System.Text;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal sealed class GoStructuralFacts : TokenStructureWalker<GoToken>
{
    private GoStructuralFacts(List<GoToken> tokens, int start, int end, List<StatementInfo> statements, bool inMethod)
        : base(tokens, start, end, statements, inMethod)
    {
    }

    /// <summary>
    /// Adds parsed expression and control-flow facts for a Go token range.
    /// </summary>
    public static void Enrich(List<GoToken> tokens, int start, int end, List<StatementInfo> statements, bool inMethod = true)
        => new GoStructuralFacts(tokens, start, end, statements, inMethod).Walk();

    /// <inheritdoc />
    protected override bool IsComment(GoToken token) => token.Kind is GoTokenKind.LineComment or GoTokenKind.BlockComment;

    /// <inheritdoc />
    protected override bool IsStringOrComment(GoToken token)
        => token.Kind is GoTokenKind.StringLiteral or GoTokenKind.LineComment or GoTokenKind.BlockComment;

    /// <inheritdoc />
    protected override string ValueOf(GoToken token) => token.Value;

    /// <inheritdoc />
    protected override void VisitToken(int i)
    {
        List<GoToken> tokens = Tokens;
        int start = Start, end = End;
        GoToken token = tokens[i];
        if (token.Kind == GoTokenKind.Identifier && i + 2 < end && tokens[i + 1].Value == "=" &&
            (i == start || tokens[i - 1].Value is not "." and not "var"))
        {
            int valueEnd = i + 2;
            while (valueEnd < end && tokens[valueEnd].Line == token.Line &&
                tokens[valueEnd].Value is not ";" and not "}")
            {
                valueEnd++;
            }
            GoStatementInfo assignment = new GoStatementInfo(new StatementInfo("assignment", [], null, token.Value, [],
                token.Line, InMethod))
            {
                Column = token.Col, ValueExpression = Expression(i + 2, valueEnd)
            };
            Attach(assignment, Parent);
            Statements.Add(assignment);
        }
        StatementInfo? control = Statements.FirstOrDefault(s => s.Line == token.Line &&
            (s.Kind == token.Value || token.Value == "for" && s.Kind == "range"
                || token.Value == "switch" && s.Kind == "type-switch"
                || token.Value == "panic" && s.Kind == "throw"
                || token.Value == "recover" && s.Kind == "catch") &&
            (s.Column is null || s.Column == token.Col));
        if (control is not null)
        {
            control.Column = token.Col;
            Attach(control, Parent);
            if (token.Value == "if")
            {
                int conditionEnd = i + 1;
                while (conditionEnd < end && tokens[conditionEnd].Value != "{")
                {
                    conditionEnd++;
                }
                if (conditionEnd < end)
                {
                    control.ConditionExpression = Expression(i + 1, conditionEnd);
                }
            }
            if (token.Value is "if" or "for" or "switch" or "select")
            {
                Pending = control;
            }
        }
        int chainStart = 0;
        bool chained = i >= start + 2 && tokens[i - 1].Value == "." && TryGetCallStart(i - 2, out chainStart);
        if (token.Kind != GoTokenKind.Identifier || i > start && tokens[i - 1].Value == "." && !chained)
        {
            return;
        }

        int first = chained ? chainStart : i;
        int targetEnd = i + 1;
        while (targetEnd + 1 < end && tokens[targetEnd].Value == "." &&
               tokens[targetEnd + 1].Kind == GoTokenKind.Identifier)
        {
            targetEnd += 2;
        }

        int open = targetEnd;
        if (open < end && tokens[open].Value == "[")
        {
            open = Matching(open, end, "[", "]") + 1;
        }

        if (open >= end || tokens[open].Value != "(")
        {
            return;
        }

        int close = Matching(open, end, "(", ")");
        if (close >= end)
        {
            return;
        }

        ExpressionInfo target = Expression(first, targetEnd);
        List<(int Start, int End)> args = Arguments(open + 1, close);
        string member = tokens[targetEnd - 1].Value;
        string? receiver = targetEnd > first + 1
            ? Text(first, targetEnd - 2) : null;
        GoStatementInfo call = new GoStatementInfo(new StatementInfo("call", [], receiver, member,
            args.Select(a => Text(a.Start, a.End)).ToList(), token.Line, InMethod))
        {
            CallTarget = target,
            ArgumentExpressions = args.Select(a => Expression(a.Start, a.End)).ToList()
        };
        AddCall(call, close, first);
    }

    private ExpressionInfo Expression(int start, int end)
    {
        List<GoToken> tokens = Tokens;
        while (start < end && tokens[start].Kind is GoTokenKind.LineComment or GoTokenKind.BlockComment)
        {
            start++;
        }

        if (start >= end)
        {
            return new("other");
        }

        if (end == start + 1)
        {
            GoToken token = tokens[start];
            if (token.Value is "true" or "false")
            {
                return new("booleanLiteral") { BooleanValue = token.Value == "true" };
            }
            if (token.Value == "nil")
            {
                return new("nullLiteral");
            }
            if (token.Kind == GoTokenKind.NumberLiteral)
            {
                return new("numericLiteral") { Name = token.Value };
            }
            if (token.Kind == GoTokenKind.StringLiteral && token.Value.StartsWith('"'))
            {
                return new("stringLiteral") { StringValue = Decode(token.Value) };
            }

            if (token.Kind == GoTokenKind.StringLiteral && token.Value.StartsWith('`'))
            {
                return new("stringLiteral") { StringValue = token.Value[1..^1].Replace("\r", string.Empty) };
            }

            if (token.Kind == GoTokenKind.Identifier)
            {
                return new("identifier") { Name = token.Value };
            }

            return new("other");
        }
        if (tokens[start].Value == "(" && Matching(start, end, "(", ")") == end - 1)
        {
            return Expression(start + 1, end - 1);
        }

        int binary = TopLevelBinary(start, end, ["==", "!=", "<", ">", "<=", ">="]);
        if (binary < 0)
        {
            binary = TopLevelBinary(start, end, ["+", "-"]);
        }
        if (binary < 0)
        {
            binary = TopLevelBinary(start, end, ["*", "/", "%"]);
        }
        if (binary > start)
        {
            return new("binary")
            {
                Name = tokens[binary].Value,
                Children = [Expression(start, binary), Expression(binary + 1, end)]
            };
        }

        if (tokens[end - 1].Value == ")")
        {
            for (int i = start + 1; i < end; i++)
            {
                if (tokens[i].Value == "(" && Matching(i, end, "(", ")") == end - 1)
                {
                    return new("invocation")
                    {
                        Target = Expression(start, i),
                        Children = Arguments(i + 1, end - 1).Select(a => Expression(a.Start, a.End)).ToList()
                    };
                }
            }
        }
        if (tokens[end - 2].Value == "." && tokens[end - 1].Kind == GoTokenKind.Identifier)
        {
            return new("memberAccess") { Name = tokens[end - 1].Value, Target = Expression(start, end - 2) };
        }

        return new("other");
    }

    // Last top-level binary operator from `operators` (left-associative split); bails out on lower-precedence
    // operators it cannot nest. An operator counts as binary only when it follows an operand.
    private int TopLevelBinary(int start, int end, string[] operators)
    {
        List<GoToken> tokens = Tokens;
        int depth = 0, found = -1;
        for (int i = start; i < end; i++)
        {
            GoToken token = tokens[i];
            if (token.Kind != GoTokenKind.Punctuation) { continue; }
            if (token.Value is "(" or "[" or "{") { depth++; }
            else if (token.Value is ")" or "]" or "}") { depth--; }
            else if (depth == 0 && token.Value is "&&" or "||" or "=" or ":=" or ":" or "<-")
            {
                return -1;
            }
            else if (depth == 0 && token.Value is "==" or "!=" or "<" or ">" or "<=" or ">=" && !operators.Contains(token.Value))
            {
                return -1;
            }
            else if (depth == 0 && operators.Contains(token.Value) && i > start &&
                (tokens[i - 1].Kind is GoTokenKind.Identifier or GoTokenKind.StringLiteral or GoTokenKind.NumberLiteral
                    || tokens[i - 1].Value is ")" or "]"))
            {
                found = i;
            }
        }
        return found;
    }

    private static string Decode(string text)
    {
        StringBuilder result = new StringBuilder();
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
                    if (escape is >= '0' and <= '7')
                    {
                        value = Convert.ToInt32(text.Substring(offset, digits), 8);
                    }

                    if (System.Text.Rune.IsValid(value))
                    {
                        result.Append(char.ConvertFromUtf32(value));
                    }

                    i = offset + digits - 1;
                    continue;
                }
            }
            result.Append(escape switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', _ => escape });
        }
        return result.ToString();
    }
}
