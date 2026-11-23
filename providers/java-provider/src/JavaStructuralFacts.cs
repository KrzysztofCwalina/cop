// Derives Java expression and control-flow structure from parsed token ranges.

using System.Text;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal sealed class JavaStructuralFacts : TokenStructureWalker<JavaToken>
{
    private string _source = string.Empty;

    private JavaStructuralFacts(List<JavaToken> tokens, int start, int end, List<StatementInfo> statements, bool inMethod)
        : base(tokens, start, end, statements, inMethod)
    {
    }

    /// <summary>Adds expression and control-flow facts for a Java token range.</summary>
    internal static void Enrich(List<JavaToken> tokens, int start, int end, List<StatementInfo> statements,
        string source, bool inMethod = true)
        => new JavaStructuralFacts(tokens, start, end, statements, inMethod) { _source = source }.Walk();

    /// <inheritdoc />
    protected override bool IsComment(JavaToken token)
        => token.Kind is JavaTokenKind.LineComment or JavaTokenKind.BlockComment or JavaTokenKind.DocComment;

    /// <inheritdoc />
    protected override bool IsStringOrComment(JavaToken token) => token.Kind is JavaTokenKind.StringLiteral
        or JavaTokenKind.LineComment or JavaTokenKind.BlockComment or JavaTokenKind.DocComment;

    /// <inheritdoc />
    protected override string ValueOf(JavaToken token) => token.Value;

    /// <inheritdoc />
    protected override void VisitToken(int i)
    {
        List<JavaToken> tokens = Tokens;
        int start = Start, end = End;
        JavaToken token = tokens[i];
        int column = token.Start - _source.LastIndexOf('\n', Math.Max(0, token.Start - 1));
        if (token.Kind == JavaTokenKind.Keyword &&
            token.Value is "if" or "while" or "return" or "switch" or "else" or "do" or "break" or "continue")
        {
            JavaStatementInfo statement = new JavaStatementInfo(new StatementInfo(token.Value, [], null, null, [], token.Line, InMethod))
            {
                IsBraced = i + 1 < end && tokens[i + 1].Value == "{",
                Column = column,
                SyntaxStart = token.Start
            };
            Attach(statement, Parent);
            Statements.Add(statement);
            if (token.Value == "return")
            {
                int last = i + 1;
                while (last < end && tokens[last].Value != ";") { last++; }
                statement.ValueExpression = Expression(i + 1, last);
            }
            if (token.Value is "if" or "while" or "switch" or "else" or "do")
            {
                Pending = statement;
            }
        }
        StatementInfo? control = Statements.FirstOrDefault(s => s.Line == token.Line && s.Kind == token.Value
            && s.Kind != "call" && (s.Column is null || s.Column == column));
        if (control is not null)
        {
            control.Column = column;
            if (control is JavaStatementInfo javaControl)
            {
                javaControl.SyntaxStart = token.Start;
            }
            Attach(control, Parent);
            if (token.Value is "for" or "try" or "catch" or "synchronized")
            {
                Pending = control;
            }
        }
        bool constructor = token.Value == "new";
        if (!constructor && token.Kind != JavaTokenKind.Identifier && token.Value is not "this" and not "super")
        {
            return;
        }

        int chainStart = 0;
        bool chained = !constructor && i >= start + 2 && tokens[i - 1].Value == "."
            && TryGetCallStart(i - 2, out chainStart);
        if (!constructor && i > start && tokens[i - 1].Value is "." or "new" && !chained)
        {
            return;
        }

        int first = constructor ? i + 1 : i;
        if (chained)
        {
            first = chainStart;
        }

        int targetEnd = i + (constructor ? 2 : 1);
        while (targetEnd + 1 < end && tokens[targetEnd].Value == "." &&
               tokens[targetEnd + 1].Kind == JavaTokenKind.Identifier)
        {
            targetEnd += 2;
        }

        int open = targetEnd;
        if (open < end && tokens[open].Value == "<")
        {
            int depth = 1;
            for (open++; open < end && depth > 0; open++)
            {
                if (tokens[open].Value == "<")
                {
                    depth++;
                }
                else if (tokens[open].Value is ">" or ">>" or ">>>")
                {
                    depth -= tokens[open].Value.Length;
                }
            }
        }
        if (open >= end || tokens[open].Value != "(")
        {
            return;
        }

        int close = Matching(open, end);
        if (close >= end)
        {
            return;
        }

        List<(int Start, int End)> args = Arguments(open + 1, close);
        ExpressionInfo target = Expression(first, targetEnd);
        string member = constructor ? "<init>" : tokens[targetEnd - 1].Value;
        string? receiver = constructor ? Text(first, open)
            : targetEnd > first + 1 ? Text(first, targetEnd - 2) : null;
        JavaStatementInfo call = new JavaStatementInfo(new StatementInfo("call", [], receiver, member,
            args.Select(a => Text(a.Start, a.End)).ToList(), token.Line, InMethod))
        {
            CallTarget = constructor ? new("memberAccess") { Name = "<init>", Target = target } : target,
            ArgumentExpressions = args.Select(a => Expression(a.Start, a.End)).ToList(),
            Column = column,
            SyntaxStart = token.Start,
            SyntaxEnd = tokens[close].End
        };
        AddCall(call, close, constructor ? i : first);
    }

    private int Matching(int start, int end) => Matching(start, end, "(", ")");

    private ExpressionInfo Expression(int start, int end)
    {
        List<JavaToken> tokens = Tokens;
        while (start < end && tokens[start].Kind is JavaTokenKind.LineComment or JavaTokenKind.BlockComment or JavaTokenKind.DocComment)
        {
            start++;
        }

        if (start >= end)
        {
            return new("other");
        }

        if (end == start + 1)
        {
            JavaToken token = tokens[start];
            if (token.Value is "true" or "false")
            {
                return new("booleanLiteral") { BooleanValue = token.Value == "true" };
            }
            if (token.Value == "null")
            {
                return new("nullLiteral");
            }
            if (token.Kind == JavaTokenKind.NumberLiteral)
            {
                return new("numericLiteral") { Name = token.Value };
            }
            if (token.Kind == JavaTokenKind.StringLiteral && token.Value.StartsWith('"'))
            {
                return token.Value.StartsWith("\"\"\"") ? new("other")
                    : new("stringLiteral") { StringValue = Decode(token.Value) };
            }

            if (token.Kind == JavaTokenKind.Identifier || token.Value is "this" or "super")
            {
                return new("identifier") { Name = token.Value };
            }

            return new("other");
        }
        if (tokens[start].Value == "(" && Matching(start, end) == end - 1)
        {
            return Expression(start + 1, end - 1);
        }

        int plus = TopLevelPlus(start, end);
        if (plus > start)
        {
            return new("binary")
            {
                Name = "+", Children = [Expression(start, plus), Expression(plus + 1, end)]
            };
        }

        if (tokens[end - 1].Value == ")")
        {
            for (int i = start + 1; i < end; i++)
            {
                if (tokens[i].Value == "(" && Matching(i, end) == end - 1)
                {
                    return new("invocation")
                    {
                        Target = tokens[start].Value == "new"
                            ? new("memberAccess") { Name = "<init>", Target = Expression(start + 1, i) }
                            : Expression(start, i),
                        Children = Arguments(i + 1, end - 1).Select(a => Expression(a.Start, a.End)).ToList()
                    };
                }
            }
        }
        if (tokens[end - 2].Value == "." &&
            (tokens[end - 1].Kind == JavaTokenKind.Identifier || tokens[end - 1].Value == "class"))
        {
            return new("memberAccess") { Name = tokens[end - 1].Value, Target = Expression(start, end - 2) };
        }

        return new("other");
    }

    // Last binary '+' outside brackets; bails out on lower-precedence operators it cannot nest.
    private int TopLevelPlus(int start, int end)
    {
        List<JavaToken> tokens = Tokens;
        int depth = 0, found = -1;
        for (int i = start; i < end; i++)
        {
            JavaToken token = tokens[i];
            if (token.Kind != JavaTokenKind.Punctuation) { continue; }
            if (token.Value is "(" or "[" or "{") { depth++; }
            else if (token.Value is ")" or "]" or "}") { depth--; }
            else if (depth == 0 && token.Value is "?" or "->" or "&&" or "||" or "==" or "!=" or "=" or ":")
            {
                return -1;
            }
            else if (depth == 0 && token.Value == "+" && i > start &&
                (tokens[i - 1].Kind is JavaTokenKind.Identifier or JavaTokenKind.StringLiteral or JavaTokenKind.NumberLiteral
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
            if (text[i] != '\\' || i + 1 >= text.Length - 1) { result.Append(text[i]); continue; }
            char escape = text[++i];
            if (escape == 'u')
            {
                while (i + 1 < text.Length - 1 && text[i + 1] == 'u')
                {
                    i++;
                }

                if (i + 4 < text.Length && ushort.TryParse(text.AsSpan(i + 1, 4),
                    System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ushort value))
                { result.Append((char)value); i += 4; continue; }
            }
            if (escape is >= '0' and <= '7')
            {
                int value = escape - '0', remaining = escape <= '3' ? 2 : 1;
                while (remaining-- > 0 && i + 1 < text.Length - 1 && text[i + 1] is >= '0' and <= '7')
                {
                    value = value * 8 + text[++i] - '0';
                }

                result.Append((char)value);
                continue;
            }
            result.Append(escape switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 's' => ' ', _ => escape });
        }
        return result.ToString();
    }
}
