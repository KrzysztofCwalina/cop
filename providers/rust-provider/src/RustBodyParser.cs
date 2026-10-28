using System.Globalization;
using System.Text;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

// Syntax facts only: a receiver is an expression, never an inferred CLR/Rust type.
internal sealed class RustBodyParser(List<RustToken> tokens, string source, List<StatementInfo> statements,
    Action<RustToken> reportNestingLimit, Func<int, int> parseNestedFunction, Dictionary<int, int> pairs)
{
    private readonly Dictionary<int, int> _pairs = pairs;
    private readonly List<(int position, StatementInfo statement)> _statements = [];
    private int _pos;
    private int _end;
    private int _depth;

    private sealed record Expression(ExpressionInfo Info, int Start, int End, List<StatementInfo> Roots);

    public void Parse(int start, int end)
    {
        _pos = start;
        _end = end;
        ParseSequence(null);
        statements.AddRange(_statements.OrderBy(s => s.position).Select(s => s.statement));
    }

    private string Value => _pos < _end ? tokens[_pos].Value : "";
    private string Next => _pos + 1 < _end ? tokens[_pos + 1].Value : "";
    private bool AtEnd => _pos >= _end;
    private static bool IsTrivia(RustToken token) => token.Kind is RustTokenKind.LineComment
        or RustTokenKind.BlockComment or RustTokenKind.DocComment or RustTokenKind.Attribute;

    private void Trivia()
    {
        while (!AtEnd && IsTrivia(tokens[_pos])) _pos++;
    }

    private void ParseSequence(StatementInfo? parent)
    {
        while (!AtEnd)
        {
            int start = _pos;
            Trivia();
            if (AtEnd) break;
            if (Value is ";" or ",") { _pos++; continue; }
            if (Value is "fn" or "async" or "unsafe" or "const" or "extern"
                && tokens.Skip(_pos).Take(6).Any(t => t.Value == "fn"))
                _pos = parseNestedFunction(_pos);
            else if (Value is "if" or "while" or "for" or "loop" or "match")
                Control(parent);
            else if (Value == "{")
                Block(parent);
            else if (Value == "macro_rules" && Next == "!")
            {
                _pos += 2;
                if (!AtEnd) _pos++;
                SkipGroup();
            }
            else if (Value == "let")
                Declaration(parent);
            else if (Value is "return" or "break" or "continue" or "yield")
            {
                string keyword = Value;
                _pos++;
                // The common model has no break/continue/yield enum member.
                var statement = Add(start, keyword == "return" ? "return" : "command", parent, [keyword]);
                if (tokens.ElementAtOrDefault(_pos).Kind == RustTokenKind.Lifetime) _pos++;
                if (!AtEnd && Value is not ";" and not "}")
                {
                    var value = ReadExpression();
                    statement.ValueExpression = value.Info;
                    Attach(statement, value.Roots);
                }
                Finish(statement, start);
            }
            else
                Attach(parent, ReadExpression().Roots);
            if (_pos == start) _pos++;
        }
    }

    private void Declaration(StatementInfo? parent)
    {
        int start = _pos++;
        int patternStart = _pos;
        // Patterns are not invocations, including tuple/struct destructuring.
        while (!AtEnd && Value is not "=" and not ";" and not "else")
        {
            if (_pairs.TryGetValue(_pos, out int close)) _pos = close + 1;
            else _pos++;
        }
        int binding = patternStart;
        while (binding < _pos && tokens[binding].Value is "mut" or "ref") binding++;
        string? name = binding < _pos && tokens[binding].Kind == RustTokenKind.Identifier
            && (binding + 1 == _pos || tokens[binding + 1].Value == ":") ? tokens[binding].Value : null;
        var declaration = Add(start, "declaration", parent, ["let"], member: name);
        if (Value == "=")
        {
            _pos++;
            var value = ReadExpression();
            declaration.ValueExpression = value.Info;
            Attach(declaration, value.Roots);
        }
        if (Value == "else")
        {
            int elseStart = _pos++;
            var alternative = Add(elseStart, "else", declaration, ["else"]);
            Block(alternative);
            Finish(alternative, elseStart);
        }
        Finish(declaration, start);
    }

    private void Control(StatementInfo? parent)
    {
        int start = _pos++;
        string keyword = tokens[start].Value;
        var conditionRoots = new List<StatementInfo>();
        var conditionExpressions = new List<ExpressionInfo>();
        int conditionStart = _pos;
        if (keyword != "loop")
        {
            // `if let`/`while let` and `for PAT in EXPR` contain patterns, not calls.
            if (Value == "let" || keyword == "for")
            {
                string separator = keyword == "for" ? "in" : "=";
                while (!AtEnd && Value != separator && Value != "{")
                {
                    if (_pairs.TryGetValue(_pos, out int close)) _pos = close + 1;
                    else _pos++;
                }
                if (Value == separator) _pos++;
            }
            while (!AtEnd && Value != "{")
            {
                int before = _pos;
                var expression = ReadExpression(stopAtBrace: true);
                conditionRoots.AddRange(expression.Roots);
                conditionExpressions.Add(expression.Info);
                if (before == _pos) _pos++;
            }
        }
        int conditionEnd = _pos;
        string kind = keyword switch { "match" => "switch", "loop" => "while", _ => keyword };
        var statement = Add(start, kind, parent, [keyword],
            condition: Text(conditionStart, conditionEnd), braced: Value == "{");
        statement.ValueExpression = conditionExpressions.Count == 1 ? conditionExpressions[0]
            : conditionExpressions.Count > 1 ? new("other") { Children = conditionExpressions } : null;
        Attach(statement, conditionRoots);
        if (keyword == "match") MatchArms(statement);
        else Block(statement);
        if (keyword == "if" && Value == "else")
        {
            int elseStart = _pos++;
            var alternative = Add(elseStart, "else", parent, ["else"], braced: Value == "{");
            if (Value == "if") Control(alternative);
            else Block(alternative);
            Finish(alternative, elseStart);
        }
        Finish(statement, start);
    }

    private void MatchArms(StatementInfo parent)
    {
        if (Value != "{") return;
        int close = GroupEnd(_pos);
        int savedEnd = _end;
        _end = close;
        _pos++;
        while (!AtEnd)
        {
            int start = _pos;
            while (!AtEnd && Value != "=>")
            {
                // Match guards are expressions; patterns before them are not.
                if (Value == "if")
                {
                    _pos++;
                    Attach(parent, ReadExpression().Roots);
                }
                else if (_pairs.TryGetValue(_pos, out int pair)) _pos = pair + 1;
                else _pos++;
            }
            if (Value == "=>") _pos++;
            if (Value == "{") Block(parent);
            else Attach(parent, ReadExpression().Roots);
            if (Value == ",") _pos++;
            if (_pos == start) _pos++;
        }
        _end = savedEnd;
        _pos = Math.Min(close + 1, _end);
    }

    private void Block(StatementInfo? parent)
    {
        if (Value != "{") return;
        int close = GroupEnd(_pos);
        int savedEnd = _end;
        _end = close;
        _pos++;
        if (++_depth <= 256) ParseSequence(parent);
        else reportNestingLimit(tokens[Math.Max(0, _pos - 1)]);
        _depth--;
        _end = savedEnd;
        _pos = Math.Min(close + 1, _end);
        Trivia();
    }

    private Expression ReadExpression(int minPrecedence = 0, bool stopAtBrace = false)
    {
        Trivia();
        int start = _pos;
        if (AtEnd || Value is ";" or "," or ")" or "]" or "}" or "=>" or "else"
            || (stopAtBrace && Value == "{"))
            return new(new("other"), start, start, []);
        if (++_depth > 256)
        {
            _depth--;
            reportNestingLimit(tokens[_pos]);
            _pos++;
            return new(new("other"), start, _pos, []);
        }
        Expression result = Prefix(stopAtBrace);
        int postfixCount = 0;
        while (!AtEnd)
        {
            Trivia();
            if (++postfixCount > 256)
            {
                reportNestingLimit(tokens[Math.Min(_pos, tokens.Count - 1)]);
                while (!AtEnd && Value is not ";" and not "," and not "}" and not ")" and not "]")
                {
                    if (_pairs.TryGetValue(_pos, out int end)) _pos = end + 1;
                    else _pos++;
                }
                break;
            }
            if (Value is "." or "::")
            {
                string separator = Value;
                _pos++;
                if (Value == "<") { SkipAngles(); continue; }
                if (AtEnd) break;
                string name = Value;
                _pos++;
                if (name == "await" && separator == ".")
                {
                    var awaited = Add(start, "await", null, ["await"]);
                    awaited.ValueExpression = new("await") { Target = result.Info };
                    Attach(awaited, result.Roots);
                    Finish(awaited, start);
                    result = new(new("await") { Target = result.Info }, start, _pos, [awaited]);
                }
                else result = new(new("memberAccess") { Name = name, Target = result.Info },
                    start, _pos, result.Roots);
                continue;
            }
            bool macro = Value == "!" && (Next is "(" or "[" or "{");
            if (Value == "(" || macro)
            {
                if (macro) _pos++;
                int open = _pos;
                int close = GroupEnd(open);
                var args = ReadArguments(close);
                string? member = result.Info.Name;
                if (macro && member is not null) member += "!";
                bool panic = macro && member is "panic!" or "todo!" or "unimplemented!" or "unreachable!";
                var call = new RustStatementInfo(panic ? "throw" : "call", [],
                    ReceiverName(result.Info.Target), member,
                    args.Select(a => Text(a.Start, a.End)).ToList(), tokens[start].Line, true)
                {
                    CallTarget = macro ? result.Info with { Name = member } : result.Info,
                    ArgumentExpressions = args.Select(a => a.Info).ToList(),
                    Expression = Text(start, _pos),
                    Column = Column(tokens[start].Start), EndLine = tokens[_pos - 1].Line,
                    EndColumn = Column(tokens[_pos - 1].End)
                };
                _statements.Add((start, call));
                Attach(call, result.Roots);
                foreach (var arg in args) Attach(call, arg.Roots);
                var invocation = new ExpressionInfo(macro ? "macroInvocation" : "invocation")
                {
                    Target = call.CallTarget,
                    Children = args.Select(a => a.Info).ToList()
                };
                call.ValueExpression = invocation;
                result = new(invocation, start, _pos, [call]);
                continue;
            }
            if (Value == "[")
            {
                int close = GroupEnd(_pos);
                var args = ReadArguments(close);
                result = new(new("elementAccess") { Target = result.Info, Children = args.Select(a => a.Info).ToList() },
                    start, _pos, [..result.Roots, ..args.SelectMany(a => a.Roots)]);
                continue;
            }
            if (Value == "?")
            {
                _pos++;
                result = new(new("try") { Target = result.Info }, start, _pos, result.Roots);
                continue;
            }
            if (Value == "{" && !stopAtBrace && result.Info.Kind is "identifier" or "memberAccess")
            {
                var fields = ReadArguments(GroupEnd(_pos));
                result = new(new("objectCreation") { Target = result.Info, Children = fields.Select(f => f.Info).ToList() },
                    start, _pos, [..result.Roots, ..fields.SelectMany(f => f.Roots)]);
                continue;
            }
            string op = Value;
            int operatorTokens = 1;
            if (Value == Next && Value is "<" or ">")
            {
                op = Value + Next;
                operatorTokens = 2;
                if (_pos + 2 < _end && tokens[_pos + 2].Value == "=")
                { op += "="; operatorTokens++; }
            }
            int precedence = Precedence(op);
            if (precedence < minPrecedence || precedence < 0) break;
            _pos += operatorTokens;
            var right = ReadExpression(precedence + (precedence == 0 ? 0 : 1), stopAtBrace);
            result = new(new("binary") { Name = op, Children = [result.Info, right.Info] },
                start, _pos, [..result.Roots, ..right.Roots]);
        }
        _depth--;
        return result with { End = _pos };
    }

    private Expression Prefix(bool stopAtBrace)
    {
        int start = _pos;
        var token = tokens[_pos++];
        if (token.Value is "&" or "*" or "!" or "-" or "+" or "ref")
        {
            if (token.Value == "&" && Value == "mut") _pos++;
            var operand = ReadExpression(12, stopAtBrace);
            return new(new("unary") { Name = token.Value, Target = operand.Info }, start, _pos, operand.Roots);
        }
        if (token.Value is "move" or "async" or "unsafe" or "const")
        {
            var operand = ReadExpression(0, stopAtBrace);
            return new(new(token.Value) { Target = operand.Info }, start, _pos, operand.Roots);
        }
        if (token.Value is "|" or "||")
        {
            if (token.Value == "|")
            {
                while (!AtEnd && Value != "|") _pos++;
                if (Value == "|") _pos++;
            }
            if (Value == "->")
            {
                _pos++;
                while (!AtEnd && Value != "{") _pos++;
            }
            var body = ReadExpression();
            return new(new("lambda") { Children = [body.Info] }, start, _pos, body.Roots);
        }
        if (token.Value is "if" or "while" or "for" or "loop" or "match")
        {
            _pos--;
            int before = _statements.Count;
            Control(null);
            return new(new("other"), start, _pos,
                _statements.Skip(before).Select(s => s.statement).Where(s => s.Parent is null).ToList());
        }
        if (token.Value == "{")
        {
            _pos--;
            int before = _statements.Count;
            Block(null);
            return new(new("block"), start, _pos,
                _statements.Skip(before).Select(s => s.statement).Where(s => s.Parent is null).ToList());
        }
        if (token.Value is "(" or "[")
        {
            _pos--;
            var args = ReadArguments(GroupEnd(_pos));
            if (token.Value == "(" && args.Count == 1) return args[0] with { Start = start, End = _pos };
            return new(new(token.Value == "(" ? "tuple" : "array") { Children = args.Select(a => a.Info).ToList() },
                start, _pos, args.SelectMany(a => a.Roots).ToList());
        }
        if (token.Value == "<")
        {
            _pos--;
            SkipAngles();
            return new(new("type") { Name = Text(start, _pos) }, start, _pos, []);
        }
        if (token.Kind == RustTokenKind.StringLiteral)
        {
            string? decoded = DecodeString(token.Value);
            return new(new(decoded is not null ? "stringLiteral" : "literal")
            { StringValue = decoded }, start, _pos, []);
        }
        if (token.Kind == RustTokenKind.NumberLiteral || token.Value is "true" or "false")
            return new(new("literal") { Name = token.Value }, start, _pos, []);
        return new(new(token.Kind is RustTokenKind.Identifier || token.Value is "self" or "Self" or "crate" or "super"
            ? "identifier" : "other") { Name = token.Value }, start, _pos, []);
    }

    private List<Expression> ReadArguments(int close)
    {
        int savedEnd = _end;
        _end = close;
        _pos++;
        var arguments = new List<Expression>();
        while (!AtEnd)
        {
            Trivia();
            if (AtEnd) break;
            int before = _pos;
            if (Value is "," or ";") { _pos++; continue; }
            // Named struct fields are labels, not expressions/call targets.
            if (Next == ":" && tokens[_pos].Kind == RustTokenKind.Identifier) _pos += 2;
            var expression = ReadExpression();
            arguments.Add(expression);
            if (_pos == before) _pos++;
        }
        _end = savedEnd;
        _pos = Math.Min(close + 1, _end);
        return arguments;
    }

    private StatementInfo Add(int position, string kind, StatementInfo? parent, List<string> keywords,
        string? condition = null, bool braced = false, string? member = null)
    {
        int end = StatementEnd(position);
        var statement = new RustStatementInfo(kind, keywords, null, member, [], tokens[position].Line, true)
        {
            Condition = condition, IsBraced = braced, Column = Column(tokens[position].Start),
            EndLine = tokens[end].Line, EndColumn = Column(tokens[end].End)
        };
        _statements.Add((position, statement));
        Attach(parent, [statement]);
        return statement;
    }

    private void Finish(StatementInfo statement, int start)
    {
        int last = Value == ";" ? _pos : Math.Max(start, _pos - 1);
        statement.EndLine = tokens[last].Line;
        statement.EndColumn = Column(tokens[last].End);
        statement.Expression = Text(start, _pos);
    }

    private static void Attach(StatementInfo? parent, IEnumerable<StatementInfo> children)
    {
        if (parent is null) return;
        foreach (var child in children)
        {
            if (ReferenceEquals(child, parent) || child.Parent is not null) continue;
            child.Parent = parent;
            parent._children.Add(child);
        }
    }

    private int GroupEnd(int open) =>
        _pairs.TryGetValue(open, out int close) && close < _end ? close : _end;

    private void SkipGroup() => _pos = Math.Min(GroupEnd(_pos) + 1, _end);

    private void SkipAngles()
    {
        int depth = 0;
        while (!AtEnd)
        {
            string value = Value;
            _pos++;
            if (value == "<") depth++;
            if (value == ">" && --depth == 0) break;
        }
    }

    private string Text(int start, int end) => start < end && start < tokens.Count
        ? source[tokens[start].Start..tokens[Math.Min(end, tokens.Count) - 1].End] : "";

    private int Column(int position)
    {
        int newline = source.LastIndexOf('\n', Math.Max(0, position - 1));
        return position - newline;
    }

    private int StatementEnd(int start)
    {
        int position = start;
        while (position < _end)
        {
            if (tokens[position].Value == ";") return position;
            if (_pairs.TryGetValue(position, out int close))
            {
                if (tokens[position].Value == "{") return close;
                position = close + 1;
            }
            else position++;
        }
        return Math.Max(start, _end - 1);
    }

    private static string? ReceiverName(ExpressionInfo? expression) => expression?.Kind switch
    {
        "identifier" => expression.Name,
        "memberAccess" => ReceiverName(expression.Target) is { } target ? target + "::" + expression.Name : null,
        _ => null
    };

    private static int Precedence(string value) => value switch
    {
        "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=" => 0,
        ".." or "..=" => 1,
        "||" => 2,
        "&&" => 3,
        "==" or "!=" or "<" or ">" or "<=" or ">=" => 4,
        "|" => 5, "^" => 6, "&" => 7,
        "<<" or ">>" => 8,
        "+" or "-" => 9,
        "*" or "/" or "%" => 10,
        "as" => 11,
        _ => -1
    };

    internal static Dictionary<int, int> BuildPairs(List<RustToken> tokens)
    {
        var pairs = new Dictionary<int, int>();
        var stack = new Stack<int>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != RustTokenKind.Punctuation) continue;
            if (tokens[i].Value is "(" or "[" or "{") stack.Push(i);
            else if (tokens[i].Value is ")" or "]" or "}" && stack.TryPop(out int open))
                pairs[open] = i;
        }
        return pairs;
    }

    internal static string? DecodeString(string text)
    {
        if (text.StartsWith('r'))
        {
            int quote = text.IndexOf('"');
            int last = text.LastIndexOf('"');
            return quote >= 0 && last > quote ? text[(quote + 1)..last] : null;
        }
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"') return null;
        var value = new StringBuilder();
        for (int i = 1; i < text.Length - 1; i++)
        {
            char c = text[i];
            if (c != '\\') { value.Append(c); continue; }
            if (++i >= text.Length - 1) return null;
            c = text[i];
            switch (c)
            {
                case 'n': value.Append('\n'); break;
                case 'r': value.Append('\r'); break;
                case 't': value.Append('\t'); break;
                case '0': value.Append('\0'); break;
                case '\\': case '"': case '\'': value.Append(c); break;
                case 'x':
                    if (i + 2 >= text.Length - 1 || !byte.TryParse(text.AsSpan(i + 1, 2),
                        NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)) return null;
                    value.Append((char)b);
                    i += 2;
                    break;
                case 'u':
                    if (i + 1 >= text.Length || text[i + 1] != '{') return null;
                    int close = text.IndexOf('}', i + 2);
                    if (close < 0 || !int.TryParse(text[(i + 2)..close].Replace("_", ""),
                        NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int scalar) || !Rune.IsValid(scalar)) return null;
                    value.Append(char.ConvertFromUtf32(scalar));
                    i = close;
                    break;
                case '\r': case '\n':
                    while (i + 1 < text.Length - 1 && char.IsWhiteSpace(text[i + 1])) i++;
                    break;
                default: return null;
            }
        }
        return value.ToString();
    }
}
