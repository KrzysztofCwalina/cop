using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class JavaScriptSourceParser : ISourceParser
{
    public override IReadOnlyList<string> Extensions => [".js", ".ts"];
    public override string Language => "javascript";

    public override SourceFile? Parse(string filePath, string sourceText)
    {
        var lines = sourceText.Split('\n');
        var types = new List<TypeDeclaration>();
        var statements = new List<StatementInfo>();
        var usings = new List<string>();

        int i = 0;
        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimStart();

            // Skip blank lines and comments
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//"))
            {
                i++;
                continue;
            }

            // Skip block comments
            if (trimmed.StartsWith("/*"))
            {
                i = SkipBlockComment(lines, i);
                continue;
            }

            // Import statements: import ... from '...' or import '...'
            if (trimmed.StartsWith("import "))
            {
                ParseImport(trimmed, usings);
                i++;
                continue;
            }

            // Require: const x = require('...')
            if (TryParseRequireModule(trimmed, out var requireModule))
            {
                usings.Add(requireModule);
                i++;
                continue;
            }

            // Class declaration
            if (IsClassDeclaration(trimmed))
            {
                var (type, nextLine) = ParseClass(lines, i, statements);
                if (type != null) types.Add(type);
                i = nextLine;
                continue;
            }

            // Top-level function declaration
            if (IsFunctionDeclaration(trimmed))
            {
                var (_, nextLine) = ParseFunction(lines, i, statements);
                i = nextLine;
                continue;
            }

            // Top-level statements
            ExtractLineStatement(trimmed, i + 1, false, statements);
            i++;
        }

        return new SourceFile(filePath, "javascript", types, statements, sourceText)
        {
            Usings = usings,
            Regions = ExtractRegions(lines),
            CommentLines = ExtractCommentLines(lines)
        };
    }

    private static void ParseImport(string trimmed, List<string> usings)
    {
        // import ... from 'module'
        if (TryParseImportFrom(trimmed, out var fromModule))
        {
            usings.Add(fromModule);
            return;
        }
        // import 'module' (side-effect)
        if (TryParseSideEffectImport(trimmed, out var sideEffectModule))
        {
            usings.Add(sideEffectModule);
        }
    }

    private static bool IsClassDeclaration(string trimmed)
    {
        // export class, export default class, class
        return IsClassDeclarationPattern(trimmed);
    }

    private static bool IsFunctionDeclaration(string trimmed)
    {
        return IsFunctionDeclarationPattern(trimmed);
    }

    private static (TypeDeclaration?, int) ParseClass(string[] lines, int startLine, List<StatementInfo> statements)
    {
        var trimmed = lines[startLine].TrimStart();
        bool isExported = trimmed.StartsWith("export");

        if (!TryParseClassDeclaration(trimmed, out var className, out var baseType)) return (null, startLine + 1);

        var baseTypes = baseType != null
            ? [baseType]
            : new List<string>();

        // Find the opening brace
        int braceStart = FindCharOnLine(lines[startLine], '{');
        if (braceStart < 0) return (null, startLine + 1);

        int braceDepth = 1;
        var methods = new List<MethodDeclaration>();
        var constructors = new List<MethodDeclaration>();

        int i = startLine + 1;
        while (i < lines.Length && braceDepth > 0)
        {
            var line = lines[i].TrimStart();

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//"))
            {
                i++;
                continue;
            }
            if (line.StartsWith("/*"))
            {
                i = SkipBlockComment(lines, i);
                continue;
            }

            // Track braces
            braceDepth += CountUnquotedChar(lines[i], '{') - CountUnquotedChar(lines[i], '}');
            if (braceDepth <= 0) { i++; break; }

            // Method: name(...) {, async name(...) {, static name(...) {, get/set name(...) {
            if (TryParseMethodDeclaration(line, out var methodName, out var parameterText, out var isStatic, out var methodIsAsync)
                && !line.StartsWith("if") && !line.StartsWith("for") && !line.StartsWith("while"))
            {
                var modifiers = Modifier.Public;
                if (isStatic) modifiers |= Modifier.Static;
                if (methodIsAsync) modifiers |= Modifier.Async;

                var parameters = ParseParameters(parameterText);

                // Extract method body statements
                var methodStatements = new List<StatementInfo>();
                int bodyEnd = SkipBracedBlock(lines, i);
                ExtractInlineBody(lines[i], i + 1, true, methodStatements);
                ExtractBodyStatements(lines, i + 1, bodyEnd, methodStatements);
                statements.AddRange(methodStatements);

                var method = new MethodDeclaration(methodName, modifiers, [], null, parameters, i + 1)
                {
                    Statements = methodStatements
                };

                if (methodName == "constructor")
                    constructors.Add(method);
                else
                    methods.Add(method);

                // Undo the brace count from line 148 for this line — SkipBracedBlock
                // already handled all braces from this line through the method's closing }.
                braceDepth -= CountUnquotedChar(lines[i], '{') - CountUnquotedChar(lines[i], '}');
                i = bodyEnd;
                continue;
            }

            i++;
        }

        var classModifiers = isExported ? Modifier.Public : Modifier.None;
        return (new TypeDeclaration(className, TypeKind.Class, classModifiers,
            baseTypes, [], constructors, methods, [], [], startLine + 1)
            .AsJavaScript(isExported: isExported, hasBaseClass: baseTypes.Count > 0), i);
    }

    private static (MethodDeclaration?, int) ParseFunction(string[] lines, int startLine,
        List<StatementInfo> statements)
    {
        var trimmed = lines[startLine].TrimStart();

        bool isExported = trimmed.StartsWith("export");
        bool isAsync = trimmed.Contains("async ");

        if (!TryParseFunctionDeclaration(trimmed, out var funcName, out var parameterText)) return (null, startLine + 1);

        var parameters = ParseParameters(parameterText);

        var modifiers = isExported ? Modifier.Public : Modifier.None;
        if (isAsync) modifiers |= Modifier.Async;

        var methodStatements = new List<StatementInfo>();
        int bodyEnd = SkipBracedBlock(lines, startLine);

        // Handle single-line bodies: function f() { stmt; }
        ExtractInlineBody(lines[startLine], startLine + 1, true, methodStatements);
        ExtractBodyStatements(lines, startLine + 1, bodyEnd, methodStatements);
        statements.AddRange(methodStatements);

        return (new MethodDeclaration(funcName, modifiers, [], null, parameters, startLine + 1)
        {
            Statements = methodStatements
        }, bodyEnd);
    }

    private static void ExtractBodyStatements(string[] lines, int start, int end,
        List<StatementInfo> statements)
    {
        for (int i = start; i < end && i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//")) continue;
            if (trimmed.StartsWith("/*"))
            {
                i = SkipBlockComment(lines, i) - 1;
                continue;
            }

            // catch clause
            if (IsCatchClause(trimmed))
            {
                // JS catch is always untyped — capture the variable name for reference but TypeName is null
                bool hasRethrow = HasRethrow(lines, i + 1, end);
                statements.Add(new StatementInfo("catch", [], null, null, [], i + 1, true)
                {
                    HasRethrow = hasRethrow,
                    IsErrorHandler = true,
                    IsGenericErrorHandler = true // JS catch is always untyped/generic
                });
                continue;
            }

            // debugger statement
            if (trimmed.StartsWith("debugger"))
            {
                statements.Add(new StatementInfo("call", ["debugger"], null, "debugger", [], i + 1, true));
                continue;
            }

            // throw statement
            if (trimmed.StartsWith("throw "))
            {
                string? typeName = TryParseThrowNewType(trimmed, out var parsedTypeName) ? parsedTypeName : null;
                statements.Add(new StatementInfo("throw", [], typeName, null, [], i + 1, true));
                continue;
            }

            // await statement (standalone or return await)
            if (trimmed.StartsWith("return await ") || trimmed.StartsWith("await "))
            {
                var awaitStart = trimmed.StartsWith("return await ") ? 13 : 6;
                var awaitExpr = trimmed[awaitStart..].TrimEnd(';').Trim();
                ExtractAwaitStatement(awaitExpr, i + 1, true, statements);
                continue;
            }

            ExtractLineStatement(trimmed, i + 1, true, statements);
        }
    }

    /// <summary>
    /// Extract statements from inline body content (e.g., function f() { alert('x'); })
    /// Looks for content between the first { and last } on the same line.
    /// </summary>
    private static void ExtractInlineBody(string line, int lineNumber, bool isInMethod,
        List<StatementInfo> statements)
    {
        int braceOpen = FindCharOnLine(line, '{');
        if (braceOpen < 0) return;

        int braceClose = line.LastIndexOf('}');
        if (braceClose <= braceOpen) return;

        var body = line[(braceOpen + 1)..braceClose].Trim();
        if (string.IsNullOrWhiteSpace(body)) return;

        // Split on semicolons for multiple statements
        foreach (var part in body.Split(';', StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(part)) continue;

            if (part.StartsWith("debugger"))
            {
                statements.Add(new StatementInfo("call", ["debugger"], null, "debugger", [], lineNumber, isInMethod));
                continue;
            }
            if (part.StartsWith("throw "))
            {
                string? typeName = TryParseThrowNewType(part, out var parsedTypeName) ? parsedTypeName : null;
                statements.Add(new StatementInfo("throw", [], typeName, null, [], lineNumber, isInMethod));
                continue;
            }
            if (part.StartsWith("return await ") || part.StartsWith("await "))
            {
                var awaitStart = part.StartsWith("return await ") ? 13 : 6;
                var awaitExpr = part[awaitStart..].Trim();
                ExtractAwaitStatement(awaitExpr, lineNumber, isInMethod, statements);
                continue;
            }
            ExtractLineStatement(part, lineNumber, isInMethod, statements);
        }
    }

    private static void ExtractLineStatement(string trimmed, int lineNumber, bool isInMethod,
        List<StatementInfo> statements)
    {
        // Variable declarations: const/let/var name = ...
        if (TryParseVariableDeclaration(trimmed, out var variableName))
        {
            var keywords = new List<string>();
            if (trimmed.Contains("const ")) keywords.Add("const");
            if (trimmed.Contains("let ")) keywords.Add("let");
            if (trimmed.Contains("var ")) keywords.Add("var");

            statements.Add(new StatementInfo("declaration", keywords, null, variableName, [], lineNumber, isInMethod));

            // Also extract calls on the right-hand side (e.g., const x = console.log(...))
            var afterEq = trimmed.IndexOf('=');
            if (afterEq > 0)
            {
                var rhs = trimmed[(afterEq + 1)..].TrimStart();
                if (rhs.StartsWith("await "))
                {
                    var awaitExpr = rhs[6..].TrimEnd(';').Trim();
                    ExtractAwaitStatement(awaitExpr, lineNumber, isInMethod, statements);
                }
                else
                {
                    ExtractCallFromExpression(rhs, lineNumber, isInMethod, statements);
                }
            }
            return;
        }

        // eval() call
        if (ContainsEvalCall(trimmed))
        {
            statements.Add(new StatementInfo("call", [], null, "eval", [], lineNumber, isInMethod));
            return;
        }

        // Function/method call: name(...) or obj.name(...)
        ExtractCallFromExpression(trimmed, lineNumber, isInMethod, statements);
    }

    private static void ExtractCallFromExpression(string expr, int lineNumber, bool isInMethod,
        List<StatementInfo> statements)
    {
        // await optional, then obj.method(...) or method(...)
        if (!TryParseCallExpression(expr, allowAwaitPrefix: true, anchored: false, out var typeName, out var memberName)) return;

        // Skip control flow and declaration keywords
        if (memberName is "if" or "for" or "while" or "switch" or "function" or "class"
            or "return" or "new" or "typeof" or "import" or "require" or "catch" or "throw")
            return;

        // Extract simple arguments
        var args = TryExtractFirstParenthesizedText(expr, out var argsText) && !string.IsNullOrWhiteSpace(argsText)
            ? argsText.Split(',', StringSplitOptions.TrimEntries).ToList()
            : new List<string>();

        statements.Add(new StatementInfo("call", [], typeName, memberName, args, lineNumber, isInMethod));
    }

    private static void ExtractAwaitStatement(string awaitExpr, int lineNumber, bool isInMethod,
        List<StatementInfo> statements)
    {
        string? typeName = null;
        string? memberName = null;

        if (TryParseCallExpression(awaitExpr, allowAwaitPrefix: false, anchored: true, out var parsedTypeName, out var parsedMemberName))
        {
            typeName = parsedTypeName;
            memberName = parsedMemberName;

            if (memberName is "if" or "for" or "while" or "switch" or "function" or "class"
                or "return" or "new" or "typeof" or "import" or "require" or "catch" or "throw")
            {
                typeName = null;
                memberName = null;
            }
        }

        statements.Add(new StatementInfo("await", [], typeName, memberName, [], lineNumber, isInMethod)
        {
            Expression = awaitExpr
        });

        // Also emit the inner call for backward compatibility
        ExtractCallFromExpression(awaitExpr, lineNumber, isInMethod, statements);
    }

    private static bool TryParseRequireModule(string text, out string module)
    {
        module = string.Empty;
        for (int i = 0; i < text.Length; i++)
        {
            if (!StartsWithAt(text, i, "require") || !IsWordBoundaryBefore(text, i))
                continue;

            int pos = i + "require".Length;
            SkipWhitespace(text, ref pos);
            if (!TryReadChar(text, ref pos, '('))
                continue;

            SkipWhitespace(text, ref pos);
            if (!TryReadQuotedText(text, ref pos, out module, requireContent: true))
                continue;

            SkipWhitespace(text, ref pos);
            if (TryReadChar(text, ref pos, ')'))
                return true;
        }

        return false;
    }

    private static bool TryParseImportFrom(string text, out string module)
    {
        module = string.Empty;
        for (int i = 0; i < text.Length; i++)
        {
            if (!StartsWithAt(text, i, "from"))
                continue;

            int pos = i + "from".Length;
            if (!SkipRequiredWhitespace(text, ref pos))
                continue;

            if (TryReadQuotedText(text, ref pos, out module, requireContent: true))
                return true;
        }

        return false;
    }

    private static bool TryParseSideEffectImport(string text, out string module)
    {
        module = string.Empty;
        int pos = 0;
        if (!TryReadLiteral(text, ref pos, "import") || !SkipRequiredWhitespace(text, ref pos))
            return false;

        return TryReadQuotedText(text, ref pos, out module, requireContent: true);
    }

    private static bool IsClassDeclarationPattern(string text)
    {
        int pos = 0;
        TryReadExportDefaultPrefix(text, ref pos);
        if (!TryReadLiteral(text, ref pos, "class") || !SkipRequiredWhitespace(text, ref pos))
            return false;

        return pos < text.Length && IsWordChar(text[pos]);
    }

    private static bool IsFunctionDeclarationPattern(string text)
    {
        int pos = 0;
        TryReadExportDefaultPrefix(text, ref pos);
        TryReadAsyncPrefix(text, ref pos);
        if (!TryReadLiteral(text, ref pos, "function") || !SkipRequiredWhitespace(text, ref pos))
            return false;

        return pos < text.Length && IsWordChar(text[pos]);
    }

    private static bool TryParseClassDeclaration(string text, out string className, out string? baseType)
    {
        className = string.Empty;
        baseType = null;

        for (int i = 0; i < text.Length; i++)
        {
            if (!StartsWithAt(text, i, "class"))
                continue;

            int pos = i + "class".Length;
            if (!SkipRequiredWhitespace(text, ref pos))
                continue;

            if (!TryReadWord(text, ref pos, out className))
                continue;

            int extendsPos = pos;
            if (SkipRequiredWhitespace(text, ref extendsPos)
                && TryReadLiteral(text, ref extendsPos, "extends")
                && SkipRequiredWhitespace(text, ref extendsPos)
                && TryReadDottedWord(text, ref extendsPos, out var parsedBaseType))
            {
                baseType = parsedBaseType;
            }

            return true;
        }

        return false;
    }

    private static bool TryParseMethodDeclaration(string text, out string methodName, out string parameterText,
        out bool isStatic, out bool isAsync)
    {
        methodName = string.Empty;
        parameterText = string.Empty;
        isStatic = false;
        isAsync = false;

        foreach (var staticChoice in GetOptionalPrefixChoices(text, 0, "static"))
        {
            foreach (var asyncChoice in GetOptionalPrefixChoices(text, staticChoice.Position, "async"))
            {
                foreach (var accessorChoice in GetOptionalPrefixChoices(text, asyncChoice.Position, "get", "set"))
                {
                    int pos = accessorChoice.Position;
                    if (!TryReadWord(text, ref pos, out methodName))
                        continue;

                    SkipWhitespace(text, ref pos);
                    if (!TryReadParenthesizedTextAt(text, ref pos, out parameterText))
                        continue;

                    isStatic = staticChoice.Consumed;
                    isAsync = asyncChoice.Consumed;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParseFunctionDeclaration(string text, out string functionName, out string parameterText)
    {
        functionName = string.Empty;
        parameterText = string.Empty;

        for (int i = 0; i < text.Length; i++)
        {
            if (!StartsWithAt(text, i, "function"))
                continue;

            int pos = i + "function".Length;
            if (!SkipRequiredWhitespace(text, ref pos) || !TryReadWord(text, ref pos, out functionName))
                continue;

            SkipWhitespace(text, ref pos);
            if (TryReadParenthesizedTextAt(text, ref pos, out parameterText))
                return true;
        }

        return false;
    }

    private static bool IsCatchClause(string text)
    {
        int pos = 0;
        if (TryReadChar(text, ref pos, '}'))
            SkipWhitespace(text, ref pos);

        return StartsWithAt(text, pos, "catch");
    }

    private static bool TryParseThrowNewType(string text, out string typeName)
    {
        typeName = string.Empty;
        int pos = 0;
        if (!TryReadLiteral(text, ref pos, "throw") || !SkipRequiredWhitespace(text, ref pos)
            || !TryReadLiteral(text, ref pos, "new") || !SkipRequiredWhitespace(text, ref pos))
            return false;

        return TryReadWord(text, ref pos, out typeName);
    }

    private static bool TryParseVariableDeclaration(string text, out string variableName)
    {
        variableName = string.Empty;
        int pos = 0;
        if (TryReadLiteral(text, ref pos, "export"))
        {
            if (!SkipRequiredWhitespace(text, ref pos))
                return false;
        }

        if (!(TryReadLiteral(text, ref pos, "const")
            || TryReadLiteral(text, ref pos, "let")
            || TryReadLiteral(text, ref pos, "var")))
            return false;

        return SkipRequiredWhitespace(text, ref pos) && TryReadWord(text, ref pos, out variableName);
    }

    private static bool ContainsEvalCall(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (!StartsWithAt(text, i, "eval") || !IsWordBoundaryBefore(text, i))
                continue;

            int pos = i + "eval".Length;
            SkipWhitespace(text, ref pos);
            if (pos < text.Length && text[pos] == '(')
                return true;
        }

        return false;
    }

    private static bool TryParseCallExpression(string text, bool allowAwaitPrefix, bool anchored,
        out string? typeName, out string memberName)
    {
        typeName = null;
        memberName = string.Empty;

        int start = 0;
        while (start < text.Length)
        {
            if (TryParseCallAt(text, start, allowAwaitPrefix, out typeName, out memberName))
                return true;

            if (anchored)
                return false;

            start++;
        }

        return false;
    }

    private static bool TryParseCallAt(string text, int start, bool allowAwaitPrefix,
        out string? typeName, out string memberName)
    {
        typeName = null;
        memberName = string.Empty;

        int pos = start;
        if (allowAwaitPrefix && StartsWithAt(text, pos, "await"))
        {
            int afterAwait = pos + "await".Length;
            if (SkipRequiredWhitespace(text, ref afterAwait))
                pos = afterAwait;
        }

        if (pos >= text.Length || !IsWordChar(text[pos]))
            return false;

        int sequenceStart = pos;
        while (pos < text.Length && (IsWordChar(text[pos]) || text[pos] == '.'))
            pos++;

        int parenPos = pos;
        SkipWhitespace(text, ref parenPos);
        if (parenPos >= text.Length || text[parenPos] != '(')
            return false;

        int memberStart = pos - 1;
        while (memberStart >= sequenceStart && IsWordChar(text[memberStart]))
            memberStart--;
        memberStart++;

        if (memberStart == pos)
            return false;

        memberName = text[memberStart..pos];
        if (memberStart > sequenceStart && text[memberStart - 1] == '.')
            typeName = text[sequenceStart..(memberStart - 1)];

        return true;
    }

    private static bool TryExtractFirstParenthesizedText(string text, out string value)
    {
        value = string.Empty;
        int open = text.IndexOf('(');
        if (open < 0)
            return false;

        int close = text.IndexOf(')', open + 1);
        if (close < 0)
            return false;

        value = text[(open + 1)..close];
        return true;
    }

    private static bool TryParseSnippetMarker(string text, string marker, out string name)
    {
        name = string.Empty;
        int pos = 0;
        if (!TryReadLiteral(text, ref pos, "//"))
            return false;

        SkipWhitespace(text, ref pos);
        if (!TryReadChar(text, ref pos, '[') || !TryReadLiteral(text, ref pos, marker)
            || !SkipRequiredWhitespace(text, ref pos))
            return false;

        int end = text.IndexOf(']', pos);
        if (end <= pos)
            return false;

        name = text[pos..end];
        return true;
    }

    private static void TryReadExportDefaultPrefix(string text, ref int pos)
    {
        int original = pos;
        if (!TryReadLiteral(text, ref pos, "export") || !SkipRequiredWhitespace(text, ref pos))
        {
            pos = original;
            return;
        }

        int beforeDefault = pos;
        if (!TryReadLiteral(text, ref pos, "default") || !SkipRequiredWhitespace(text, ref pos))
            pos = beforeDefault;
    }

    private static List<(int Position, bool Consumed)> GetOptionalPrefixChoices(string text, int pos,
        params string[] prefixes)
    {
        var choices = new List<(int Position, bool Consumed)>();
        foreach (var prefix in prefixes)
        {
            int next = pos;
            if (TryReadLiteral(text, ref next, prefix) && SkipRequiredWhitespace(text, ref next))
                choices.Add((next, true));
        }

        choices.Add((pos, false));
        return choices;
    }

    private static void TryReadAsyncPrefix(string text, ref int pos)
    {
        int original = pos;
        if (!TryReadLiteral(text, ref pos, "async") || !SkipRequiredWhitespace(text, ref pos))
            pos = original;
    }

    private static bool TryReadQuotedText(string text, ref int pos, out string value, bool requireContent)
    {
        value = string.Empty;
        if (pos >= text.Length || text[pos] is not ('\'' or '"'))
            return false;

        pos++;
        int start = pos;
        while (pos < text.Length && text[pos] is not ('\'' or '"'))
            pos++;

        if (pos >= text.Length || (requireContent && pos == start))
            return false;

        value = text[start..pos];
        pos++;
        return true;
    }

    private static bool TryReadParenthesizedTextAt(string text, ref int pos, out string value)
    {
        value = string.Empty;
        if (!TryReadChar(text, ref pos, '('))
            return false;

        int start = pos;
        while (pos < text.Length && text[pos] != ')')
            pos++;

        if (pos >= text.Length)
            return false;

        value = text[start..pos];
        pos++;
        return true;
    }

    private static bool TryReadDottedWord(string text, ref int pos, out string value)
    {
        value = string.Empty;
        int start = pos;
        if (pos >= text.Length || !IsWordChar(text[pos]))
            return false;

        pos++;
        while (pos < text.Length && (IsWordChar(text[pos]) || text[pos] == '.'))
            pos++;

        value = text[start..pos];
        return true;
    }

    private static bool TryReadWord(string text, ref int pos, out string value)
    {
        value = string.Empty;
        int start = pos;
        while (pos < text.Length && IsWordChar(text[pos]))
            pos++;

        if (pos == start)
            return false;

        value = text[start..pos];
        return true;
    }

    private static bool TryReadLiteral(string text, ref int pos, string literal)
    {
        if (!StartsWithAt(text, pos, literal))
            return false;

        pos += literal.Length;
        return true;
    }

    private static bool TryReadChar(string text, ref int pos, char ch)
    {
        if (pos >= text.Length || text[pos] != ch)
            return false;

        pos++;
        return true;
    }

    private static bool SkipRequiredWhitespace(string text, ref int pos)
    {
        int start = pos;
        SkipWhitespace(text, ref pos);
        return pos > start;
    }

    private static void SkipWhitespace(string text, ref int pos)
    {
        while (pos < text.Length && IsWhitespace(text[pos]))
            pos++;
    }

    private static bool StartsWithAt(string text, int pos, string value)
    {
        return pos >= 0 && pos + value.Length <= text.Length
            && string.CompareOrdinal(text, pos, value, 0, value.Length) == 0;
    }

    private static bool IsWordBoundaryBefore(string text, int pos)
    {
        return pos == 0 || !IsWordChar(text[pos - 1]);
    }

    private static bool IsWordChar(char ch)
    {
        return ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_';
    }

    private static bool IsWhitespace(char ch)
    {
        return char.IsWhiteSpace(ch);
    }

    private static bool HasRethrow(string[] lines, int start, int end)
    {
        int depth = 0;
        for (int i = start; i < end && i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            depth += CountUnquotedChar(lines[i], '{') - CountUnquotedChar(lines[i], '}');
            if (depth < 0) break;
            if (trimmed.StartsWith("throw") && (trimmed.Length == 5 || trimmed[5] is ' ' or ';'))
                return true;
        }
        return false;
    }

    private static List<ParameterDeclaration> ParseParameters(string paramString)
    {
        var parameters = new List<ParameterDeclaration>();
        if (string.IsNullOrWhiteSpace(paramString)) return parameters;

        foreach (var part in paramString.Split(',', StringSplitOptions.TrimEntries))
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            bool isVariadic = trimmed.StartsWith("...");
            if (isVariadic) trimmed = trimmed[3..];

            // Remove TS type annotations: name: Type
            var colonIdx = trimmed.IndexOf(':');
            string? typeText = null;
            if (colonIdx > 0)
            {
                typeText = trimmed[(colonIdx + 1)..].Trim();
                trimmed = trimmed[..colonIdx].Trim();
            }

            // Remove default values: name = value
            var eqIdx = trimmed.IndexOf('=');
            bool hasDefault = eqIdx > 0;
            if (hasDefault) trimmed = trimmed[..eqIdx].Trim();

            // Remove optional marker: name?
            if (trimmed.EndsWith('?')) trimmed = trimmed[..^1];

            var typeRef = typeText != null ? new TypeReference(typeText, null, [], typeText) : null;
            parameters.Add(new ParameterDeclaration(trimmed, typeRef, isVariadic, false, hasDefault, 0));
        }

        return parameters;
    }

    private static int SkipBlockComment(string[] lines, int startLine)
    {
        for (int i = startLine; i < lines.Length; i++)
        {
            if (lines[i].Contains("*/"))
                return i + 1;
        }
        return lines.Length;
    }

    private static int SkipBracedBlock(string[] lines, int startLine)
    {
        int depth = 0;
        for (int i = startLine; i < lines.Length; i++)
        {
            depth += CountUnquotedChar(lines[i], '{') - CountUnquotedChar(lines[i], '}');
            if (depth <= 0) return i + 1;
        }
        return lines.Length;
    }

    private static int FindCharOnLine(string line, char ch)
    {
        bool inString = false;
        char strChar = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            if (inString)
            {
                if (line[i] == strChar && (i == 0 || line[i - 1] != '\\'))
                    inString = false;
                continue;
            }
            if (line[i] is '\'' or '"' or '`')
            {
                inString = true;
                strChar = line[i];
                continue;
            }
            if (line[i] == ch) return i;
        }
        return -1;
    }

    private static int CountUnquotedChar(string line, char ch)
    {
        int count = 0;
        bool inString = false;
        char strChar = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            if (inString)
            {
                if (line[i] == strChar && (i == 0 || line[i - 1] != '\\'))
                    inString = false;
                continue;
            }
            if (line[i] is '\'' or '"' or '`')
            {
                inString = true;
                strChar = line[i];
                continue;
            }
            if (line[i] == '/') // Skip line comments
            {
                if (i + 1 < line.Length && line[i + 1] == '/') break;
            }
            if (line[i] == ch) count++;
        }
        return count;
    }

    // Extracts regions from // [START name] / // [END name] comment markers
    private static List<RegionInfo> ExtractRegions(string[] lines)
    {
        var regions = new List<RegionInfo>();
        var stack = new Stack<(string Name, int Line)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("// [START"))
            {
                if (TryParseSnippetMarker(trimmed, "START", out var startName))
                    stack.Push((startName, i + 1));
            }
            else if (trimmed.StartsWith("// [END") && stack.Count > 0)
            {
                if (TryParseSnippetMarker(trimmed, "END", out var endName))
                {
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

    private static HashSet<int> ExtractCommentLines(string[] lines)
    {
        var commentLines = new HashSet<int>();
        bool inBlockComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();

            if (inBlockComment)
            {
                commentLines.Add(i + 1);
                if (trimmed.Contains("*/"))
                    inBlockComment = false;
                continue;
            }

            if (trimmed.StartsWith("//"))
            {
                commentLines.Add(i + 1);
            }
            else if (trimmed.StartsWith("/*"))
            {
                commentLines.Add(i + 1);
                if (!trimmed.Contains("*/"))
                    inBlockComment = true;
            }
        }

        return commentLines;
    }
}
