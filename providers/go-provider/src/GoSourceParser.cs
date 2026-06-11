using System.Text.RegularExpressions;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class GoSourceParser : ISourceParser
{
    public override IReadOnlyList<string> Extensions => [".go"];
    public override string Language => "go";

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

            // Skip blank lines
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                i++;
                continue;
            }

            // Skip line comments (not doc comments)
            if (trimmed.StartsWith("//") && !IsDocComment(lines, i))
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

            // Package declaration (skip)
            if (trimmed.StartsWith("package "))
            {
                i++;
                continue;
            }

            // Import block
            if (trimmed.StartsWith("import "))
            {
                i = ParseImports(lines, i, usings);
                continue;
            }

            // Type declaration: type Name struct/interface
            if (trimmed.StartsWith("type "))
            {
                var (type, nextLine) = ParseTypeDeclaration(lines, i, statements);
                if (type != null) types.Add(type);
                i = nextLine;
                continue;
            }

            // Function/method: func Name(...) or func (recv) Name(...)
            if (trimmed.StartsWith("func "))
            {
                var (method, receiver, nextLine) = ParseFunc(lines, i, statements);
                if (method != null && receiver != null)
                {
                    // Method with receiver — attach to existing type or create impl type
                    var existingType = types.FirstOrDefault(t => t.Name == receiver || t.Name == receiver + " (impl)");
                    if (existingType != null)
                    {
                        existingType.Methods.Add(method);
                    }
                    else
                    {
                        types.Add(new TypeDeclaration(receiver + " (impl)", TypeKind.Class, Modifier.Public,
                            [], [], [], [method], [], [], i + 1));
                    }
                }
                i = nextLine;
                continue;
            }

            // Top-level var/const blocks (skip structure, extract statements)
            if (trimmed.StartsWith("var ") || trimmed.StartsWith("const "))
            {
                if (trimmed.Contains('('))
                {
                    i = SkipParenBlock(lines, i);
                }
                else
                {
                    i++;
                }
                continue;
            }

            // Other top-level statements
            ExtractLineStatement(trimmed, i + 1, false, statements);
            i++;
        }

        return new SourceFile(filePath, "go", types, statements, sourceText)
        {
            Usings = usings,
            Regions = [],
            CommentLines = ExtractCommentLines(lines)
        };
    }

    private static int ParseImports(string[] lines, int startLine, List<string> usings)
    {
        var trimmed = lines[startLine].TrimStart();

        // Single import: import "fmt"
        var singleMatch = Regex.Match(trimmed, @"^import\s+""([^""]+)""");
        if (singleMatch.Success)
        {
            usings.Add(singleMatch.Groups[1].Value);
            return startLine + 1;
        }

        // Block import: import ( ... )
        if (trimmed.Contains('('))
        {
            int i = startLine + 1;
            while (i < lines.Length)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith(")")) return i + 1;
                var importMatch = Regex.Match(line, @"""([^""]+)""");
                if (importMatch.Success)
                    usings.Add(importMatch.Groups[1].Value);
                i++;
            }
            return i;
        }

        return startLine + 1;
    }

    private static (TypeDeclaration?, int) ParseTypeDeclaration(string[] lines, int startLine, List<StatementInfo> statements)
    {
        var trimmed = lines[startLine].TrimStart();
        bool hasDocComment = HasDocComment(lines, startLine);

        // type Name struct { ... }
        var structMatch = Regex.Match(trimmed, @"^type\s+(\w+)\s+struct\b");
        if (structMatch.Success)
        {
            string name = structMatch.Groups[1].Value;
            var modifiers = IsExported(name) ? Modifier.Public : Modifier.Private;

            if (!trimmed.Contains('{'))
            {
                // Look for opening brace on next line
                int braceSearch = startLine + 1;
                while (braceSearch < lines.Length && !lines[braceSearch].Contains('{'))
                    braceSearch++;
                if (braceSearch >= lines.Length)
                    return (new TypeDeclaration(name, TypeKind.Struct, modifiers, [], [], [], [], [], [], startLine + 1)
                    { HasDocComment = hasDocComment }, startLine + 1);

                int braceEnd = FindClosingBrace(lines, braceSearch);
                var fields = ParseStructFields(lines, braceSearch + 1, braceEnd);
                return (new TypeDeclaration(name, TypeKind.Struct, modifiers, [], [], [], [], [], [], startLine + 1)
                { HasDocComment = hasDocComment, Fields = fields }, braceEnd + 1);
            }

            int end = FindClosingBrace(lines, startLine);
            var structFields = ParseStructFields(lines, startLine + 1, end);
            return (new TypeDeclaration(name, TypeKind.Struct, modifiers, [], [], [], [], [], [], startLine + 1)
            { HasDocComment = hasDocComment, Fields = structFields }, end + 1);
        }

        // type Name interface { ... }
        var ifaceMatch = Regex.Match(trimmed, @"^type\s+(\w+)\s+interface\b");
        if (ifaceMatch.Success)
        {
            string name = ifaceMatch.Groups[1].Value;
            var modifiers = IsExported(name) ? Modifier.Public : Modifier.Private;

            if (!trimmed.Contains('{'))
            {
                int braceSearch = startLine + 1;
                while (braceSearch < lines.Length && !lines[braceSearch].Contains('{'))
                    braceSearch++;
                if (braceSearch >= lines.Length)
                    return (new TypeDeclaration(name, TypeKind.Interface, modifiers, [], [], [], [], [], [], startLine + 1)
                    { HasDocComment = hasDocComment }, startLine + 1);

                int braceEnd = FindClosingBrace(lines, braceSearch);
                var methods = ParseInterfaceMethods(lines, braceSearch + 1, braceEnd, startLine + 1);
                return (new TypeDeclaration(name, TypeKind.Interface, modifiers, [], [], [], methods, [], [], startLine + 1)
                { HasDocComment = hasDocComment }, braceEnd + 1);
            }

            int end = FindClosingBrace(lines, startLine);
            var ifaceMethods = ParseInterfaceMethods(lines, startLine + 1, end, startLine + 1);
            return (new TypeDeclaration(name, TypeKind.Interface, modifiers, [], [], [], ifaceMethods, [], [], startLine + 1)
            { HasDocComment = hasDocComment }, end + 1);
        }

        // type Name = ... (type alias) or type Name OtherType
        var aliasMatch = Regex.Match(trimmed, @"^type\s+(\w+)\s+");
        if (aliasMatch.Success)
        {
            string name = aliasMatch.Groups[1].Value;
            var modifiers = IsExported(name) ? Modifier.Public : Modifier.Private;
            return (new TypeDeclaration(name, TypeKind.Class, modifiers, [], [], [], [], [], [], startLine + 1)
            { HasDocComment = hasDocComment }, startLine + 1);
        }

        return (null, startLine + 1);
    }

    private static List<FieldDeclaration> ParseStructFields(string[] lines, int start, int end)
    {
        var fields = new List<FieldDeclaration>();
        for (int i = start; i < end; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//")) continue;

            // Embedded type (no field name, just a type)
            var embeddedMatch = Regex.Match(trimmed, @"^(\*?[A-Z]\w*)\s*(?://.*)?$");
            if (embeddedMatch.Success)
            {
                string typeName = embeddedMatch.Groups[1].Value;
                var visibility = IsExported(typeName.TrimStart('*')) ? Modifier.Public : Modifier.Private;
                fields.Add(new FieldDeclaration(typeName, new TypeReference(typeName, null, [], typeName), visibility, i + 1));
                continue;
            }

            // Named field: Name Type `tag`
            var fieldMatch = Regex.Match(trimmed, @"^(\w+)\s+(.+?)(?:\s+`[^`]*`)?\s*(?://.*)?$");
            if (fieldMatch.Success)
            {
                string fieldName = fieldMatch.Groups[1].Value;
                string fieldType = fieldMatch.Groups[2].Value.Trim();
                var visibility = IsExported(fieldName) ? Modifier.Public : Modifier.Private;
                var typeRef = new TypeReference(fieldType, null, [], fieldType);
                fields.Add(new FieldDeclaration(fieldName, typeRef, visibility, i + 1));
            }
        }
        return fields;
    }

    private static List<MethodDeclaration> ParseInterfaceMethods(string[] lines, int start, int end, int typeLine)
    {
        var methods = new List<MethodDeclaration>();
        for (int i = start; i < end; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//")) continue;

            // Method signature: Name(params) returnType
            var methodMatch = Regex.Match(trimmed, @"^(\w+)\s*\(([^)]*)\)(.*)$");
            if (methodMatch.Success)
            {
                string name = methodMatch.Groups[1].Value;
                var parameters = ParseParameters(methodMatch.Groups[2].Value);
                string retText = methodMatch.Groups[3].Value.Trim();
                TypeReference? returnType = !string.IsNullOrWhiteSpace(retText)
                    ? new TypeReference(retText, null, [], retText) : null;
                var modifiers = IsExported(name) ? Modifier.Public : Modifier.Private;
                bool hasDoc = HasDocComment(lines, i);
                methods.Add(new MethodDeclaration(name, modifiers | Modifier.Abstract, [], returnType, parameters, i + 1)
                { HasDocComment = hasDoc });
            }
        }
        return methods;
    }

    private static (MethodDeclaration?, string?, int) ParseFunc(string[] lines, int startLine, List<StatementInfo> statements)
    {
        var trimmed = lines[startLine].TrimStart();
        bool hasDocComment = HasDocComment(lines, startLine);

        // Join multi-line function signature
        string fullSig = trimmed;
        int nextLine = startLine + 1;
        while (!fullSig.Contains('{') && !fullSig.TrimEnd().EndsWith("}") && nextLine < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[nextLine]))
            {
                nextLine++;
                break;
            }
            fullSig += " " + lines[nextLine].Trim();
            nextLine++;
        }

        // Method with receiver: func (r *Type) Name(...)
        string? receiver = null;
        var receiverMatch = Regex.Match(fullSig, @"^func\s+\(\s*\w+\s+\*?(\w+)\s*\)\s+(\w+)\s*\(([^)]*)\)(.*)");
        // Free function: func Name(...)
        var funcMatch = Regex.Match(fullSig, @"^func\s+(\w+)\s*\(([^)]*)\)(.*)");

        string name;
        List<ParameterDeclaration> parameters;
        TypeReference? returnType = null;

        if (receiverMatch.Success)
        {
            receiver = receiverMatch.Groups[1].Value;
            name = receiverMatch.Groups[2].Value;
            parameters = ParseParameters(receiverMatch.Groups[3].Value);
            var retText = receiverMatch.Groups[4].Value.Trim().TrimEnd('{').Trim();
            if (!string.IsNullOrWhiteSpace(retText))
                returnType = new TypeReference(retText, null, [], retText);
        }
        else if (funcMatch.Success)
        {
            name = funcMatch.Groups[1].Value;
            parameters = ParseParameters(funcMatch.Groups[2].Value);
            var retText = funcMatch.Groups[3].Value.Trim().TrimEnd('{').Trim();
            if (!string.IsNullOrWhiteSpace(retText))
                returnType = new TypeReference(retText, null, [], retText);
        }
        else
        {
            return (null, null, nextLine);
        }

        var modifiers = IsExported(name) ? Modifier.Public : Modifier.Private;

        // Find function body
        int braceSearchLine = startLine;
        while (braceSearchLine < lines.Length && !lines[braceSearchLine].Contains('{'))
            braceSearchLine++;

        if (braceSearchLine >= lines.Length)
            return (new MethodDeclaration(name, modifiers, [], returnType, parameters, startLine + 1)
            { HasDocComment = hasDocComment }, receiver, nextLine);

        int bodyEnd = FindClosingBrace(lines, braceSearchLine);
        nextLine = bodyEnd + 1;

        // Extract statements from body
        var methodStatements = new List<StatementInfo>();
        ExtractBodyStatements(lines, braceSearchLine + 1, bodyEnd, methodStatements);
        statements.AddRange(methodStatements);

        var method = new MethodDeclaration(name, modifiers, [], returnType, parameters, startLine + 1)
        { Statements = methodStatements, HasDocComment = hasDocComment };

        return (method, receiver, nextLine);
    }

    private static void ExtractBodyStatements(string[] lines, int start, int end, List<StatementInfo> statements)
    {
        for (int i = start; i < end; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//")) continue;

            ExtractLineStatement(trimmed, i + 1, true, statements);
        }
    }

    private static void ExtractLineStatement(string trimmed, int lineNumber, bool isInMethod, List<StatementInfo> statements)
    {
        // panic(...) / log.Fatal(...)
        if (trimmed.StartsWith("panic(") || Regex.IsMatch(trimmed, @"\bpanic\("))
        {
            statements.Add(new StatementInfo("throw", [], null, "panic", [], lineNumber, isInMethod));
            return;
        }

        // defer statement
        if (trimmed.StartsWith("defer "))
        {
            var deferCallMatch = Regex.Match(trimmed, @"^defer\s+(?:(\w[\w.]*?)\.)?(\w+)\s*\(");
            if (deferCallMatch.Success)
            {
                string? typeName = deferCallMatch.Groups[1].Success ? deferCallMatch.Groups[1].Value : null;
                string memberName = deferCallMatch.Groups[2].Value;
                statements.Add(new StatementInfo("call", ["defer"], typeName, memberName, [], lineNumber, isInMethod));
            }
            return;
        }

        // go statement (goroutine)
        if (trimmed.StartsWith("go "))
        {
            var goCallMatch = Regex.Match(trimmed, @"^go\s+(?:(\w[\w.]*?)\.)?(\w+)\s*\(");
            if (goCallMatch.Success)
            {
                string? typeName = goCallMatch.Groups[1].Success ? goCallMatch.Groups[1].Value : null;
                string memberName = goCallMatch.Groups[2].Value;
                statements.Add(new StatementInfo("call", ["go"], typeName, memberName, [], lineNumber, isInMethod));
            }
            return;
        }

        // Method call: expr.Method(...)
        var methodCallMatch = Regex.Match(trimmed, @"(?:(\w[\w.]*?)\.)?(\w+)\s*\(");
        if (methodCallMatch.Success)
        {
            string? typeName = methodCallMatch.Groups[1].Success ? methodCallMatch.Groups[1].Value : null;
            string memberName = methodCallMatch.Groups[2].Value;

            // Skip keywords that look like calls
            if (memberName is "if" or "for" or "switch" or "select" or "func" or "return"
                or "range" or "go" or "defer" or "type" or "var" or "const" or "make" or "len"
                or "cap" or "append" or "copy" or "delete" or "close" or "new")
                return;

            statements.Add(new StatementInfo("call", [], typeName, memberName, [], lineNumber, isInMethod));
        }
    }

    private static List<ParameterDeclaration> ParseParameters(string paramString)
    {
        var parameters = new List<ParameterDeclaration>();
        if (string.IsNullOrWhiteSpace(paramString)) return parameters;

        foreach (var part in SplitParameters(paramString))
        {
            var trimmed = part.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Go params: name type or name1, name2 type
            var paramMatch = Regex.Match(trimmed, @"^(\w+)\s+(.+)$");
            if (paramMatch.Success)
            {
                string name = paramMatch.Groups[1].Value;
                string typeText = paramMatch.Groups[2].Value.Trim();
                bool isVariadic = typeText.StartsWith("...");
                if (isVariadic) typeText = typeText[3..];
                var typeRef = new TypeReference(typeText, null, [], typeText);
                parameters.Add(new ParameterDeclaration(name, typeRef, isVariadic, false, false, 0));
            }
            else
            {
                // Just a type (unnamed, or part of multi-name declaration)
                parameters.Add(new ParameterDeclaration(trimmed, null, false, false, false, 0));
            }
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

    private static bool IsExported(string name) =>
        name.Length > 0 && char.IsUpper(name[0]);

    private static bool HasDocComment(string[] lines, int startLine)
    {
        for (int i = startLine - 1; i >= 0; i--)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//"))
                return true;
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;
            break;
        }
        return false;
    }

    private static bool IsDocComment(string[] lines, int lineIndex)
    {
        // In Go, a doc comment is a // comment immediately preceding a declaration
        for (int i = lineIndex + 1; i < lines.Length; i++)
        {
            var next = lines[i].TrimStart();
            if (next.StartsWith("//")) continue;
            if (string.IsNullOrWhiteSpace(next)) return false;
            // Next non-comment line is a declaration
            return next.StartsWith("func ") || next.StartsWith("type ") ||
                   next.StartsWith("var ") || next.StartsWith("const ") ||
                   next.StartsWith("package ");
        }
        return false;
    }

    private static int FindClosingBrace(string[] lines, int openBraceLine)
    {
        int depth = 0;
        for (int i = openBraceLine; i < lines.Length; i++)
        {
            foreach (char c in lines[i])
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
        }
        return lines.Length - 1;
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

    private static int SkipParenBlock(string[] lines, int startLine)
    {
        int depth = 0;
        for (int i = startLine; i < lines.Length; i++)
        {
            foreach (char c in lines[i])
            {
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) return i + 1;
                }
            }
        }
        return lines.Length;
    }

    private static HashSet<int> ExtractCommentLines(string[] lines)
    {
        var commentLines = new HashSet<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//"))
                commentLines.Add(i + 1);
        }
        return commentLines;
    }
}
