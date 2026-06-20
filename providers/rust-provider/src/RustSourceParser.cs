using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class RustSourceParser : ISourceParser
{
    public override IReadOnlyList<string> Extensions => [".rs"];
    public override string Language => "rust";

    public override SourceFile? Parse(string filePath, string sourceText)
    {
        var lexer = new RustLexer(sourceText);
        var tokens = lexer.Tokenize();
        var parser = new RustParser(tokens, sourceText);
        return parser.Parse(filePath);
    }
}

#region Lexer

internal enum RustTokenKind
{
    Identifier, Keyword, Punctuation, StringLiteral, NumberLiteral,
    LineComment, DocComment, BlockComment, Attribute, Lifetime, Eof
}

internal record struct RustToken(RustTokenKind Kind, string Value, int Line, int Start, int End);

internal class RustLexer(string source)
{
    private int _pos;
    private int _line = 1;

    private static readonly HashSet<string> Keywords =
    [
        "as", "async", "await", "break", "const", "continue", "crate", "dyn",
        "else", "enum", "extern", "false", "fn", "for", "if", "impl", "in",
        "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return",
        "self", "Self", "static", "struct", "super", "trait", "true", "type",
        "unsafe", "use", "where", "while", "yield"
    ];

    public List<RustToken> Tokenize()
    {
        var tokens = new List<RustToken>();
        while (_pos < source.Length)
        {
            SkipWhitespace();
            if (_pos >= source.Length) break;

            char c = source[_pos];

            // Line/doc/block comments
            if (c == '/' && _pos + 1 < source.Length)
            {
                if (source[_pos + 1] == '/')
                {
                    var token = ReadLineComment();
                    tokens.Add(token);
                    continue;
                }
                if (source[_pos + 1] == '*')
                {
                    var token = ReadBlockComment();
                    tokens.Add(token);
                    continue;
                }
            }

            // Attributes: #[...] or #![...]
            if (c == '#' && _pos + 1 < source.Length && (source[_pos + 1] == '[' || (source[_pos + 1] == '!' && _pos + 2 < source.Length && source[_pos + 2] == '[')))
            {
                tokens.Add(ReadAttribute());
                continue;
            }

            // String literals
            if (c == '"')
            {
                tokens.Add(ReadStringLiteral());
                continue;
            }

            // Raw strings: r"..." r#"..."#  — but NOT raw identifiers r#ident
            if (c == 'r' && _pos + 1 < source.Length && source[_pos + 1] == '"')
            {
                tokens.Add(ReadRawString());
                continue;
            }
            if (c == 'r' && _pos + 1 < source.Length && source[_pos + 1] == '#')
            {
                // A raw string has a '"' after the run of '#'; a raw identifier (r#ident)
                // has an identifier char there instead.
                int k = _pos + 1;
                while (k < source.Length && source[k] == '#') k++;
                if (k < source.Length && source[k] == '"')
                {
                    tokens.Add(ReadRawString());
                    continue;
                }
                if (k < source.Length && (char.IsLetter(source[k]) || source[k] == '_'))
                {
                    tokens.Add(ReadRawIdentifier());
                    continue;
                }
            }

            // Char literal vs lifetime: 'x' / '\n' are char literals; 'a / 'static / '_ are lifetimes.
            if (c == '\'')
            {
                bool isChar = (_pos + 1 < source.Length && source[_pos + 1] == '\\')      // escaped: '\n'
                    || (_pos + 2 < source.Length && source[_pos + 2] == '\'');            // single char: 'a'
                if (isChar) { tokens.Add(ReadCharLiteral()); continue; }
                tokens.Add(ReadLifetime());
                continue;
            }

            // Numbers
            if (char.IsDigit(c))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            // Identifiers and keywords
            if (char.IsLetter(c) || c == '_')
            {
                tokens.Add(ReadIdentifierOrKeyword());
                continue;
            }

            // Punctuation (single or multi-char)
            tokens.Add(ReadPunctuation());
        }

        tokens.Add(new RustToken(RustTokenKind.Eof, "", _line, _pos, _pos));
        return tokens;
    }

    private void SkipWhitespace()
    {
        while (_pos < source.Length)
        {
            char ch = source[_pos];
            if (ch is ' ' or '\t' or '\r') { _pos++; }
            else if (ch == '\n') { _line++; _pos++; }
            else break;
        }
    }

    private RustToken ReadLineComment()
    {
        int start = _pos;
        int line = _line;
        _pos += 2; // skip //
        // /// is an outer doc, //! is an inner doc, but //// (or more) is a plain comment.
        bool isDoc = false;
        if (_pos < source.Length)
        {
            char c3 = source[_pos];
            if (c3 == '!') isDoc = true;
            else if (c3 == '/' && (_pos + 1 >= source.Length || source[_pos + 1] != '/')) isDoc = true;
        }
        while (_pos < source.Length && source[_pos] != '\n')
            _pos++;
        var value = source[start.._pos];
        return new RustToken(isDoc ? RustTokenKind.DocComment : RustTokenKind.LineComment, value, line, start, _pos);
    }

    private RustToken ReadBlockComment()
    {
        int start = _pos;
        int line = _line;
        int depth = 0;
        while (_pos < source.Length)
        {
            if (source[_pos] == '/' && _pos + 1 < source.Length && source[_pos + 1] == '*')
            { depth++; _pos += 2; }
            else if (source[_pos] == '*' && _pos + 1 < source.Length && source[_pos + 1] == '/')
            { depth--; _pos += 2; if (depth == 0) break; }
            else
            {
                if (source[_pos] == '\n') _line++;
                _pos++;
            }
        }
        var value = source[start.._pos];
        // /** ... */ is an outer doc and /*! ... */ an inner doc; /**/ and /*** ... */ are not.
        RustTokenKind kind = RustTokenKind.BlockComment;
        if (value.Length >= 3 && value[1] == '*')
        {
            if (value[2] == '!')
                kind = RustTokenKind.DocComment;
            else if (value[2] == '*' && (value.Length < 4 || (value[3] != '*' && value[3] != '/')))
                kind = RustTokenKind.DocComment;
        }
        return new RustToken(kind, value, line, start, _pos);
    }

    private RustToken ReadAttribute()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip #
        if (_pos < source.Length && source[_pos] == '!') _pos++;
        if (_pos < source.Length && source[_pos] == '[')
        {
            int depth = 0;
            while (_pos < source.Length)
            {
                if (source[_pos] == '[') depth++;
                else if (source[_pos] == ']') { depth--; if (depth == 0) { _pos++; break; } }
                else if (source[_pos] == '\n') _line++;
                _pos++;
            }
        }
        return new RustToken(RustTokenKind.Attribute, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadStringLiteral()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip opening "
        while (_pos < source.Length && source[_pos] != '"')
        {
            if (source[_pos] == '\\') _pos++; // skip escape
            if (_pos < source.Length && source[_pos] == '\n') _line++;
            _pos++;
        }
        if (_pos < source.Length) _pos++; // skip closing "
        return new RustToken(RustTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadRawString()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip r
        int hashes = 0;
        while (_pos < source.Length && source[_pos] == '#') { hashes++; _pos++; }
        if (_pos < source.Length && source[_pos] == '"') _pos++; // skip "
        // Find closing "###
        while (_pos < source.Length)
        {
            if (source[_pos] == '"')
            {
                _pos++;
                int endHashes = 0;
                while (_pos < source.Length && source[_pos] == '#' && endHashes < hashes) { endHashes++; _pos++; }
                if (endHashes == hashes) break;
            }
            else
            {
                if (source[_pos] == '\n') _line++;
                _pos++;
            }
        }
        return new RustToken(RustTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadCharLiteral()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip '
        if (_pos < source.Length && source[_pos] == '\\') _pos++;
        if (_pos < source.Length) _pos++;
        if (_pos < source.Length && source[_pos] == '\'') _pos++;
        return new RustToken(RustTokenKind.StringLiteral, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadLifetime()
    {
        int start = _pos;
        int line = _line;
        _pos++; // skip '
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
            _pos++;
        return new RustToken(RustTokenKind.Lifetime, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadRawIdentifier()
    {
        int start = _pos;
        int line = _line;
        _pos += 2; // skip r#
        int nameStart = _pos;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
            _pos++;
        // A raw identifier is always an identifier (never a keyword) — that is its purpose.
        return new RustToken(RustTokenKind.Identifier, source[nameStart.._pos], line, start, _pos);
    }

    private RustToken ReadNumber()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] is '.' or '_'))
            _pos++;
        return new RustToken(RustTokenKind.NumberLiteral, source[start.._pos], line, start, _pos);
    }

    private RustToken ReadIdentifierOrKeyword()
    {
        int start = _pos;
        int line = _line;
        while (_pos < source.Length && (char.IsLetterOrDigit(source[_pos]) || source[_pos] == '_'))
            _pos++;
        var value = source[start.._pos];
        var kind = Keywords.Contains(value) ? RustTokenKind.Keyword : RustTokenKind.Identifier;
        return new RustToken(kind, value, line, start, _pos);
    }

    private RustToken ReadPunctuation()
    {
        int start = _pos;
        int line = _line;
        // Multi-char operators
        if (_pos + 1 < source.Length)
        {
            var two = source.Substring(_pos, 2);
            if (two is "::" or "->" or "=>" or "&&" or "||" or "==" or "!=" or ">=" or "<=" or ".." or "+=" or "-=" or "*=" or "/=")
            {
                _pos += 2;
                return new RustToken(RustTokenKind.Punctuation, two, line, start, _pos);
            }
        }
        _pos++;
        return new RustToken(RustTokenKind.Punctuation, source[start.._pos], line, start, _pos);
    }
}

#endregion

#region Parser

internal class RustParser(List<RustToken> tokens, string sourceText)
{
    private int _pos;

    public SourceFile Parse(string filePath)
    {
        var types = new List<TypeDeclaration>();
        var statements = new List<StatementInfo>();
        var usings = new List<string>();
        var freeFunctions = new List<MethodDeclaration>();

        while (!IsAtEnd())
        {
            int loopStart = _pos;
            SkipCommentsAndAttributes();
            if (IsAtEnd()) break;

            // Skip `#[cfg(test)] mod tests { ... }` — its contents are test code, not production.
            if (CheckKeyword("mod") && PrecededByCfgTest())
            {
                SkipModuleBody();
            }
            // Skip `macro_rules! name { ... }` — its body is a template, not real code.
            else if (CheckIdent("macro_rules") && Peek().Value == "!")
            {
                Advance(); Advance();        // macro_rules !
                ConsumeIdentifier();         // macro name
                if (Check("{")) SkipBraces();
                else if (Check("(")) { SkipParens(); if (Check(";")) Advance(); }
                else if (Check("[")) { SkipBrackets(); if (Check(";")) Advance(); }
            }
            else if (MatchKeyword("use"))
            {
                ParseUse(usings);
            }
            else if (CheckKeyword("struct") || CheckKeyword("enum") || CheckKeyword("trait")
                || CheckKeyword("impl") || CheckKeyword("pub") || CheckKeyword("unsafe")
                || (CheckIdent("union") && Peek().Kind == RustTokenKind.Identifier))
            {
                var type = TryParseTypeOrImpl(statements, freeFunctions);
                if (type != null) types.Add(type);
                // If null (e.g. a `pub fn` free function), the function was parsed in place and
                // collected into freeFunctions; or the position was reset and the global
                // progress guard below advances past the unrecognized token.
            }
            else if ((CheckKeyword("fn") || CheckKeyword("async") || CheckKeyword("const")
                || CheckKeyword("extern")) && IsFnAhead())
            {
                var (m, _) = ParseFn(statements);
                if (m != null) freeFunctions.Add(m);
            }

            // Guarantee forward progress on every iteration — never spin on a token that
            // no branch consumed (stray punctuation, top-level const/mod/type, etc.).
            if (_pos == loopStart) Advance();
        }

        // Free functions are part of a module's API surface but don't belong to a declared
        // type, so expose them as methods of a synthetic per-file container (a Class, which
        // type-level naming/doc checks deliberately skip).
        if (freeFunctions.Count > 0)
        {
            var moduleName = System.IO.Path.GetFileNameWithoutExtension(filePath) + " (functions)";
            types.Add(new TypeDeclaration(moduleName, TypeKind.Class, Modifier.Public, [], [], [], freeFunctions, [], [], 0).AsRust());
        }

        return new SourceFile(filePath, "rust", types, statements, sourceText)
        {
            Usings = usings,
            Regions = [],
            CommentLines = ExtractCommentLines()
        };
    }

    private TypeDeclaration? TryParseTypeOrImpl(List<StatementInfo> statements, List<MethodDeclaration> freeFunctions)
    {
        int savedPos = _pos;
        var attributes = CollectPrecedingAttributes();
        bool hasDoc = HasPrecedingDocComment();

        Modifier vis = ReadVisibility();

        bool isUnsafe = MatchKeyword("unsafe");

        if (CheckKeyword("struct")) return ParseStruct(vis, attributes, hasDoc).AsRust(isUnsafe: isUnsafe);
        if (CheckKeyword("enum")) return ParseEnum(vis, attributes, hasDoc).AsRust(isUnsafe: isUnsafe);
        if (CheckKeyword("trait")) return ParseTrait(vis, attributes, hasDoc, statements).AsRust(isTrait: true, isUnsafe: isUnsafe);
        if (CheckKeyword("impl")) return ParseImpl(statements)?.AsRust(isImpl: true, isUnsafe: isUnsafe);
        if (CheckIdent("union") && Peek().Kind == RustTokenKind.Identifier)
            return ParseUnion(vis, attributes, hasDoc).AsRust(isUnsafe: isUnsafe);

        // A free function (possibly pub/const/async/extern/unsafe fn). Parse it here exactly
        // once with its real visibility and collect it; the main loop's fn branch only runs
        // for non-pub free fns (which never reach this method).
        if (CheckKeyword("fn") || CheckKeyword("async") || CheckKeyword("const")
            || CheckKeyword("extern"))
        {
            var (m, _) = ParseFn(statements);
            if (m != null)
            {
                if (vis != Modifier.Private) m = m with { Modifiers = m.Modifiers | vis };
                freeFunctions.Add(m);
            }
            return null;
        }

        _pos = savedPos;
        return null;
    }

    private Modifier ReadVisibility()
    {
        if (!MatchKeyword("pub")) return Modifier.Private;
        if (Check("(")) { SkipParens(); return Modifier.Internal; } // pub(crate)/pub(super)/pub(in path)
        return Modifier.Public;
    }

    private void SkipWhereClause()
    {
        // A where clause runs until the item body `{` or a terminating `;` (body-less items).
        while (!IsAtEnd() && !Check("{") && !Check(";")) Advance();
    }

    private TypeDeclaration ParseUnion(Modifier vis, List<string> attributes, bool hasDoc)
    {
        int line = CurrentLine();
        Advance(); // skip 'union' (a contextual keyword, lexed as an identifier)
        string name = ConsumeIdentifier();
        SkipGenerics();
        if (CheckKeyword("where")) SkipWhereClause();
        var fields = new List<FieldDeclaration>();
        if (Check("{")) fields = ParseStructFields();
        return new TypeDeclaration(name, TypeKind.Struct, vis, [], attributes, [], [], [], [], line)
        { HasDocComment = hasDoc, Fields = fields };
    }

    private TypeDeclaration ParseStruct(Modifier vis, List<string> attributes, bool hasDoc)
    {
        int line = CurrentLine();
        Advance(); // skip 'struct'
        string name = ConsumeIdentifier();
        SkipGenerics();

        var fields = new List<FieldDeclaration>();

        if (Check("("))
        {
            // tuple struct: `struct S(T);` or `struct S(T) where T: X;`
            SkipParens();
            if (CheckKeyword("where")) SkipWhereClause();
            if (Check(";")) Advance();
        }
        else
        {
            if (CheckKeyword("where")) SkipWhereClause();
            if (Check(";")) { Advance(); }                  // unit struct (optionally with where)
            else if (Check("{")) { fields = ParseStructFields(); }
        }

        return new TypeDeclaration(name, TypeKind.Struct, vis, [], attributes, [], [], [], [], line)
        { HasDocComment = hasDoc, Fields = fields };
    }

    private List<FieldDeclaration> ParseStructFields()
    {
        var fields = new List<FieldDeclaration>();
        Advance(); // skip {
        while (!IsAtEnd() && !Check("}"))
        {
            SkipCommentsAndAttributes();
            if (Check("}")) break;

            bool fieldPub = MatchKeyword("pub");
            if (fieldPub && Check("(")) SkipParens();

            if (Check("}")) break;
            string fieldName = ConsumeIdentifier();
            if (fieldName == "" || !Check(":")) { SkipUntilCommaOrBrace(); continue; }
            Advance(); // skip :
            string fieldType = ConsumeType();
            var vis = fieldPub ? Modifier.Public : Modifier.Private;
            fields.Add(new FieldDeclaration(fieldName, new TypeReference(fieldType, null, [], fieldType), vis, CurrentLine()));
            if (Check(",")) Advance();
        }
        if (Check("}")) Advance();
        return fields;
    }

    private TypeDeclaration ParseEnum(Modifier vis, List<string> attributes, bool hasDoc)
    {
        int line = CurrentLine();
        Advance(); // skip 'enum'
        string name = ConsumeIdentifier();
        SkipGenerics();
        if (CheckKeyword("where")) SkipWhereClause();

        var modifiers = vis;
        var variants = new List<string>();

        if (Check("{"))
        {
            Advance();
            while (!IsAtEnd() && !Check("}"))
            {
                int loopStart = _pos;
                SkipCommentsAndAttributes();
                if (Check("}")) break;
                string variantName = ConsumeIdentifier();
                if (variantName != "") variants.Add(variantName);
                // Skip variant data
                if (Check("(")) SkipParens();
                if (Check("{")) SkipBraces();
                if (Check("=")) { Advance(); while (!IsAtEnd() && !Check(",") && !Check("}")) Advance(); }
                if (Check(",")) Advance();
                if (_pos == loopStart) Advance();
            }
            if (Check("}")) Advance();
        }

        return new TypeDeclaration(name, TypeKind.Enum, modifiers, [], attributes, [], [], [], variants, line)
        { HasDocComment = hasDoc };
    }

    private TypeDeclaration ParseTrait(Modifier vis, List<string> attributes, bool hasDoc, List<StatementInfo> statements)
    {
        int line = CurrentLine();
        Advance(); // skip 'trait'
        string name = ConsumeIdentifier();
        SkipGenerics();

        var baseTypes = new List<string>();
        if (Check(":"))
        {
            Advance();
            while (!IsAtEnd() && !Check("{") && !CheckKeyword("where"))
            {
                int boundStart = _pos;
                SkipCommentsAndAttributes();
                if (!IsAtEnd() && Current().Kind == RustTokenKind.Lifetime)
                {
                    Advance(); // lifetime supertrait bound, e.g. `: 'static`
                }
                else
                {
                    string bt = ConsumeIdentifier();
                    if (bt != "") baseTypes.Add(bt);
                    SkipGenerics();
                }
                if (Check("+")) Advance();
                else if (_pos == boundStart) Advance(); // progress guard
                else break;
            }
        }
        if (CheckKeyword("where")) SkipWhereClause();

        var methods = new List<MethodDeclaration>();
        if (Check("{"))
        {
            Advance();
            while (!IsAtEnd() && !Check("}"))
            {
                SkipCommentsAndAttributes();
                if (Check("}")) break;
                if (CheckKeyword("fn") || CheckKeyword("async") || CheckKeyword("unsafe")
                    || CheckKeyword("const") || CheckKeyword("extern"))
                {
                    var (m, _) = ParseFn(statements);
                    // Trait items inherit the trait's visibility (a pub trait's methods are
                    // public API), so doc/visibility checks can see them.
                    if (m != null)
                    {
                        if (vis != Modifier.Private) m = m with { Modifiers = m.Modifiers | vis };
                        methods.Add(m);
                    }
                }
                else Advance();
            }
            if (Check("}")) Advance();
        }

        return new TypeDeclaration(name, TypeKind.Interface, vis, baseTypes, attributes, [], methods, [], [], line)
        { HasDocComment = hasDoc };
    }

    private TypeDeclaration? ParseImpl(List<StatementInfo> statements)
    {
        int line = CurrentLine();
        Advance(); // skip 'impl'
        SkipGenerics();

        // Read the first type/trait path (handles std::fmt::Debug, &T, [T], (A,B), dyn X, generics).
        string first = ConsumeImplTypeName();

        string typeName;
        var baseTypes = new List<string>();

        if (MatchKeyword("for"))
        {
            // impl Trait for Type
            typeName = ConsumeImplTypeName();
            if (first != "") baseTypes.Add(first);
        }
        else
        {
            typeName = first;
        }

        if (CheckKeyword("where")) SkipWhereClause();

        var methods = new List<MethodDeclaration>();
        if (Check("{"))
        {
            Advance();
            while (!IsAtEnd() && !Check("}"))
            {
                int loopStart = _pos;
                SkipCommentsAndAttributes();
                if (Check("}")) break;
                bool methodPub = MatchKeyword("pub");
                if (methodPub && Check("(")) SkipParens();
                if (CheckKeyword("fn") || CheckKeyword("async") || CheckKeyword("unsafe")
                    || CheckKeyword("const") || CheckKeyword("extern") || CheckKeyword("default"))
                {
                    var (m, _) = ParseFn(statements);
                    if (m != null)
                    {
                        if (methodPub) m = m with { Modifiers = m.Modifiers | Modifier.Public };
                        methods.Add(m);
                    }
                }
                else Advance();
                if (_pos == loopStart) Advance(); // progress guard
            }
            if (Check("}")) Advance();
        }

        var constructors = methods.Where(m => m.Name == "new").ToList();
        var regularMethods = methods.Where(m => m.Name != "new").ToList();

        if (typeName == "") typeName = "?";
        string displayName = baseTypes.Count > 0 ? $"{typeName} (impl {baseTypes[0]})" : $"{typeName} (impl)";
        return new TypeDeclaration(displayName, TypeKind.Class, Modifier.Public, baseTypes, [], constructors, regularMethods, [], [], line);
    }

    /// <summary>
    /// Consumes a type or trait reference in `impl` position and returns a single display name.
    /// Handles reference/pointer prefixes, dyn/impl, slices/arrays, tuples, qualified paths
    /// (keeping the last segment), and generic arguments.
    /// </summary>
    private string ConsumeImplTypeName()
    {
        if (Check("!")) Advance(); // negative impl: `impl !Send for T`
        while (Check("&") || Check("*"))
        {
            Advance();
            if (!IsAtEnd() && Current().Kind == RustTokenKind.Lifetime) Advance();
            MatchKeyword("mut"); MatchKeyword("const");
        }
        MatchKeyword("dyn"); MatchKeyword("impl");

        if (Check("[") || Check("("))
        {
            bool bracket = Check("[");
            int save = _pos;
            Advance(); // skip [ or (
            string inner = ConsumeIdentifier();
            _pos = save;
            if (bracket) SkipBrackets(); else SkipParens();
            SkipGenerics();
            return inner != "" ? inner : (bracket ? "slice" : "tuple");
        }

        string name = ConsumeIdentifier();
        while (Check("::"))
        {
            Advance();
            string seg = ConsumeIdentifier();
            if (seg != "") name = seg; // keep the last path segment as the type/trait name
        }
        SkipGenerics();
        return name;
    }

    private (MethodDeclaration?, string?) ParseFn(List<StatementInfo> statements)
    {
        bool hasDoc = HasPrecedingDocComment();
        var modifiers = Modifier.Private;

        // Consume fn modifiers in any order: async, const, unsafe, default, extern "C".
        while (true)
        {
            if (MatchKeyword("async")) { modifiers |= Modifier.Async; continue; }
            if (MatchKeyword("const") || MatchKeyword("unsafe") || MatchKeyword("default")) continue;
            if (MatchKeyword("extern")) { if (Check("\"")) Advance(); continue; }
            break;
        }

        if (!MatchKeyword("fn")) return (null, null);

        int line = PreviousLine();
        string name = ConsumeIdentifier();
        SkipGenerics();

        // Parameters
        var parameters = new List<ParameterDeclaration>();
        if (Check("("))
        {
            Advance();
            while (!IsAtEnd() && !Check(")"))
            {
                int loopStart = _pos;
                SkipCommentsAndAttributes();
                if (Check(")")) break;

                // self receiver: self, &self, &mut self, self: Box<Self>
                if (CheckKeyword("self") || Check("&"))
                {
                    SkipUntilCommaOrParen();
                    if (Check(",")) Advance();
                    if (_pos == loopStart) Advance();
                    continue;
                }

                // Destructuring patterns bound as a parameter: (a, b): (T1, T2), [a, b]: [T; 2]
                if (Check("(") || Check("["))
                {
                    if (Check("(")) SkipParens(); else SkipBrackets();
                    if (Check(":")) { Advance(); ConsumeType(); }
                    if (Check(",")) Advance();
                    if (_pos == loopStart) Advance();
                    continue;
                }

                while (MatchKeyword("mut") || MatchKeyword("ref")) { }
                string paramName = ConsumeIdentifier();
                if (Check(":"))
                {
                    Advance();
                    string paramType = ConsumeType();
                    if (paramName != "" && paramName != "_")
                        parameters.Add(new ParameterDeclaration(paramName,
                            new TypeReference(paramType, null, [], paramType), false, false, false, 0));
                }
                if (Check(",")) Advance();

                // Guaranteed forward progress: never spin on an unexpected token.
                if (_pos == loopStart) Advance();
            }
            if (Check(")")) Advance();
        }

        // Return type
        TypeReference? returnType = null;
        if (Check("->"))
        {
            Advance();
            string retType = ConsumeType();
            if (retType != "")
                returnType = new TypeReference(retType, null, [], retType);
        }

        if (CheckKeyword("where")) SkipWhereClause();

        // Body
        var methodStatements = new List<StatementInfo>();
        if (Check("{"))
        {
            ParseBlock(methodStatements);
        }
        else if (Check(";")) Advance();

        statements.AddRange(methodStatements);
        return (new MethodDeclaration(name, modifiers, [], returnType, parameters, line)
        { Statements = methodStatements, HasDocComment = hasDoc }, null);
    }

    private void ParseBlock(List<StatementInfo> statements)
    {
        if (!Check("{")) return;
        Advance(); // skip {
        int depth = 1;
        while (!IsAtEnd() && depth > 0)
        {
            if (Check("{")) { depth++; Advance(); continue; }
            if (Check("}")) { depth--; if (depth == 0) { Advance(); break; } Advance(); continue; }

            // Detect panic!/todo!/unimplemented!
            if (Current().Kind == RustTokenKind.Identifier &&
                Current().Value is "panic" or "todo" or "unimplemented" or "unreachable" &&
                Peek().Value == "!")
            {
                string macroName = Current().Value;
                int stmtLine = CurrentLine();
                statements.Add(new StatementInfo("throw", [], null, macroName, [], stmtLine, true));
                Advance(); Advance(); // skip name and !
                // Do NOT skip the macro args — let the scanner descend so calls like
                // panic!("{}", x.unwrap()) still surface the inner unwrap.
                continue;
            }

            // Detect function/method calls: ident( or ident::ident( or expr.ident(
            if (Current().Kind == RustTokenKind.Identifier)
            {
                int stmtLine = CurrentLine();
                string typeName = "";
                string memberName = Current().Value;
                Advance();

                // path::ident
                while (Check("::"))
                {
                    Advance();
                    if (Current().Kind == RustTokenKind.Identifier)
                    {
                        typeName = typeName == "" ? memberName : typeName + "::" + memberName;
                        memberName = Current().Value;
                        Advance();
                    }
                }

                // .method calls
                if (Check(".") && Peek().Kind == RustTokenKind.Identifier)
                {
                    typeName = memberName;
                    Advance(); // skip .
                    memberName = Current().Value;
                    Advance();
                }

                if (Check("(") && memberName is not "if" and not "for" and not "while" and not "loop" and not "match")
                {
                    statements.Add(new StatementInfo("call", [],
                        typeName != "" ? typeName : null, memberName, [], stmtLine, true));
                    // Do NOT skip the argument list — keep scanning so calls nested in
                    // arguments/closures (e.g. log(x.unwrap())) are captured too.
                }
                // Macro calls: ident!(...)
                else if (Check("!"))
                {
                    Advance();
                    string macroCall = memberName + "!";
                    // Emit the macro call but keep scanning its delimited body for nested calls.
                    statements.Add(new StatementInfo("call", [], null, macroCall, [], stmtLine, true));
                }
                continue;
            }

            Advance();
        }
    }

    // Helper: parse a use statement
    private void ParseUse(List<string> usings)
    {
        var parts = new List<string>();
        while (!IsAtEnd() && !Check(";"))
        {
            if (Check("{"))
            {
                string basePath = string.Join("::", parts);
                Advance();
                while (!IsAtEnd() && !Check("}"))
                {
                    if (Current().Kind == RustTokenKind.Identifier)
                    {
                        usings.Add(basePath == "" ? Current().Value : basePath + "::" + Current().Value);
                    }
                    Advance();
                }
                if (Check("}")) Advance();
                parts.Clear();
                break;
            }
            if (Current().Kind == RustTokenKind.Identifier || Current().Kind == RustTokenKind.Keyword)
                parts.Add(Current().Value);
            Advance();
        }
        if (parts.Count > 0) usings.Add(string.Join("::", parts));
        if (Check(";")) Advance();
    }

    #region Token helpers

    private RustToken Current() => _pos < tokens.Count ? tokens[_pos] : tokens[^1];
    private RustToken Peek() => _pos + 1 < tokens.Count ? tokens[_pos + 1] : tokens[^1];
    private int CurrentLine() => Current().Line;
    private int PreviousLine() => _pos > 0 ? tokens[_pos - 1].Line : 1;
    private bool IsAtEnd() => _pos >= tokens.Count || Current().Kind == RustTokenKind.Eof;
    private void Advance() { if (!IsAtEnd()) _pos++; }

    private bool Check(string value) => !IsAtEnd() && Current().Value == value;
    private bool CheckKeyword(string kw) => !IsAtEnd() && Current().Kind == RustTokenKind.Keyword && Current().Value == kw;
    private bool CheckIdent(string id) => !IsAtEnd() && Current().Kind == RustTokenKind.Identifier && Current().Value == id;
    private bool MatchKeyword(string kw) { if (CheckKeyword(kw)) { Advance(); return true; } return false; }

    // True if the item at the current position is preceded by a #[cfg(test)] attribute.
    private bool PrecededByCfgTest()
    {
        for (int i = _pos - 1; i >= 0 && i >= _pos - 8; i--)
        {
            var t = tokens[i];
            if (t.Kind == RustTokenKind.Attribute)
            {
                var v = t.Value.Replace(" ", "");
                if (v.Contains("cfg(test)") || v.Contains("cfg(all(test") || v.Contains(",test)") || v.Contains("(test,")) return true;
            }
            else if (t.Kind is not RustTokenKind.DocComment and not RustTokenKind.LineComment and not RustTokenKind.BlockComment)
            {
                break;
            }
        }
        return false;
    }

    // Skips `mod name { ... }` or `mod name;`.
    private void SkipModuleBody()
    {
        Advance();           // mod
        ConsumeIdentifier(); // name
        if (Check("{")) SkipBraces();
        else if (Check(";")) Advance();
    }

    private string ConsumeIdentifier()
    {
        if (!IsAtEnd() && Current().Kind == RustTokenKind.Identifier)
        { var v = Current().Value; Advance(); return v; }
        if (!IsAtEnd() && Current().Kind == RustTokenKind.Keyword && Current().Value == "Self")
        { Advance(); return "Self"; }
        return "";
    }

    private string ConsumeType()
    {
        int start = _pos;
        int angle = 0, paren = 0, bracket = 0;
        while (!IsAtEnd())
        {
            if (Check("<")) { angle++; Advance(); continue; }
            if (Check(">")) { if (angle == 0) break; angle--; Advance(); continue; }
            if (Check("(")) { paren++; Advance(); continue; }
            if (Check(")")) { if (paren == 0) break; paren--; Advance(); continue; }
            if (Check("[")) { bracket++; Advance(); continue; }
            if (Check("]")) { if (bracket == 0) break; bracket--; Advance(); continue; }
            if (angle == 0 && paren == 0 && bracket == 0
                && (Check(",") || Check(";") || Check("{") || Check("}") || CheckKeyword("where"))) break;
            Advance();
        }
        if (_pos == start) return "";
        return string.Join("", tokens[start.._pos].Select(t => t.Value));
    }

    private void SkipGenerics()
    {
        if (!Check("<")) return;
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("<")) { depth++; Advance(); }
            else if (Check(">")) { depth--; Advance(); if (depth == 0) break; }
            else Advance();
        }
    }

    private void SkipParens()
    {
        if (!Check("(")) return;
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("(")) { depth++; Advance(); }
            else if (Check(")")) { depth--; Advance(); if (depth == 0) break; }
            else Advance();
        }
    }

    private void SkipBraces()
    {
        if (!Check("{")) return;
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("{")) { depth++; Advance(); }
            else if (Check("}")) { depth--; Advance(); if (depth == 0) break; }
            else Advance();
        }
    }

    private void SkipBrackets()
    {
        if (!Check("[")) return;
        int depth = 0;
        while (!IsAtEnd())
        {
            if (Check("[")) { depth++; Advance(); }
            else if (Check("]")) { depth--; Advance(); if (depth == 0) break; }
            else Advance();
        }
    }

    private void SkipUntil(string value)
    {
        while (!IsAtEnd() && !Check(value)) Advance();
    }

    private void SkipUntilCommaOrBrace()
    {
        while (!IsAtEnd() && !Check(",") && !Check("}")) Advance();
        if (Check(",")) Advance();
    }

    private void SkipUntilCommaOrParen()
    {
        while (!IsAtEnd() && !Check(",") && !Check(")")) Advance();
    }

    private void SkipCommentsAndAttributes()
    {
        while (!IsAtEnd() && Current().Kind is RustTokenKind.LineComment or RustTokenKind.DocComment or RustTokenKind.BlockComment or RustTokenKind.Attribute)
            Advance();
    }

    private bool IsFnAhead()
    {
        for (int i = _pos; i < Math.Min(_pos + 5, tokens.Count); i++)
            if (tokens[i].Kind == RustTokenKind.Keyword && tokens[i].Value == "fn") return true;
        return false;
    }

    private bool HasPrecedingDocComment()
    {
        for (int i = _pos - 1; i >= 0 && i >= _pos - 10; i--)
        {
            if (tokens[i].Kind == RustTokenKind.DocComment) return true;
            if (tokens[i].Kind is RustTokenKind.Attribute or RustTokenKind.LineComment or RustTokenKind.BlockComment) continue;
            // Skip visibility/modifier keywords consumed by the caller (e.g., pub, async, unsafe)
            if (tokens[i].Kind == RustTokenKind.Keyword && tokens[i].Value is "pub" or "async" or "unsafe") continue;
            // Skip pub(crate) parenthesized visibility
            if (tokens[i].Kind == RustTokenKind.Punctuation && tokens[i].Value is ")" or "(" or "crate") continue;
            break;
        }
        return false;
    }

    private List<string> CollectPrecedingAttributes()
    {
        var attrs = new List<string>();
        for (int i = _pos - 1; i >= 0; i--)
        {
            if (tokens[i].Kind == RustTokenKind.Attribute)
                attrs.Insert(0, tokens[i].Value.Trim('#', '[', ']'));
            else if (tokens[i].Kind is RustTokenKind.DocComment or RustTokenKind.LineComment or RustTokenKind.BlockComment)
                continue;
            else break;
        }
        return attrs;
    }

    private HashSet<int> ExtractCommentLines()
    {
        var lines = new HashSet<int>();
        foreach (var t in tokens)
            if (t.Kind is RustTokenKind.LineComment or RustTokenKind.DocComment or RustTokenKind.BlockComment)
                lines.Add(t.Line);
        return lines;
    }

    #endregion
}

#endregion
