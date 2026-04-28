using System.Text.RegularExpressions;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class JavaScriptSourceParser : ISourceParser
{
    public IReadOnlyList<string> Extensions => [".js", ".ts"];
    public string Language => "javascript";

    public SourceFile? Parse(string filePath, string sourceText)
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
            var requireMatch = Regex.Match(trimmed, @"\brequire\s*\(\s*['""]([^'""]+)['""]\s*\)");
            if (requireMatch.Success)
            {
                usings.Add(requireMatch.Groups[1].Value);
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
            Usings = usings
        };
    }

    private static void ParseImport(string trimmed, List<string> usings)
    {
        // import ... from 'module'
        var fromMatch = Regex.Match(trimmed, @"from\s+['""]([^'""]+)['""]");
        if (fromMatch.Success)
        {
            usings.Add(fromMatch.Groups[1].Value);
            return;
        }
        // import 'module' (side-effect)
        var sideEffectMatch = Regex.Match(trimmed, @"^import\s+['""]([^'""]+)['""]");
        if (sideEffectMatch.Success)
        {
            usings.Add(sideEffectMatch.Groups[1].Value);
        }
    }

    private static bool IsClassDeclaration(string trimmed)
    {
        // export class, export default class, class
        return Regex.IsMatch(trimmed, @"^(?:export\s+(?:default\s+)?)?class\s+\w");
    }

    private static bool IsFunctionDeclaration(string trimmed)
    {
        return Regex.IsMatch(trimmed, @"^(?:export\s+(?:default\s+)?)?(?:async\s+)?function\s+\w");
    }

    private static (TypeDeclaration?, int) ParseClass(string[] lines, int startLine, List<StatementInfo> statements)
    {
        var trimmed = lines[startLine].TrimStart();
        bool isExported = trimmed.StartsWith("export");

        var classMatch = Regex.Match(trimmed, @"class\s+(\w+)(?:\s+extends\s+(\w[\w.]*))?");
        if (!classMatch.Success) return (null, startLine + 1);

        string className = classMatch.Groups[1].Value;
        var baseTypes = classMatch.Groups[2].Success
            ? [classMatch.Groups[2].Value]
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
            var methodMatch = Regex.Match(line,
                @"^(static\s+)?(?:(async)\s+)?(?:(?:get|set)\s+)?(\w+)\s*\(([^)]*)\)");
            if (methodMatch.Success && !line.StartsWith("if") && !line.StartsWith("for") && !line.StartsWith("while"))
            {
                var modifiers = Modifier.Public;
                if (methodMatch.Groups[1].Success) modifiers |= Modifier.Static;
                if (methodMatch.Groups[2].Success) modifiers |= Modifier.Async;

                string methodName = methodMatch.Groups[3].Value;
                var parameters = ParseParameters(methodMatch.Groups[4].Value);

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
            baseTypes, [], constructors, methods, [], [], startLine + 1), i);
    }

    private static (MethodDeclaration?, int) ParseFunction(string[] lines, int startLine,
        List<StatementInfo> statements)
    {
        var trimmed = lines[startLine].TrimStart();

        bool isExported = trimmed.StartsWith("export");
        bool isAsync = trimmed.Contains("async ");

        var funcMatch = Regex.Match(trimmed, @"function\s+(\w+)\s*\(([^)]*)\)");
        if (!funcMatch.Success) return (null, startLine + 1);

        string funcName = funcMatch.Groups[1].Value;
        var parameters = ParseParameters(funcMatch.Groups[2].Value);

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
            var catchMatch = Regex.Match(trimmed, @"^}\s*catch\s*(?:\((\w+)\))?\s*\{?|^catch\s*(?:\((\w+)\))?\s*\{?");
            if (catchMatch.Success)
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
                var throwMatch = Regex.Match(trimmed, @"^throw\s+new\s+(\w+)");
                string? typeName = throwMatch.Success ? throwMatch.Groups[1].Value : null;
                statements.Add(new StatementInfo("throw", [], typeName, null, [], i + 1, true));
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
                var throwMatch = Regex.Match(part, @"^throw\s+new\s+(\w+)");
                string? typeName = throwMatch.Success ? throwMatch.Groups[1].Value : null;
                statements.Add(new StatementInfo("throw", [], typeName, null, [], lineNumber, isInMethod));
                continue;
            }
            ExtractLineStatement(part, lineNumber, isInMethod, statements);
        }
    }

    private static void ExtractLineStatement(string trimmed, int lineNumber, bool isInMethod,
        List<StatementInfo> statements)
    {
        // Variable declarations: const/let/var name = ...
        var declMatch = Regex.Match(trimmed, @"^(?:export\s+)?(?:const|let|var)\s+(\w+)");
        if (declMatch.Success)
        {
            var keywords = new List<string>();
            if (trimmed.Contains("const ")) keywords.Add("const");
            if (trimmed.Contains("let ")) keywords.Add("let");
            if (trimmed.Contains("var ")) keywords.Add("var");

            statements.Add(new StatementInfo("declaration", keywords, null, declMatch.Groups[1].Value, [], lineNumber, isInMethod));

            // Also extract calls on the right-hand side (e.g., const x = console.log(...))
            var afterEq = trimmed.IndexOf('=');
            if (afterEq > 0)
            {
                var rhs = trimmed[(afterEq + 1)..].TrimStart();
                ExtractCallFromExpression(rhs, lineNumber, isInMethod, statements);
            }
            return;
        }

        // eval() call
        var evalMatch = Regex.Match(trimmed, @"\beval\s*\(");
        if (evalMatch.Success)
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
        var callMatch = Regex.Match(expr, @"(?:await\s+)?(?:(\w[\w.]*?)\.)?(\w+)\s*\(");
        if (!callMatch.Success) return;

        string? typeName = callMatch.Groups[1].Success ? callMatch.Groups[1].Value : null;
        string memberName = callMatch.Groups[2].Value;

        // Skip control flow and declaration keywords
        if (memberName is "if" or "for" or "while" or "switch" or "function" or "class"
            or "return" or "new" or "typeof" or "import" or "require" or "catch" or "throw")
            return;

        // Extract simple arguments
        var argsMatch = Regex.Match(expr, @"\(([^)]*)\)");
        var args = argsMatch.Success && !string.IsNullOrWhiteSpace(argsMatch.Groups[1].Value)
            ? argsMatch.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).ToList()
            : new List<string>();

        statements.Add(new StatementInfo("call", [], typeName, memberName, args, lineNumber, isInMethod));
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
}
