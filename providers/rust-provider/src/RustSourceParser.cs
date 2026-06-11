using System.Text.RegularExpressions;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class RustSourceParser : ISourceParser
{
    public override IReadOnlyList<string> Extensions => [".rs"];
    public override string Language => "rust";

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

            // Skip line comments
            if (trimmed.StartsWith("//") && !trimmed.StartsWith("///") && !trimmed.StartsWith("//!"))
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

            // use statements
            if (trimmed.StartsWith("use "))
            {
                ParseUse(trimmed, usings);
                i++;
                continue;
            }

            // Collect attributes for upcoming item
            if (trimmed.StartsWith("#[") || trimmed.StartsWith("#!["))
            {
                i++;
                continue;
            }

            // struct declaration
            if (IsStructDeclaration(trimmed))
            {
                var (type, nextLine) = ParseStruct(lines, i, statements);
                if (type != null) types.Add(type);
                i = nextLine;
                continue;
            }

            // enum declaration
            if (IsEnumDeclaration(trimmed))
            {
                var (type, nextLine) = ParseEnum(lines, i);
                if (type != null) types.Add(type);
                i = nextLine;
                continue;
            }

            // trait declaration
            if (IsTraitDeclaration(trimmed))
            {
                var (type, nextLine) = ParseTrait(lines, i, statements);
                if (type != null) types.Add(type);
                i = nextLine;
                continue;
            }

            // impl block
            if (IsImplBlock(trimmed))
            {
                var (type, nextLine) = ParseImpl(lines, i, statements);
                if (type != null) types.Add(type);
                i = nextLine;
                continue;
            }

            // top-level function
            if (IsFnDeclaration(trimmed))
            {
                var (method, nextLine) = ParseFunction(lines, i, statements);
                i = nextLine;
                continue;
            }

            // Module-level statements
            ExtractLineStatement(trimmed, i + 1, false, statements);
            i++;
        }

        return new SourceFile(filePath, "rust", types, statements, sourceText)
        {
            Usings = usings,
            Regions = [],
            CommentLines = ExtractCommentLines(lines)
        };
    }

    private static bool IsStructDeclaration(string trimmed)
    {
        return Regex.IsMatch(trimmed, @"^(pub(\s*\([^)]*\))?\s+)?struct\s+\w+");
    }

    private static bool IsEnumDeclaration(string trimmed)
    {
        return Regex.IsMatch(trimmed, @"^(pub(\s*\([^)]*\))?\s+)?enum\s+\w+");
    }

    private static bool IsTraitDeclaration(string trimmed)
    {
        return Regex.IsMatch(trimmed, @"^(pub(\s*\([^)]*\))?\s+)?(unsafe\s+)?trait\s+\w+");
    }

    private static bool IsImplBlock(string trimmed)
    {
        return Regex.IsMatch(trimmed, @"^(unsafe\s+)?impl\s*(<[^>]*>)?\s+\w+");
    }

    private static bool IsFnDeclaration(string trimmed)
    {
        return Regex.IsMatch(trimmed, @"^(pub(\s*\([^)]*\))?\s+)?(async\s+)?(unsafe\s+)?(extern\s+""[^""]*""\s+)?fn\s+\w+");
    }

    private static (TypeDeclaration?, int) ParseStruct(string[] lines, int startLine, List<StatementInfo> statements)
    {
        var attributes = CollectAttributes(lines, startLine);
        var trimmed = lines[startLine].TrimStart();
        var match = Regex.Match(trimmed, @"^(pub(\s*\([^)]*\))?\s+)?struct\s+(\w+)");
        if (!match.Success) return (null, startLine + 1);

        string name = match.Groups[3].Value;
        var modifiers = ParseVisibility(trimmed);
        bool hasDocComment = HasDocComment(lines, startLine);

        // Check if it's a tuple struct (ends with ;) or unit struct
        if (trimmed.Contains(';'))
            return (new TypeDeclaration(name, TypeKind.Struct, modifiers, [], attributes, [], [], [], [], startLine + 1)
            { HasDocComment = hasDocComment }, startLine + 1);

        // Find the opening brace
        int nextLine = startLine;
        while (nextLine < lines.Length && !lines[nextLine].Contains('{'))
            nextLine++;

        if (nextLine >= lines.Length)
            return (new TypeDeclaration(name, TypeKind.Struct, modifiers, [], attributes, [], [], [], [], startLine + 1)
            { HasDocComment = hasDocComment }, startLine + 1);

        // Skip to closing brace
        int braceEnd = FindClosingBrace(lines, nextLine);

        // Parse fields
        var fields = ParseStructFields(lines, nextLine + 1, braceEnd);

        return (new TypeDeclaration(name, TypeKind.Struct, modifiers, [], attributes, [], [], [], [], startLine + 1)
        {
            HasDocComment = hasDocComment,
            Fields = fields
        }, braceEnd + 1);
    }

    private static List<FieldDeclaration> ParseStructFields(string[] lines, int start, int end)
    {
        var fields = new List<FieldDeclaration>();
        for (int i = start; i < end; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//") || trimmed.StartsWith("#["))
                continue;

            var fieldMatch = Regex.Match(trimmed, @"^(pub(\s*\([^)]*\))?\s+)?(\w+)\s*:\s*(.+?)\s*,?\s*$");
            if (fieldMatch.Success)
            {
                string fieldName = fieldMatch.Groups[3].Value;
                string fieldType = fieldMatch.Groups[4].Value.TrimEnd(',').Trim();
                var visibility = fieldMatch.Groups[1].Success ? Modifier.Public : Modifier.Private;
                var typeRef = new TypeReference(fieldType, null, [], fieldType);
                fields.Add(new FieldDeclaration(fieldName, typeRef, visibility, i + 1));
            }
        }
        return fields;
    }

    private static (TypeDeclaration?, int) ParseEnum(string[] lines, int startLine)
    {
        var attributes = CollectAttributes(lines, startLine);
        var trimmed = lines[startLine].TrimStart();
        var match = Regex.Match(trimmed, @"^(pub(\s*\([^)]*\))?\s+)?enum\s+(\w+)");
        if (!match.Success) return (null, startLine + 1);

        string name = match.Groups[3].Value;
        var modifiers = ParseVisibility(trimmed);
        bool hasDocComment = HasDocComment(lines, startLine);

        // Find the opening brace
        int nextLine = startLine;
        while (nextLine < lines.Length && !lines[nextLine].Contains('{'))
            nextLine++;

        if (nextLine >= lines.Length)
            return (new TypeDeclaration(name, TypeKind.Enum, modifiers, [], attributes, [], [], [], [], startLine + 1)
            { HasDocComment = hasDocComment }, startLine + 1);

        int braceEnd = FindClosingBrace(lines, nextLine);

        // Parse enum variants
        var variants = new List<string>();
        for (int i = nextLine + 1; i < braceEnd; i++)
        {
            var varTrimmed = lines[i].TrimStart();
            if (string.IsNullOrWhiteSpace(varTrimmed) || varTrimmed.StartsWith("//") || varTrimmed.StartsWith("#["))
                continue;
            var variantMatch = Regex.Match(varTrimmed, @"^(\w+)");
            if (variantMatch.Success)
                variants.Add(variantMatch.Groups[1].Value);
        }

        return (new TypeDeclaration(name, TypeKind.Enum, modifiers, [], attributes, [], [], [], variants, startLine + 1)
        { HasDocComment = hasDocComment }, braceEnd + 1);
    }

    private static (TypeDeclaration?, int) ParseTrait(string[] lines, int startLine, List<StatementInfo> statements)
    {
        var attributes = CollectAttributes(lines, startLine);
        var trimmed = lines[startLine].TrimStart();
        var match = Regex.Match(trimmed, @"^(pub(\s*\([^)]*\))?\s+)?(unsafe\s+)?trait\s+(\w+)(\s*:\s*(.+?))?(\s*\{|\s*where)");
        if (!match.Success)
        {
            // Try simpler match without trailing brace/where
            match = Regex.Match(trimmed, @"^(pub(\s*\([^)]*\))?\s+)?(unsafe\s+)?trait\s+(\w+)");
            if (!match.Success) return (null, startLine + 1);
        }

        string name = match.Groups[4].Value;
        var modifiers = ParseVisibility(trimmed);
        bool hasDocComment = HasDocComment(lines, startLine);

        // Parse super-traits
        var baseTypes = new List<string>();
        if (match.Groups[6].Success && !string.IsNullOrWhiteSpace(match.Groups[6].Value))
        {
            foreach (var bt in match.Groups[6].Value.Split('+', StringSplitOptions.TrimEntries))
            {
                var baseType = bt.Split('<')[0].Trim();
                if (!string.IsNullOrWhiteSpace(baseType) && baseType != "{")
                    baseTypes.Add(baseType);
            }
        }

        // Find the opening brace
        int nextLine = startLine;
        while (nextLine < lines.Length && !lines[nextLine].Contains('{'))
            nextLine++;

        if (nextLine >= lines.Length)
            return (new TypeDeclaration(name, TypeKind.Interface, modifiers, baseTypes, attributes, [], [], [], [], startLine + 1)
            { HasDocComment = hasDocComment }, startLine + 1);

        int braceEnd = FindClosingBrace(lines, nextLine);

        // Parse methods in trait
        var methods = new List<MethodDeclaration>();
        int mi = nextLine + 1;
        while (mi < braceEnd)
        {
            var mTrimmed = lines[mi].TrimStart();
            if (IsFnDeclaration(mTrimmed))
            {
                var (method, mNext) = ParseFunction(lines, mi, statements);
                if (method != null) methods.Add(method);
                mi = mNext;
            }
            else
            {
                mi++;
            }
        }

        return (new TypeDeclaration(name, TypeKind.Interface, modifiers, baseTypes, attributes, [], methods, [], [], startLine + 1)
        { HasDocComment = hasDocComment }, braceEnd + 1);
    }

    private static (TypeDeclaration?, int) ParseImpl(string[] lines, int startLine, List<StatementInfo> statements)
    {
        var trimmed = lines[startLine].TrimStart();

        // impl Trait for Type or impl Type
        var traitForMatch = Regex.Match(trimmed, @"^(unsafe\s+)?impl\s*(<[^>]*>)?\s+(\w+)(<[^>]*>)?\s+for\s+(\w+)");
        var simpleMatch = Regex.Match(trimmed, @"^(unsafe\s+)?impl\s*(<[^>]*>)?\s+(\w+)");

        string name;
        var baseTypes = new List<string>();
        bool isTrait = false;

        if (traitForMatch.Success)
        {
            name = traitForMatch.Groups[5].Value;
            baseTypes.Add(traitForMatch.Groups[3].Value);
            isTrait = true;
        }
        else if (simpleMatch.Success)
        {
            name = simpleMatch.Groups[3].Value;
        }
        else
        {
            return (null, startLine + 1);
        }

        // Find the opening brace
        int nextLine = startLine;
        while (nextLine < lines.Length && !lines[nextLine].Contains('{'))
            nextLine++;

        if (nextLine >= lines.Length)
            return (null, startLine + 1);

        int braceEnd = FindClosingBrace(lines, nextLine);

        // Parse methods in impl block
        var methods = new List<MethodDeclaration>();
        int mi = nextLine + 1;
        while (mi < braceEnd)
        {
            var mTrimmed = lines[mi].TrimStart();
            if (IsFnDeclaration(mTrimmed))
            {
                var (method, mNext) = ParseFunction(lines, mi, statements);
                if (method != null) methods.Add(method);
                mi = mNext;
            }
            else
            {
                mi++;
            }
        }

        // Separate constructors (new) from regular methods
        var constructors = methods.Where(m => m.Name == "new").ToList();
        var regularMethods = methods.Where(m => m.Name != "new").ToList();

        return (new TypeDeclaration(name + (isTrait ? $" (impl {baseTypes[0]})" : " (impl)"),
            TypeKind.Class, Modifier.Public, baseTypes, [], constructors, regularMethods, [], [], startLine + 1), braceEnd + 1);
    }

    private static (MethodDeclaration?, int) ParseFunction(string[] lines, int startLine, List<StatementInfo> statements)
    {
        var attributes = CollectAttributes(lines, startLine);
        var trimmed = lines[startLine].TrimStart();
        bool hasDocComment = HasDocComment(lines, startLine);

        // Join multi-line function signature
        string fullSig = trimmed;
        int nextLine = startLine + 1;
        while (!fullSig.Contains('{') && !fullSig.Contains(';') && nextLine < lines.Length)
        {
            fullSig += " " + lines[nextLine].Trim();
            nextLine++;
        }

        var fnMatch = Regex.Match(fullSig, @"^(pub(\s*\([^)]*\))?\s+)?(async\s+)?(unsafe\s+)?(extern\s+""[^""]*""\s+)?fn\s+(\w+)\s*(<[^>]*>)?\s*\(([^)]*)\)(\s*->\s*(.+?))?(\s*where\s+.+?)?\s*[{;]");
        if (!fnMatch.Success)
        {
            // Simpler match without body
            fnMatch = Regex.Match(fullSig, @"^(pub(\s*\([^)]*\))?\s+)?(async\s+)?(unsafe\s+)?(extern\s+""[^""]*""\s+)?fn\s+(\w+)");
            if (!fnMatch.Success) return (null, nextLine);
        }

        string name = fnMatch.Groups[6].Value;
        var modifiers = ParseVisibility(fullSig);
        if (fnMatch.Groups[3].Success) modifiers |= Modifier.Async;

        // Parse parameters
        var parameters = new List<ParameterDeclaration>();
        if (fnMatch.Groups[8].Success)
            parameters = ParseParameters(fnMatch.Groups[8].Value);

        // Parse return type
        TypeReference? returnType = null;
        if (fnMatch.Groups[10].Success && !string.IsNullOrWhiteSpace(fnMatch.Groups[10].Value))
        {
            var retText = fnMatch.Groups[10].Value.Trim();
            returnType = new TypeReference(retText, null, [], retText);
        }

        // Find function body and extract statements
        int bodyStart;
        int bodyEnd;
        if (fullSig.Contains(';') && !fullSig.Contains('{'))
        {
            // Trait method declaration (no body)
            bodyStart = nextLine;
            bodyEnd = nextLine;
        }
        else
        {
            // Find opening brace in the original lines
            int braceSearchLine = startLine;
            while (braceSearchLine < lines.Length && !lines[braceSearchLine].Contains('{'))
                braceSearchLine++;

            if (braceSearchLine >= lines.Length)
                return (new MethodDeclaration(name, modifiers, attributes, returnType, parameters, startLine + 1)
                { HasDocComment = hasDocComment }, nextLine);

            bodyEnd = FindClosingBrace(lines, braceSearchLine);
            bodyStart = braceSearchLine + 1;
            nextLine = bodyEnd + 1;
        }

        var methodStatements = new List<StatementInfo>();
        ExtractBodyStatements(lines, bodyStart, bodyEnd, methodStatements);
        statements.AddRange(methodStatements);

        return (new MethodDeclaration(name, modifiers, attributes, returnType, parameters, startLine + 1)
        { Statements = methodStatements, HasDocComment = hasDocComment }, nextLine);
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
        // panic! / todo! / unimplemented! macros
        var macroMatch = Regex.Match(trimmed, @"^(\w+)!\s*\(");
        if (macroMatch.Success)
        {
            string macroName = macroMatch.Groups[1].Value;
            if (macroName is "panic" or "todo" or "unimplemented" or "unreachable")
            {
                statements.Add(new StatementInfo("throw", [], null, macroName, [], lineNumber, isInMethod));
                return;
            }
            // Other macro invocations treated as calls
            statements.Add(new StatementInfo("call", [], null, macroName + "!", [], lineNumber, isInMethod));
            return;
        }

        // Method call: expr.method(...)
        var methodCallMatch = Regex.Match(trimmed, @"(?:(\w[\w:]*)\.)(\w+)\s*\(");
        if (methodCallMatch.Success)
        {
            string? typeName = methodCallMatch.Groups[1].Value;
            string memberName = methodCallMatch.Groups[2].Value;
            if (memberName is "if" or "for" or "while" or "loop" or "match" or "let" or "return" or "fn" or "struct" or "enum" or "impl" or "trait" or "use" or "mod")
                return;
            statements.Add(new StatementInfo("call", [], typeName, memberName, [], lineNumber, isInMethod));
            return;
        }

        // Function call: name(...) or path::name(...)
        var fnCallMatch = Regex.Match(trimmed, @"^(?:let\s+\w+\s*=\s*)?(?:(\w[\w:]*?)::)?(\w+)\s*\(");
        if (fnCallMatch.Success)
        {
            string? typeName = fnCallMatch.Groups[1].Success ? fnCallMatch.Groups[1].Value : null;
            string memberName = fnCallMatch.Groups[2].Value;
            if (memberName is "if" or "for" or "while" or "loop" or "match" or "let" or "return" or "fn"
                or "struct" or "enum" or "impl" or "trait" or "use" or "mod" or "pub" or "async" or "unsafe")
                return;
            statements.Add(new StatementInfo("call", [], typeName, memberName, [], lineNumber, isInMethod));
            return;
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

            // Skip self parameters
            if (trimmed is "self" or "&self" or "&mut self" or "mut self"
                || trimmed.StartsWith("self:") || trimmed.StartsWith("&self"))
                continue;

            // Pattern: name: Type
            var paramMatch = Regex.Match(trimmed, @"^(mut\s+)?(\w+)\s*:\s*(.+)$");
            if (paramMatch.Success)
            {
                string name = paramMatch.Groups[2].Value;
                string typeText = paramMatch.Groups[3].Value.Trim();
                var typeRef = new TypeReference(typeText, null, [], typeText);
                bool isVariadic = typeText.StartsWith("...");
                parameters.Add(new ParameterDeclaration(name, typeRef, isVariadic, false, false, 0));
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
            if (s[i] is '(' or '[' or '{' or '<') depth++;
            else if (s[i] is ')' or ']' or '}' or '>') depth--;
            else if (s[i] == ',' && depth == 0)
            {
                result.Add(s[start..i]);
                start = i + 1;
            }
        }
        result.Add(s[start..]);
        return result;
    }

    private static Modifier ParseVisibility(string line)
    {
        if (Regex.IsMatch(line, @"^pub(\s*\(|[\s])"))
            return Modifier.Public;
        return Modifier.Private;
    }

    private static bool HasDocComment(string[] lines, int startLine)
    {
        for (int i = startLine - 1; i >= 0; i--)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("///") || trimmed.StartsWith("//!"))
                return true;
            if (trimmed.StartsWith("#[") || string.IsNullOrWhiteSpace(trimmed))
                continue;
            break;
        }
        return false;
    }

    private static List<string> CollectAttributes(string[] lines, int startLine)
    {
        var attributes = new List<string>();
        for (int i = startLine - 1; i >= 0; i--)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("#["))
            {
                var attrMatch = Regex.Match(trimmed, @"#\[(.+?)\]");
                if (attrMatch.Success)
                    attributes.Insert(0, attrMatch.Groups[1].Value);
            }
            else if (trimmed.StartsWith("///") || trimmed.StartsWith("//!") || string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }
            else
            {
                break;
            }
        }
        return attributes;
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
        int depth = 0;
        for (int i = startLine; i < lines.Length; i++)
        {
            var line = lines[i];
            for (int j = 0; j < line.Length - 1; j++)
            {
                if (line[j] == '/' && line[j + 1] == '*')
                {
                    depth++;
                    j++;
                }
                else if (line[j] == '*' && line[j + 1] == '/')
                {
                    depth--;
                    if (depth == 0) return i + 1;
                    j++;
                }
            }
        }
        return lines.Length;
    }

    private static void ParseUse(string trimmed, List<string> usings)
    {
        // use std::collections::HashMap;
        // use crate::module::Type;
        var match = Regex.Match(trimmed, @"^use\s+(.+?)\s*;");
        if (match.Success)
        {
            var path = match.Groups[1].Value;
            // Handle braced imports: use std::{fmt, io}
            if (path.Contains('{'))
            {
                var baseMatch = Regex.Match(path, @"^(.+?)::\{(.+)\}$");
                if (baseMatch.Success)
                {
                    var basePath = baseMatch.Groups[1].Value;
                    foreach (var item in baseMatch.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries))
                    {
                        var itemName = item.Split(" as ")[0].Trim();
                        if (!string.IsNullOrWhiteSpace(itemName))
                            usings.Add($"{basePath}::{itemName}");
                    }
                }
            }
            else
            {
                var importPath = path.Split(" as ")[0].Trim();
                usings.Add(importPath);
            }
        }
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
