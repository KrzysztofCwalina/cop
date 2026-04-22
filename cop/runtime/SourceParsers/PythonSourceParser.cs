using System.Text.RegularExpressions;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class PythonSourceParser : ISourceParser
{
    public IReadOnlyList<string> Extensions => [".py"];
    public string Language => "python";

    public SourceFile? Parse(string filePath, string sourceText)
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
                    var match = Regex.Match(trimmed, @"^from\s+(\S+)\s+import");
                    if (match.Success) usings.Add(match.Groups[1].Value);
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
            Usings = usings
        };
    }

    private static (TypeDeclaration?, int) ParseClass(string[] lines, int startLine, List<StatementInfo> statements)
    {
        int classIndent = lines[startLine].Length - lines[startLine].TrimStart().Length;

        var decorators = CollectDecorators(lines, startLine);

        var classMatch = Regex.Match(lines[startLine].TrimStart(), @"class\s+(\w+)\s*(?:\(([^)]*)\))?\s*:");
        if (!classMatch.Success) return (null, startLine + 1);

        string className = classMatch.Groups[1].Value;
        var baseTypes = classMatch.Groups[2].Success
            ? classMatch.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];

        var methods = new List<MethodDeclaration>();
        var constructors = new List<MethodDeclaration>();

        int i = startLine + 1;
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
            baseTypes, decorators, constructors, methods, [], [], startLine + 1), i);
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

        var defMatch = Regex.Match(fullDef, @"def\s+(\w+)\s*\(([^)]*)\)");
        if (!defMatch.Success) return (null, nextLine);

        string methodName = defMatch.Groups[1].Value;
        var parameters = ParseParameters(defMatch.Groups[2].Value);

        var modifiers = Modifier.None;
        if (isAsync) modifiers |= Modifier.Async;
        if (decorators.Contains("staticmethod")) modifiers |= Modifier.Static;
        if (decorators.Contains("classmethod")) modifiers |= Modifier.Static;
        if (decorators.Contains("abstractmethod")) modifiers |= Modifier.Abstract;
        if (!methodName.StartsWith("_")) modifiers |= Modifier.Public;
        else modifiers |= Modifier.Private;

        // Extract statements from method body — collect per-method and add to global list
        int bodyStart = nextLine;
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
        var retMatch = Regex.Match(fullDef, @"\)\s*->\s*(\S+)\s*:");
        if (retMatch.Success) returnType = retMatch.Groups[1].Value;

        var retRef = returnType != null ? new TypeReference(returnType, null, [], returnType) : null;
        return (new MethodDeclaration(methodName, modifiers, decorators,
            retRef, parameters, startLine + 1) { Statements = methodStatements }, nextLine);
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
                var exceptMatch = Regex.Match(trimmed, @"^except\s*(?:\(([^)]+)\)|(\w[\w.]*))?(?:\s+as\s+\w+)?\s*:");
                string? caughtType = null;
                if (exceptMatch.Success)
                {
                    // Group 1: tuple form (Foo, Bar), Group 2: single type
                    caughtType = exceptMatch.Groups[1].Success
                        ? exceptMatch.Groups[1].Value.Split(',')[0].Trim()
                        : exceptMatch.Groups[2].Success ? exceptMatch.Groups[2].Value : null;
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
            var raiseMatch = Regex.Match(trimmed, @"^raise\s+(\w+)");
            string? typeName = raiseMatch.Success ? raiseMatch.Groups[1].Value : null;
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
        var callMatch = Regex.Match(trimmed, @"^(?:(?:await\s+)?(?:(\w[\w.]*?)\.)?)?(\w+)\s*\(");
        if (callMatch.Success)
        {
            string? typeName = callMatch.Groups[1].Success ? callMatch.Groups[1].Value : null;
            string memberName = callMatch.Groups[2].Value;

            // Skip control flow keywords that look like calls
            if (memberName is "if" or "for" or "while" or "with" or "elif" or "def" or "class"
                or "return" or "assert" or "del" or "except" or "raise" or "yield" or "import" or "from")
                return;

            // Extract simple arguments
            var argsMatch = Regex.Match(trimmed, @"\(([^)]*)\)");
            var args = argsMatch.Success && !string.IsNullOrWhiteSpace(argsMatch.Groups[1].Value)
                ? argsMatch.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).ToList()
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
}
