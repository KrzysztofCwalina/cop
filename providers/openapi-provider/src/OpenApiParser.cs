namespace Cop.Providers.OpenApi;

/// <summary>
/// Small dependency-free parser focused on extracting paths and operations from OpenAPI
/// YAML specs. JSON is parsed with a minimal hand-rolled scanner for common object-shaped specs.
/// </summary>
public static class OpenApiParser
{
    public record OpenApiParseResult(List<OpenApiOperationInfo> Operations, List<OpenApiPathInfo> Paths);

    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "delete", "patch", "head", "options"
    };

    public static OpenApiParseResult ParseYaml(string text)
    {
        if (!HasYamlOpenApiMarker(text))
            return Empty();

        var operations = new List<OpenApiOperationInfo>();
        var paths = new List<OpenApiPathInfo>();
        var lines = text.Split('\n');

        bool inPaths = false;
        int pathsIndent = -1;
        int? pathIndent = null;
        string currentPath = "";
        int currentPathIndent = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var contentTrim = raw.TrimStart(' ');
            if (contentTrim.Length == 0 || contentTrim[0] == '#')
                continue;

            int indent = raw.Length - contentTrim.Length;
            var work = StripInlineComment(contentTrim);
            if (work.Length == 0 || !TrySplitKeyValue(work, out var key, out _))
                continue;

            if (indent == 0 && key.Equals("paths", StringComparison.Ordinal))
            {
                inPaths = true;
                pathsIndent = indent;
                pathIndent = null;
                currentPath = "";
                currentPathIndent = -1;
                continue;
            }

            if (!inPaths)
                continue;

            if (indent <= pathsIndent)
            {
                inPaths = false;
                pathIndent = null;
                currentPath = "";
                currentPathIndent = -1;
                continue;
            }

            if (key.StartsWith("/", StringComparison.Ordinal)
                && (pathIndent is null || indent == pathIndent.Value))
            {
                pathIndent ??= indent;
                currentPath = key;
                currentPathIndent = indent;
                paths.Add(new OpenApiPathInfo(currentPath, i + 1));
                continue;
            }

            if (currentPath.Length == 0 || indent <= currentPathIndent || !IsHttpMethod(key))
                continue;

            var details = ScanYamlOperation(lines, i, indent);
            operations.Add(new OpenApiOperationInfo(
                key.ToUpperInvariant(),
                currentPath,
                details.OperationId,
                details.HasSummary,
                details.HasResponses,
                i + 1));
        }

        return new OpenApiParseResult(operations, paths);
    }

    public static OpenApiParseResult ParseJson(string text)
    {
        try
        {
            var parser = new JsonScanner(text);
            return parser.Parse();
        }
        catch
        {
            return Empty();
        }
    }

    private static OpenApiParseResult Empty() => new([], []);

    private static bool HasYamlOpenApiMarker(string text)
    {
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var raw = line.TrimEnd('\r');
            var contentTrim = raw.TrimStart(' ');
            if (contentTrim.Length == 0 || contentTrim[0] == '#' || contentTrim.StartsWith("---", StringComparison.Ordinal))
                continue;

            int indent = raw.Length - contentTrim.Length;
            var work = StripInlineComment(contentTrim);
            if (indent == 0
                && TrySplitKeyValue(work, out var key, out _)
                && (key.Equals("openapi", StringComparison.Ordinal) || key.Equals("swagger", StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    private static (string OperationId, bool HasSummary, bool HasResponses) ScanYamlOperation(string[] lines, int operationIndex, int operationIndent)
    {
        string operationId = "";
        bool hasSummary = false;
        bool hasResponses = false;
        int? childIndent = null;

        for (int j = operationIndex + 1; j < lines.Length; j++)
        {
            var raw = lines[j].TrimEnd('\r');
            var contentTrim = raw.TrimStart(' ');
            if (contentTrim.Length == 0 || contentTrim[0] == '#')
                continue;

            int indent = raw.Length - contentTrim.Length;
            if (indent <= operationIndent)
                break;

            var work = StripInlineComment(contentTrim);
            if (!TrySplitKeyValue(work, out var key, out var value))
                continue;

            childIndent ??= indent;
            if (indent != childIndent.Value)
                continue;

            if (key.Equals("operationId", StringComparison.Ordinal))
                operationId = value;
            else if (key.Equals("summary", StringComparison.Ordinal) || key.Equals("description", StringComparison.Ordinal))
                hasSummary = true;
            else if (key.Equals("responses", StringComparison.Ordinal))
                hasResponses = true;
        }

        return (operationId, hasSummary, hasResponses);
    }

    private static bool IsHttpMethod(string key) => HttpMethods.Contains(key);

    private static bool TrySplitKeyValue(string s, out string key, out string value)
    {
        key = "";
        value = "";
        int colon = FindMappingColon(s);
        if (colon < 0) return false;

        key = Unquote(s[..colon].TrimEnd(' '));
        if (key.Length == 0) return false;
        var rest = s[(colon + 1)..].Trim(' ');
        value = Unquote(rest);
        return true;
    }

    private static int FindMappingColon(string s)
    {
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '\'' || c == '"') { quote = c; continue; }
            if (c == ':' && (i + 1 == s.Length || s[i + 1] == ' '))
                return i;
        }
        return -1;
    }

    private static string StripInlineComment(string s)
    {
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '\'' || c == '"') { quote = c; continue; }
            if (c == '#' && (i == 0 || s[i - 1] == ' ' || s[i - 1] == '\t'))
                return s[..i].TrimEnd(' ');
        }
        return s;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2)
        {
            char a = s[0], b = s[^1];
            if ((a == '"' && b == '"') || (a == '\'' && b == '\''))
                return s[1..^1];
        }
        return s;
    }

    private sealed class JsonScanner
    {
        private readonly string _text;
        private int _pos;
        private readonly List<OpenApiOperationInfo> _operations = [];
        private readonly List<OpenApiPathInfo> _paths = [];
        private bool _hasMarker;

        public JsonScanner(string text) => _text = text;

        public OpenApiParseResult Parse()
        {
            SkipWhitespace();
            if (!TryConsume('{'))
                return Empty();

            while (ReadProperty(out var name, out var line))
            {
                if (name.Equals("openapi", StringComparison.Ordinal) || name.Equals("swagger", StringComparison.Ordinal))
                    _hasMarker = true;

                if (name.Equals("paths", StringComparison.Ordinal) && Peek() == '{')
                    ParsePathsObject();
                else
                    SkipValue();

                if (!ConsumeCommaOrEnd('}'))
                    break;
            }

            return _hasMarker ? new OpenApiParseResult(_operations, _paths) : Empty();
        }

        private void ParsePathsObject()
        {
            if (!TryConsume('{')) return;

            while (ReadProperty(out var path, out var line))
            {
                if (path.StartsWith("/", StringComparison.Ordinal) && Peek() == '{')
                {
                    _paths.Add(new OpenApiPathInfo(path, line));
                    ParsePathItemObject(path);
                }
                else
                {
                    SkipValue();
                }

                if (!ConsumeCommaOrEnd('}'))
                    break;
            }
        }

        private void ParsePathItemObject(string path)
        {
            if (!TryConsume('{')) return;

            while (ReadProperty(out var method, out var line))
            {
                if (IsHttpMethod(method) && Peek() == '{')
                {
                    var details = ParseOperationObject();
                    _operations.Add(new OpenApiOperationInfo(
                        method.ToUpperInvariant(),
                        path,
                        details.OperationId,
                        details.HasSummary,
                        details.HasResponses,
                        line));
                }
                else
                {
                    SkipValue();
                }

                if (!ConsumeCommaOrEnd('}'))
                    break;
            }
        }

        private (string OperationId, bool HasSummary, bool HasResponses) ParseOperationObject()
        {
            string operationId = "";
            bool hasSummary = false;
            bool hasResponses = false;

            if (!TryConsume('{'))
                return (operationId, hasSummary, hasResponses);

            while (ReadProperty(out var name, out _))
            {
                if (name.Equals("operationId", StringComparison.Ordinal) && Peek() == '"')
                    operationId = ParseString();
                else
                {
                    if (name.Equals("summary", StringComparison.Ordinal) || name.Equals("description", StringComparison.Ordinal))
                        hasSummary = true;
                    else if (name.Equals("responses", StringComparison.Ordinal))
                        hasResponses = true;
                    SkipValue();
                }

                if (!ConsumeCommaOrEnd('}'))
                    break;
            }

            return (operationId, hasSummary, hasResponses);
        }

        private bool ReadProperty(out string name, out int line)
        {
            name = "";
            line = 0;
            SkipWhitespace();
            if (Peek() == '}') return false;
            if (Peek() != '"') return false;
            line = LineAt(_pos);
            name = ParseString();
            SkipWhitespace();
            return TryConsume(':');
        }

        private void SkipValue()
        {
            SkipWhitespace();
            char c = Peek();
            if (c == '"')
            {
                ParseString();
                return;
            }
            if (c == '{')
            {
                SkipComposite('{', '}');
                return;
            }
            if (c == '[')
            {
                SkipComposite('[', ']');
                return;
            }
            while (_pos < _text.Length && _text[_pos] != ',' && _text[_pos] != '}' && _text[_pos] != ']')
                _pos++;
        }

        private void SkipComposite(char open, char close)
        {
            if (!TryConsume(open)) return;
            int depth = 1;
            while (_pos < _text.Length && depth > 0)
            {
                char c = _text[_pos];
                if (c == '"')
                {
                    ParseString();
                    continue;
                }
                if (c == open) depth++;
                else if (c == close) depth--;
                _pos++;
            }
        }

        private string ParseString()
        {
            if (!TryConsume('"')) return "";
            var sb = new System.Text.StringBuilder();
            while (_pos < _text.Length)
            {
                char c = _text[_pos++];
                if (c == '"') break;
                if (c == '\\' && _pos < _text.Length)
                {
                    char escaped = _text[_pos++];
                    sb.Append(escaped switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        '/' => '/',
                        'b' => '\b',
                        'f' => '\f',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => escaped
                    });
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private bool ConsumeCommaOrEnd(char end)
        {
            SkipWhitespace();
            if (TryConsume(',')) return true;
            if (TryConsume(end)) return false;
            return false;
        }

        private bool TryConsume(char c)
        {
            SkipWhitespace();
            if (_pos >= _text.Length || _text[_pos] != c) return false;
            _pos++;
            return true;
        }

        private char Peek()
        {
            SkipWhitespace();
            return _pos < _text.Length ? _text[_pos] : '\0';
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                _pos++;
        }

        private int LineAt(int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < _text.Length; i++)
                if (_text[i] == '\n') line++;
            return line;
        }
    }
}

