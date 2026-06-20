using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class PythonSourceParser : ISourceParser
{
    public override IReadOnlyList<string> Extensions => [".py"];
    public override string Language => "python";

    public override SourceFile? Parse(string filePath, string sourceText)
    {
        var lines = sourceText.Split('\n');
        var types = new List<TypeDeclaration>();
        var statements = new List<StatementInfo>();
        var usings = new List<string>();
        bool inTripleQuote = false;

        int i = 0;
        while (i < lines.Length)
        {
            // Track triple-quoted string regions
            if (IsTripleQuoteToggle(lines[i], ref inTripleQuote))
            {
                i++;
                continue;
            }
            if (inTripleQuote) { i++; continue; }

            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('#') || string.IsNullOrWhiteSpace(trimmed))
            {
                i++;
                continue;
            }

            if (trimmed.StartsWith("class "))
            {
                var (type, nextLine) = ParseClass(lines, i, statements);
                if (type != null) types.Add(type);
                i = nextLine;
            }
            else if (trimmed.StartsWith("def ") || trimmed.StartsWith("async def "))
            {
                // Top-level function
                int indent = lines[i].Length - trimmed.Length;
                var (method, nextLine) = ParseMethod(lines, i, indent, statements);
                i = nextLine;
            }
            else
            {
                // Extract import statements
                if (trimmed.StartsWith("import "))
                {
                    var modules = trimmed["import ".Length..].Split(',', StringSplitOptions.TrimEntries);
                    foreach (var m in modules)
                    {
                        var name = m.Split(" as ")[0].Trim();
                        if (!string.IsNullOrWhiteSpace(name)) usings.Add(name);
                    }
                }
                else if (trimmed.StartsWith("from "))
                {
                    var moduleName = ParseFromImportModule(trimmed);
                    if (moduleName is not null) usings.Add(moduleName);
                }
                else
                {
                    // Module-level statements (invocations, etc.)
                    ExtractLineStatement(trimmed, i + 1, false, statements);
                }
                i++;
            }
        }

        return new SourceFile(filePath, "python", types, statements, sourceText)
        {
            Usings = usings,
            Regions = ExtractRegions(lines),
            CommentLines = ExtractCommentLines(lines)
        };
    }

    private static (TypeDeclaration?, int) ParseClass(string[] lines, int startLine, List<StatementInfo> statements)
    {
        int classIndent = lines[startLine].Length - lines[startLine].TrimStart().Length;

        var decorators = CollectDecorators(lines, startLine);

        var classMatch = ParseClassHeader(lines[startLine].TrimStart());
        if (classMatch is null) return (null, startLine + 1);

        string className = classMatch.Value.Name;
        var baseTypes = classMatch.Value.BaseTypes is not null
            ? classMatch.Value.BaseTypes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];

        var methods = new List<MethodDeclaration>();
        var constructors = new List<MethodDeclaration>();

        int i = startLine + 1;

        // Detect class docstring (first non-blank line after class: is a triple-quoted string)
        bool hasDocstring = false;
        int docCheckLine = i;
        while (docCheckLine < lines.Length && string.IsNullOrWhiteSpace(lines[docCheckLine])) docCheckLine++;
        if (docCheckLine < lines.Length)
        {
            var docTrimmed = lines[docCheckLine].TrimStart();
            if (docTrimmed.StartsWith("\"\"\"") || docTrimmed.StartsWith("'''"))
                hasDocstring = true;
        }

        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }
            int indent = lines[i].Length - lines[i].TrimStart().Length;
            if (indent <= classIndent) break;

            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("def ") || trimmed.StartsWith("async def "))
            {
                var (method, nextLine) = ParseMethod(lines, i, indent, statements);
                if (method != null)
                {
                    if (method.Name == "__init__")
                        constructors.Add(method);
                    else
                        methods.Add(method);
                }
                i = nextLine;
            }
            else
            {
                i++;
            }
        }

        return (new TypeDeclaration(className, TypeKind.Class, Modifier.Public,
            baseTypes, decorators, constructors, methods, [], [], startLine + 1)
        { HasDocComment = hasDocstring }
            .AsPython(
                isDataclass: decorators.Exists(d => d.Contains("dataclass")),
                isEnum: baseTypes.Exists(b => b is "Enum" or "IntEnum" or "StrEnum" or "Flag" or "IntFlag")), i);
    }

    private static (MethodDeclaration?, int) ParseMethod(string[] lines, int startLine, int methodIndent,
        List<StatementInfo> statements)
    {
        var line = lines[startLine].TrimStart();
        bool isAsync = line.StartsWith("async ");
        if (isAsync) line = line["async ".Length..];

        var decorators = CollectDecorators(lines, startLine);

        // Join multi-line def
        string fullDef = line;
        int nextLine = startLine + 1;
        while (!fullDef.Contains("):") && !fullDef.Contains(") ->") && nextLine < lines.Length)
        {
            fullDef += " " + lines[nextLine].Trim();
            nextLine++;
        }

        var defMatch = ParseDefHeader(fullDef);
        if (defMatch is null) return (null, nextLine);

        string methodName = defMatch.Value.Name;
        var parameters = ParseParameters(defMatch.Value.Parameters);

        var modifiers = Modifier.None;
        if (isAsync) modifiers |= Modifier.Async;
        if (decorators.Contains("staticmethod")) modifiers |= Modifier.Static;
        if (decorators.Contains("abstractmethod")) modifiers |= Modifier.Abstract;
        if (!methodName.StartsWith("_")) modifiers |= Modifier.Public;
        else modifiers |= Modifier.Private;

        // Extract statements from method body — collect per-method and add to global list
        int bodyStart = nextLine;

        // Detect method docstring
        bool hasDocstring = false;
        int docLine = bodyStart;
        while (docLine < lines.Length && string.IsNullOrWhiteSpace(lines[docLine])) docLine++;
        if (docLine < lines.Length)
        {
            var docTrimmed = lines[docLine].TrimStart();
            if (docTrimmed.StartsWith("\"\"\"") || docTrimmed.StartsWith("'''"))
                hasDocstring = true;
        }

        while (nextLine < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[nextLine])) { nextLine++; continue; }
            int indent = lines[nextLine].Length - lines[nextLine].TrimStart().Length;
            if (indent <= methodIndent) break;
            nextLine++;
        }
        var methodStatements = new List<StatementInfo>();
        ExtractBodyStatements(lines, bodyStart, nextLine, methodIndent, methodStatements, isInMethod: true);
        statements.AddRange(methodStatements);

        string? returnType = null;
        returnType = ParseReturnType(fullDef);

        var retRef = returnType != null ? new TypeReference(returnType, null, [], returnType) : null;
        return (new MethodDeclaration(methodName, modifiers, decorators,
            retRef, parameters, startLine + 1) { Statements = methodStatements, HasDocComment = hasDocstring }, nextLine);
    }

    /// <summary>
    /// Extract statements from a block of code lines (method body or except body).
    /// </summary>
    private static void ExtractBodyStatements(string[] lines, int start, int end, int parentIndent,
        List<StatementInfo> statements, bool isInMethod)
    {
        bool inTripleQuote = false;
        for (int i = start; i < end; i++)
        {
            if (IsTripleQuoteToggle(lines[i], ref inTripleQuote)) continue;
            if (inTripleQuote) continue;

            var trimmed = lines[i].TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;

            int lineIndent = lines[i].Length - trimmed.Length;
            if (lineIndent <= parentIndent) continue;

            // except clauses
            if (trimmed.StartsWith("except") && (trimmed.Length == 6 || trimmed[6] is ' ' or ':'))
            {
                string? caughtType = null;
                var exceptMatch = ParseExceptClause(trimmed);
                if (exceptMatch.Success)
                {
                    // Group 1: tuple form (Foo, Bar), Group 2: single type
                    caughtType = exceptMatch.TupleTypes is not null
                        ? exceptMatch.TupleTypes.Split(',')[0].Trim()
                        : exceptMatch.SingleType;
                }

                // Check for bare raise in the except body (same indentation level as the except block's children)
                bool hasRethrow = HasBareRaise(lines, i + 1, end, lineIndent);

                statements.Add(new StatementInfo("catch", [], caughtType, null, [], i + 1, isInMethod)
                {
                    HasRethrow = hasRethrow,
                    IsErrorHandler = true,
                    IsGenericErrorHandler = caughtType is null or "Exception" or "BaseException"
                });
                continue;
            }

            ExtractLineStatement(trimmed, i + 1, isInMethod, statements);
        }
    }

    /// <summary>
    /// Check if there's a bare 'raise' (without arguments) in the block following an except clause.
    /// Only checks lines at the immediate child indent level.
    /// </summary>
    private static bool HasBareRaise(string[] lines, int start, int end, int exceptIndent)
    {
        for (int i = start; i < end; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            int indent = lines[i].Length - lines[i].TrimStart().Length;
            if (indent <= exceptIndent) break;
            var trimmed = lines[i].TrimStart();
            if (trimmed == "raise" || trimmed.StartsWith("raise #") || trimmed.StartsWith("raise\r"))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Extract a statement from a single line of code (invocations, raise, etc.).
    /// </summary>
    private static void ExtractLineStatement(string trimmed, int lineNumber, bool isInMethod,
        List<StatementInfo> statements)
    {
        // raise with type: raise SomeException(...)
        if (trimmed.StartsWith("raise "))
        {
            string? typeName = ParseRaisedType(trimmed);
            statements.Add(new StatementInfo("throw", [], typeName, null, [], lineNumber, isInMethod));
            return;
        }
        // bare raise (re-raise)
        if (trimmed is "raise" or "raise\r")
        {
            statements.Add(new StatementInfo("throw", [], null, null, [], lineNumber, isInMethod));
            return;
        }

        // Function/method call: name(...) or module.name(...)
        var callMatch = ParseCall(trimmed);
        if (callMatch.Success)
        {
            string? typeName = callMatch.TypeName;
            string memberName = callMatch.MemberName!;

            // Skip control flow keywords that look like calls
            if (memberName is "if" or "for" or "while" or "with" or "elif" or "def" or "class"
                or "return" or "assert" or "del" or "except" or "raise" or "yield" or "import" or "from")
                return;

            // Extract simple arguments
            var argsText = ParseParenthesizedArguments(trimmed);
            var args = argsText is not null && !string.IsNullOrWhiteSpace(argsText)
                ? argsText.Split(',', StringSplitOptions.TrimEntries).ToList()
                : new List<string>();

            statements.Add(new StatementInfo("call", [], typeName, memberName, args, lineNumber, isInMethod));
        }
    }

    /// <summary>
    /// Checks if a line toggles a triple-quoted string region.
    /// Returns true if the line is a pure triple-quote boundary (e.g., docstring delimiters).
    /// </summary>
    private static bool IsTripleQuoteToggle(string line, ref bool inTripleQuote)
    {
        var trimmed = line.TrimStart();
        int count = CountTripleQuotes(trimmed);
        if (count > 0 && count % 2 == 1)
        {
            inTripleQuote = !inTripleQuote;
            return true;
        }
        if (count >= 2)
            return true; // Single-line docstring like """text"""
        return false;
    }

    private static int CountTripleQuotes(string line)
    {
        int count = 0;
        int i = 0;
        while (i < line.Length - 2)
        {
            if ((line[i] == '"' && line[i + 1] == '"' && line[i + 2] == '"') ||
                (line[i] == '\'' && line[i + 1] == '\'' && line[i + 2] == '\''))
            {
                count++;
                i += 3;
            }
            else
            {
                i++;
            }
        }
        return count;
    }

    private static List<string> CollectDecorators(string[] lines, int startLine)
    {
        var decorators = new List<string>();
        for (int d = startLine - 1; d >= 0; d--)
        {
            var trimmed = lines[d].TrimStart();
            if (trimmed.StartsWith("@"))
                decorators.Insert(0, trimmed[1..].Split('(')[0].Trim());
            else if (string.IsNullOrWhiteSpace(lines[d]))
                continue;
            else
                break;
        }
        return decorators;
    }

    private static List<ParameterDeclaration> ParseParameters(string paramString)
    {
        var parameters = new List<ParameterDeclaration>();
        if (string.IsNullOrWhiteSpace(paramString)) return parameters;

        foreach (var part in SplitParameters(paramString))
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed == "self" || trimmed == "cls") continue;

            bool isKwargs = trimmed.StartsWith("**");
            bool isVariadic = !isKwargs && trimmed.StartsWith("*");
            if (isKwargs) trimmed = trimmed[2..];
            else if (isVariadic) trimmed = trimmed[1..];

            var colonIdx = trimmed.IndexOf(':');
            string name;
            string? type = null;
            if (colonIdx > 0)
            {
                name = trimmed[..colonIdx].Trim();
                var afterColon = trimmed[(colonIdx + 1)..];
                var eqIdx = afterColon.IndexOf('=');
                type = (eqIdx > 0 ? afterColon[..eqIdx] : afterColon).Trim();
            }
            else
            {
                var eqIdx = trimmed.IndexOf('=');
                name = (eqIdx > 0 ? trimmed[..eqIdx] : trimmed).Trim();
            }

            var typeRef = type != null ? new TypeReference(type, null, [], type) : null;
            parameters.Add(new ParameterDeclaration(name, typeRef, isVariadic, isKwargs, false, 0));
        }

        return parameters;
    }

    private static List<string> SplitParameters(string s)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] is '(' or '[' or '{') depth++;
            else if (s[i] is ')' or ']' or '}') depth--;
            else if (s[i] == ',' && depth == 0)
            {
                result.Add(s[start..i]);
                start = i + 1;
            }
        }
        result.Add(s[start..]);
        return result;
    }

    // Extracts regions from # [START name] / # [END name] comment markers
    private static List<RegionInfo> ExtractRegions(string[] lines)
    {
        var regions = new List<RegionInfo>();
        var stack = new Stack<(string Name, int Line)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("# [START"))
            {
                var name = ParseRegionMarker(trimmed, "START");
                if (name is not null)
                    stack.Push((name, i + 1));
            }
            else if (trimmed.StartsWith("# [END") && stack.Count > 0)
            {
                var endName = ParseRegionMarker(trimmed, "END");
                if (endName is not null)
                {
                    // Pop matching region from stack
                    var items = new List<(string Name, int Line)>();
                    while (stack.Count > 0)
                    {
                        var top = stack.Pop();
                        if (top.Name == endName)
                        {
                            int startLine = top.Line;
                            int endLine = i + 1;
                            var contentLines = new List<string>();
                            for (int j = startLine; j < endLine - 1 && j < lines.Length; j++)
                                contentLines.Add(lines[j].TrimEnd('\r'));
                            var content = string.Join('\n', contentLines);
                            regions.Add(new RegionInfo(endName, startLine, endLine, content));
                            // Push back any items that weren't the match
                            for (int k = items.Count - 1; k >= 0; k--)
                                stack.Push(items[k]);
                            break;
                        }
                        items.Add(top);
                    }
                }
            }
        }

        return regions;
    }

    private static string? ParseFromImportModule(string text)
    {
        var index = "from".Length;
        if (!text.StartsWith("from", StringComparison.Ordinal) || !HasWhitespaceAt(text, index))
            return null;

        index = SkipWhitespace(text, index);
        var start = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
            index++;
        if (index == start || !HasWhitespaceAt(text, index))
            return null;

        var module = text[start..index];
        index = SkipWhitespace(text, index);
        return StartsWithAt(text, index, "import") ? module : null;
    }

    private static (string Name, string? BaseTypes)? ParseClassHeader(string text)
    {
        var classIndex = text.IndexOf("class", StringComparison.Ordinal);
        if (classIndex < 0)
            return null;

        var index = classIndex + "class".Length;
        if (!HasWhitespaceAt(text, index))
            return null;
        index = SkipWhitespace(text, index);

        var nameStart = index;
        while (index < text.Length && IsWordChar(text[index]))
            index++;
        if (index == nameStart)
            return null;

        var name = text[nameStart..index];
        index = SkipWhitespace(text, index);

        string? baseTypes = null;
        if (index < text.Length && text[index] == '(')
        {
            var baseStart = index + 1;
            var close = text.IndexOf(')', baseStart);
            if (close < 0)
                return null;
            baseTypes = text[baseStart..close];
            index = close + 1;
            index = SkipWhitespace(text, index);
        }

        return index < text.Length && text[index] == ':' ? (name, baseTypes) : null;
    }

    private static (string Name, string Parameters)? ParseDefHeader(string text)
    {
        var defIndex = text.IndexOf("def", StringComparison.Ordinal);
        if (defIndex < 0)
            return null;

        var index = defIndex + "def".Length;
        if (!HasWhitespaceAt(text, index))
            return null;
        index = SkipWhitespace(text, index);

        var nameStart = index;
        while (index < text.Length && IsWordChar(text[index]))
            index++;
        if (index == nameStart)
            return null;

        var name = text[nameStart..index];
        index = SkipWhitespace(text, index);
        if (index >= text.Length || text[index] != '(')
            return null;

        var parametersStart = index + 1;
        var close = text.IndexOf(')', parametersStart);
        return close >= 0 ? (name, text[parametersStart..close]) : null;
    }

    private static string? ParseReturnType(string text)
    {
        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var closeParen = text.IndexOf(')', searchIndex);
            if (closeParen < 0)
                return null;

            var index = SkipWhitespace(text, closeParen + 1);
            if (!StartsWithAt(text, index, "->"))
            {
                searchIndex = closeParen + 1;
                continue;
            }

            index = SkipWhitespace(text, index + 2);
            var typeStart = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
                index++;

            for (var end = index; end > typeStart; end--)
            {
                var afterType = SkipWhitespace(text, end);
                if (afterType < text.Length && text[afterType] == ':')
                    return text[typeStart..end];
            }

            searchIndex = closeParen + 1;
        }

        return null;
    }

    private static (bool Success, string? TupleTypes, string? SingleType) ParseExceptClause(string text)
    {
        var index = "except".Length;
        if (!text.StartsWith("except", StringComparison.Ordinal))
            return (false, null, null);

        index = SkipWhitespace(text, index);
        string? tupleTypes = null;
        string? singleType = null;

        if (index < text.Length && text[index] == '(')
        {
            var tupleStart = index + 1;
            var close = text.IndexOf(')', tupleStart);
            if (close < 0 || close == tupleStart)
                return (false, null, null);
            tupleTypes = text[tupleStart..close];
            index = close + 1;
        }
        else if (index < text.Length && IsWordChar(text[index]))
        {
            var typeStart = index;
            index++;
            while (index < text.Length && (IsWordChar(text[index]) || text[index] == '.'))
                index++;
            singleType = text[typeStart..index];
        }

        if (HasWhitespaceAt(text, index))
        {
            var asIndex = SkipWhitespace(text, index);
            if (StartsWithAt(text, asIndex, "as") && HasWhitespaceAt(text, asIndex + "as".Length))
            {
                var nameIndex = SkipWhitespace(text, asIndex + "as".Length);
                if (nameIndex >= text.Length || !IsWordChar(text[nameIndex]))
                    return (false, null, null);
                nameIndex++;
                while (nameIndex < text.Length && IsWordChar(text[nameIndex]))
                    nameIndex++;
                index = nameIndex;
            }
        }

        index = SkipWhitespace(text, index);
        return index < text.Length && text[index] == ':'
            ? (true, tupleTypes, singleType)
            : (false, null, null);
    }

    private static string? ParseRaisedType(string text)
    {
        var index = "raise".Length;
        if (!text.StartsWith("raise", StringComparison.Ordinal) || !HasWhitespaceAt(text, index))
            return null;

        index = SkipWhitespace(text, index);
        var start = index;
        while (index < text.Length && IsWordChar(text[index]))
            index++;

        return index > start ? text[start..index] : null;
    }

    private static (bool Success, string? TypeName, string? MemberName) ParseCall(string text)
    {
        var withPrefix = ParseCallCore(text, allowAwait: true);
        return withPrefix.Success ? withPrefix : ParseCallCore(text, allowAwait: false);
    }

    private static (bool Success, string? TypeName, string? MemberName) ParseCallCore(string text, bool allowAwait)
    {
        var index = 0;
        if (allowAwait && StartsWithAt(text, 0, "await") && HasWhitespaceAt(text, "await".Length))
            index = SkipWhitespace(text, "await".Length);

        var tokenStart = index;
        if (index >= text.Length || !IsWordChar(text[index]))
            return (false, null, null);

        while (index < text.Length && (IsWordChar(text[index]) || text[index] == '.'))
            index++;

        var token = text[tokenStart..index];
        index = SkipWhitespace(text, index);
        if (index >= text.Length || text[index] != '(')
            return (false, null, null);

        var dot = token.LastIndexOf('.');
        if (dot >= 0)
        {
            if (dot == 0 || dot == token.Length - 1)
                return (false, null, null);

            var member = token[(dot + 1)..];
            if (!AllWordChars(member))
                return (false, null, null);

            return (true, token[..dot], member);
        }

        return AllWordChars(token) ? (true, null, token) : (false, null, null);
    }

    private static string? ParseParenthesizedArguments(string text)
    {
        var open = text.IndexOf('(');
        if (open < 0)
            return null;

        var close = text.IndexOf(')', open + 1);
        return close >= 0 ? text[(open + 1)..close] : null;
    }

    private static string? ParseRegionMarker(string text, string marker)
    {
        var index = 0;
        if (index >= text.Length || text[index] != '#')
            return null;
        index++;
        index = SkipWhitespace(text, index);

        var prefix = "[" + marker;
        if (!StartsWithAt(text, index, prefix))
            return null;
        index += prefix.Length;
        if (!HasWhitespaceAt(text, index))
            return null;
        index = SkipWhitespace(text, index);

        var end = text.IndexOf(']', index);
        return end > index ? text[index..end] : null;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static bool HasWhitespaceAt(string text, int index) =>
        index < text.Length && char.IsWhiteSpace(text[index]);

    private static bool StartsWithAt(string text, int index, string value) =>
        index >= 0
        && index <= text.Length - value.Length
        && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static bool IsWordChar(char ch) =>
        ch is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_';

    private static bool AllWordChars(string text)
    {
        foreach (var ch in text)
        {
            if (!IsWordChar(ch))
                return false;
        }
        return text.Length > 0;
    }

    private static HashSet<int> ExtractCommentLines(string[] lines)
    {
        var commentLines = new HashSet<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('#'))
                commentLines.Add(i + 1);
        }
        return commentLines;
    }
}
