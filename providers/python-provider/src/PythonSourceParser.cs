using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>
/// Python source parser built on a real lexer (<see cref="PythonLexer"/>) and recursive-descent
/// parser. Produces the exact same <see cref="SourceFile"/> model as the previous line-scanner
/// while correctly handling all Python string forms, INDENT/DEDENT block structure, and
/// reporting genuine syntax errors into <see cref="SourceFile.ParseErrors"/>.
/// </summary>
public class PythonSourceParser : ISourceParser
{
    public override IReadOnlyList<string> Extensions => [".py"];
    public override string Language => "python";

    public override SourceFile? Parse(string filePath, string sourceText)
    {
        var lexer = new PythonLexer(sourceText, filePath);
        var tokens = lexer.Tokenize();
        return new PythonParserCore(filePath, tokens, lexer.CommentLines, lexer.LexErrors)
            .ParseModule(sourceText);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Internal recursive-descent parser
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class PythonParserCore
{
    private readonly string _filePath;
    private readonly List<Tok> _toks;
    private int _pos;

    private readonly List<TypeDeclaration> _types      = [];
    private readonly List<StatementInfo>   _statements = [];
    private readonly List<string>          _usings     = [];
    private readonly List<string>          _errors     = [];
    private readonly HashSet<int>          _commentLines;

    internal PythonParserCore(string filePath, List<Tok> toks,
        HashSet<int> commentLines, List<string> lexErrors)
    {
        _filePath     = filePath;
        _toks         = toks;
        _commentLines = commentLines;
        _errors.AddRange(lexErrors);
    }

    // ── Token navigation ────────────────────────────────────────────────────

    private Tok Peek(int offset = 0)
    {
        int idx = _pos + offset;
        return idx < _toks.Count ? _toks[idx] : new Tok(TK.Eof, "", 0, 0);
    }

    private Tok Consume()
    {
        var t = Peek();
        if (_pos < _toks.Count) _pos++;
        return t;
    }

    private bool At(TK k)         => Peek().Kind == k;
    private bool AtIdent(string s) => Peek().Is(s);

    private void SkipNewlines()
    {
        while (At(TK.Newline)) Consume();
    }

    private void SkipStructural()
    {
        while (At(TK.Newline) || At(TK.Indent) || At(TK.Dedent)) Consume();
    }

    private void SkipToNewline()
    {
        while (!At(TK.Newline) && !At(TK.Indent) && !At(TK.Dedent) && !At(TK.Eof))
            Consume();
        if (At(TK.Newline)) Consume();
    }

    private void SkipBlock()
    {
        if (!At(TK.Indent)) return;
        Consume();
        int depth = 1;
        while (!At(TK.Eof) && depth > 0)
        {
            if (At(TK.Indent)) depth++;
            else if (At(TK.Dedent)) depth--;
            Consume();
        }
    }

    private void AddError(int line, int col, string msg) =>
        _errors.Add($"{_filePath}({line},{col}): error: {msg}");

    // ── Module-level parse ──────────────────────────────────────────────────

    internal SourceFile ParseModule(string sourceText)
    {
        var decorators = new List<string>();

        while (!At(TK.Eof))
        {
            SkipStructural();
            if (At(TK.Eof)) break;

            var tok = Peek();

            // Decorator
            if (tok.Kind == TK.At)
            {
                decorators.Add(ParseDecoratorName());
                SkipToNewline();
                continue;
            }

            // Class declaration
            if (tok.Is("class"))
            {
                var type = ParseClassDecl(decorators);
                if (type != null) _types.Add(type);
                decorators = [];
                continue;
            }

            // Function declaration (sync)
            if (tok.Is("def"))
            {
                ParseTopLevelFunc(decorators, isAsync: false);
                decorators = [];
                continue;
            }

            // async def (top-level function)
            if (tok.Is("async") && Peek(1).Is("def"))
            {
                Consume(); // "async"
                ParseTopLevelFunc(decorators, isAsync: true);
                decorators = [];
                continue;
            }

            // Import statements
            if (tok.Is("import")) { ParseImport();     decorators = []; continue; }
            if (tok.Is("from"))   { ParseFromImport(); decorators = []; continue; }

            // All other module-level statements
            var lineToks = CollectLineTokens();
            AnalyzeBodyLine(lineToks, isInMethod: false, _statements);
            decorators = [];
        }

        return new SourceFile(_filePath, "python", _types, _statements, sourceText)
        {
            Usings       = _usings,
            Regions      = ExtractRegions(sourceText),
            CommentLines = _commentLines,
            ParseErrors  = _errors,
        };
    }

    // ── Import parsing ──────────────────────────────────────────────────────

    private void ParseImport()
    {
        Consume(); // "import"
        var lineToks = CollectLineTokens();

        int i = 0;
        while (i < lineToks.Count)
        {
            // Collect dotted module name tokens (Names and Dots), stop at "as"/Comma
            var parts = new List<string>();
            while (i < lineToks.Count
                   && !lineToks[i].Is("as")
                   && lineToks[i].Kind != TK.Comma)
            {
                if (lineToks[i].Kind == TK.Name) parts.Add(lineToks[i].Text);
                i++;
            }

            if (parts.Count > 0) _usings.Add(string.Join(".", parts));

            // Skip "as alias"
            if (i < lineToks.Count && lineToks[i].Is("as"))
            {
                i++; // "as"
                if (i < lineToks.Count) i++; // alias name
            }

            // Skip comma separator
            if (i < lineToks.Count && lineToks[i].Kind == TK.Comma) i++;
        }
    }

    private void ParseFromImport()
    {
        Consume(); // "from"
        var lineToks = CollectLineTokens();

        // Collect module name (Names + Dots) until "import"
        var parts = new List<string>();
        int i = 0;
        while (i < lineToks.Count && !lineToks[i].Is("import"))
        {
            if (lineToks[i].Kind == TK.Name) parts.Add(lineToks[i].Text);
            i++;
        }

        // Only register if "import" keyword was found
        if (i < lineToks.Count && lineToks[i].Is("import") && parts.Count > 0)
            _usings.Add(string.Join(".", parts));
    }

    // ── Class declaration ───────────────────────────────────────────────────

    private TypeDeclaration? ParseClassDecl(List<string> decorators)
    {
        var classTok = Consume(); // "class"

        if (!At(TK.Name))
        {
            AddError(classTok.Line, classTok.Col, "expected class name after 'class'");
            SkipToNewline();
            if (At(TK.Indent)) SkipBlock();
            return null;
        }
        string className = Consume().Text;

        // Optional base types: (Base1, Base2, ...)
        var baseTypes = new List<string>();
        if (At(TK.LParen))
        {
            Consume();
            baseTypes = ParseBaseTypes(CollectUntilBalanced(TK.RParen));
            if (At(TK.RParen)) Consume(); else AddError(classTok.Line, classTok.Col, $"unclosed '(' in class '{className}'");
        }

        if (!At(TK.Colon))
        {
            AddError(classTok.Line, classTok.Col, $"expected ':' after class '{className}'");
            SkipToNewline();
            if (At(TK.Indent)) SkipBlock();
            return null;
        }
        Consume(); // ':'
        SkipToNewline();
        SkipNewlines();

        if (!At(TK.Indent))
        {
            AddError(classTok.Line, classTok.Col, $"expected indented block for class '{className}'");
            return BuildClassDecl(className, classTok.Line, baseTypes, decorators, [], [], false, false);
        }
        Consume(); // INDENT

        var methods      = new List<MethodDeclaration>();
        var constructors = new List<MethodDeclaration>();
        bool hasDocstring = false;
        bool hasSlots     = false;
        bool firstItem    = true;
        var  innerDecs    = new List<string>();

        while (!At(TK.Dedent) && !At(TK.Eof))
        {
            SkipNewlines();
            if (At(TK.Dedent) || At(TK.Eof)) break;

            var tok = Peek();

            // Decorator
            if (tok.Kind == TK.At)
            {
                innerDecs.Add(ParseDecoratorName());
                SkipToNewline();
                continue;
            }

            // Docstring (first non-blank item in class body that is a string literal)
            if (firstItem && tok.Kind == TK.Str)
            {
                hasDocstring = true;
                Consume();
                SkipToNewline();
                firstItem = false;
                innerDecs = [];
                continue;
            }

            firstItem = false;

            // Method definition
            if (tok.Is("def") || (tok.Is("async") && Peek(1).Is("def")))
            {
                bool isAsync = tok.Is("async");
                if (isAsync) Consume(); // "async"

                var m = ParseMethodDecl(innerDecs, isAsync);
                if (m != null)
                {
                    (m.Name == "__init__" ? constructors : methods).Add(m);
                    _statements.AddRange(m.Statements);
                }
                innerDecs = [];
                continue;
            }

            // Nested class → skip its body
            if (tok.Is("class"))
            {
                innerDecs = [];
                SkipToNewline();
                if (At(TK.Indent)) SkipBlock();
                continue;
            }

            // __slots__ detection
            if (tok.Kind == TK.Name && tok.Text == "__slots__") hasSlots = true;

            innerDecs = [];
            SkipToNewline();
        }

        if (At(TK.Dedent)) Consume();

        return BuildClassDecl(className, classTok.Line, baseTypes, decorators,
            constructors, methods, hasDocstring, hasSlots);
    }

    private TypeDeclaration BuildClassDecl(string name, int line, List<string> baseTypes,
        List<string> decorators, List<MethodDeclaration> constructors,
        List<MethodDeclaration> methods, bool hasDocstring, bool hasSlots)
        => new TypeDeclaration(name, TypeKind.Class, Modifier.Public,
                baseTypes, decorators, constructors, methods, [], [], line)
            { HasDocComment = hasDocstring }
            .AsPython(
                isDataclass:  decorators.Exists(d => d.Contains("dataclass")),
                isEnum:       baseTypes.Exists(b => b is "Enum" or "IntEnum" or "StrEnum" or "Flag" or "IntFlag"),
                isAbstract:   baseTypes.Exists(b => b == "ABC" || b == "ABCMeta" || b.EndsWith(".ABC")),
                isNamedTuple: baseTypes.Exists(b => b == "NamedTuple" || b.EndsWith(".NamedTuple")),
                isProtocol:   baseTypes.Exists(b => b == "Protocol" || b.EndsWith(".Protocol")),
                isException:  baseTypes.Exists(b => b.EndsWith("Exception") || b.EndsWith("Error") || b == "BaseException"),
                hasSlots:     hasSlots);

    // ── Function / method declaration ───────────────────────────────────────

    private void ParseTopLevelFunc(List<string> decorators, bool isAsync)
    {
        var m = ParseMethodDecl(decorators, isAsync);
        if (m != null) _statements.AddRange(m.Statements);
    }

    private MethodDeclaration? ParseMethodDecl(List<string> decorators, bool isAsync)
    {
        var defTok = Consume(); // "def"

        if (!At(TK.Name))
        {
            AddError(defTok.Line, defTok.Col, "expected function name after 'def'");
            SkipToNewline();
            if (At(TK.Indent)) SkipBlock();
            return null;
        }
        string methodName = Consume().Text;

        if (!At(TK.LParen))
        {
            AddError(defTok.Line, defTok.Col, $"expected '(' after function name '{methodName}'");
            SkipToNewline();
            if (At(TK.Indent)) SkipBlock();
            return null;
        }
        Consume(); // '('
        var paramToks = CollectUntilBalanced(TK.RParen);
        if (At(TK.RParen)) Consume();
        else AddError(defTok.Line, defTok.Col, $"unclosed '(' in function '{methodName}'");

        // Optional return annotation
        string? returnType = null;
        if (At(TK.Arrow)) { Consume(); returnType = CollectReturnType(); }

        if (!At(TK.Colon))
            AddError(defTok.Line, defTok.Col, $"expected ':' at end of function definition '{methodName}'");
        else
            Consume();

        SkipToNewline();
        SkipNewlines();

        // Check for docstring (first non-newline token in body is a Str)
        bool hasDocstring = false;
        if (At(TK.Indent))
        {
            int la = _pos + 1; // past INDENT
            while (la < _toks.Count && _toks[la].Kind == TK.Newline) la++;
            if (la < _toks.Count && _toks[la].Kind == TK.Str) hasDocstring = true;
        }

        // Build modifiers
        var modifiers = Modifier.None;
        if (isAsync)                               modifiers |= Modifier.Async;
        if (decorators.Contains("staticmethod"))   modifiers |= Modifier.Static;
        if (decorators.Contains("abstractmethod")) modifiers |= Modifier.Abstract;
        if (!methodName.StartsWith('_'))           modifiers |= Modifier.Public;
        else                                       modifiers |= Modifier.Private;

        var parameters   = ParseParameters(paramToks);
        var retRef       = returnType is not null ? new TypeReference(returnType, null, [], returnType) : null;
        var bodyStatements = new List<StatementInfo>();

        if (At(TK.Indent)) ParseFlatBody(isInMethod: true, bodyStatements);

        return new MethodDeclaration(methodName, modifiers, decorators, retRef, parameters, defTok.Line)
        {
            Statements   = bodyStatements,
            HasDocComment = hasDocstring,
        }
        .AsPython(isGenerator: bodyStatements.Exists(s => s.Kind == "yield"));
    }

    // ── Flat body scanner ───────────────────────────────────────────────────
    // Processes ALL tokens inside a block (at any nesting depth) to extract
    // statements, matching the original line-scanner's flat enumeration.

    private void ParseFlatBody(bool isInMethod, List<StatementInfo> outStatements)
    {
        if (!At(TK.Indent)) return;
        Consume(); // initial INDENT

        int depth = 1;

        while (!At(TK.Eof) && depth > 0)
        {
            var tok = Peek();

            if (tok.Kind == TK.Indent)  { depth++;                            Consume(); continue; }
            if (tok.Kind == TK.Dedent)  { depth--; if (depth > 0) Consume(); continue; }
            if (tok.Kind == TK.Newline || tok.Kind == TK.Semi) { Consume();  continue; }

            // except clauses are handled specially (need look-ahead for HasRethrow)
            if (tok.Is("except"))
            {
                ParseExceptLine(isInMethod, outStatements);
                continue;
            }

            var lineToks = CollectLineTokens();
            AnalyzeBodyLine(lineToks, isInMethod, outStatements);
        }

        if (At(TK.Dedent)) Consume();
    }

    // ── Except clause ───────────────────────────────────────────────────────

    private void ParseExceptLine(bool isInMethod, List<StatementInfo> outStatements)
    {
        var exceptTok = Consume(); // "except"

        string? caughtType  = null;
        bool    isGeneric   = true;   // bare except or Exception/BaseException

        if (At(TK.Colon))
        {
            // bare except:
            Consume();
        }
        else if (At(TK.Star))
        {
            // except* (Python 3.11+ exception groups) — consume star + type
            Consume();
            if (At(TK.Name)) { caughtType = Consume().Text; isGeneric = false; }
            if (At(TK.Name) && AtIdent("as")) { Consume(); if (At(TK.Name)) Consume(); }
            if (At(TK.Colon)) Consume();
            else AddError(exceptTok.Line, exceptTok.Col, "expected ':' after except* clause");
        }
        else if (At(TK.LParen))
        {
            // Tuple form: except (ValueError, TypeError):
            Consume();
            var inner = CollectUntilBalanced(TK.RParen);
            if (At(TK.RParen)) Consume();
            int fi = inner.FindIndex(t => t.Kind == TK.Name);
            caughtType = fi >= 0 ? inner[fi].Text : null;
            isGeneric  = caughtType is null or "Exception" or "BaseException";
            if (At(TK.Name) && AtIdent("as")) { Consume(); if (At(TK.Name)) Consume(); }
            if (At(TK.Colon)) Consume();
            else AddError(exceptTok.Line, exceptTok.Col, "expected ':' after except clause");
        }
        else if (At(TK.Name))
        {
            // Single type: except ValueError: or except ValueError as e:
            caughtType = Consume().Text;
            // Dotted name: except os.error:
            while (At(TK.Dot) && Peek(1).Kind == TK.Name)
            {
                caughtType += "." + Peek(1).Text;
                Consume(); Consume();
            }
            isGeneric = caughtType is "Exception" or "BaseException";
            // Optional "as alias"
            if (AtIdent("as")) { Consume(); if (At(TK.Name)) Consume(); }
            if (At(TK.Colon)) Consume();
            else AddError(exceptTok.Line, exceptTok.Col, "expected ':' after except clause");
        }

        // Consume trailing newlines before the except body block
        while (At(TK.Newline)) Consume();

        // Look ahead (without consuming) to detect a bare re-raise in the body
        bool hasRethrow = HasBareRaiseInNextBlock();

        outStatements.Add(new PythonStatementInfo(
            "catch", [], caughtType, null, [], exceptTok.Line, isInMethod)
        {
            IsErrorHandler         = true,
            IsGenericErrorHandler  = isGeneric,
            HasRethrow             = hasRethrow,
        });
    }

    /// <summary>
    /// Non-consuming look-ahead: scans the upcoming INDENT...DEDENT block to find
    /// a bare <c>raise</c> statement (i.e., <c>raise</c> followed by NEWLINE or end-of-block).
    /// </summary>
    private bool HasBareRaiseInNextBlock()
    {
        int i = _pos;
        // Skip any stray newlines before INDENT
        while (i < _toks.Count && _toks[i].Kind == TK.Newline) i++;
        if (i >= _toks.Count || _toks[i].Kind != TK.Indent) return false;
        i++; // past INDENT

        int  depth     = 1;
        bool lineStart = true;

        while (i < _toks.Count && depth > 0)
        {
            var t = _toks[i];
            switch (t.Kind)
            {
                case TK.Indent:  depth++; i++; lineStart = true;  continue;
                case TK.Dedent:  depth--; i++; lineStart = true;  continue;
                case TK.Newline: i++;          lineStart = true;  continue;
                case TK.Eof:     goto Done;
            }

            if (lineStart && t.Is("raise"))
            {
                // Bare raise = Name("raise") immediately followed by Newline / Dedent / EOF
                int j = i + 1;
                while (j < _toks.Count && _toks[j].Kind == TK.Newline) j++;
                if (j >= _toks.Count) return true;
                if (_toks[j].Kind is TK.Dedent or TK.Eof) return true;
                // raise <something> → not bare
            }

            lineStart = false;
            i++;
        }
        Done:
        return false;
    }

    // ── Line collection & analysis ──────────────────────────────────────────

    /// <summary>Consume tokens up to (and including) the next NEWLINE or SEMI.</summary>
    private List<Tok> CollectLineTokens()
    {
        var result = new List<Tok>();
        while (!At(TK.Newline) && !At(TK.Indent) && !At(TK.Dedent) && !At(TK.Semi) && !At(TK.Eof))
            result.Add(Consume());
        if (At(TK.Newline) || At(TK.Semi)) Consume();
        return result;
    }

    /// <summary>
    /// Analyse tokens on one logical line and emit the appropriate statement(s),
    /// replicating the semantics of the original ExtractLineStatement / ExtractBodyStatements.
    /// </summary>
    private void AnalyzeBodyLine(List<Tok> lt, bool isInMethod, List<StatementInfo> out_)
    {
        if (lt.Count == 0) return;
        int lineNum = lt[0].Line;
        var first   = lt[0];

        // yield anywhere on the line → yield statement (return early, matches original)
        if (lt.Any(t => t.Is("yield")))
        {
            out_.Add(new PythonStatementInfo("yield", [], null, null, [], lineNum, isInMethod));
            return;
        }

        // Comprehension: 'for' inside brackets (can co-exist with a call below)
        if (HasForInBrackets(lt))
            out_.Add(new PythonStatementInfo("comprehension", [], null, null, [], lineNum, isInMethod));

        // async with
        if (first.Is("async") && lt.Count > 1 && lt[1].Is("with"))
        {
            out_.Add(new PythonStatementInfo("async with", [], null, null, [], lineNum, isInMethod));
            return;
        }

        if (first.Is("with"))     { out_.Add(new PythonStatementInfo("with",     [], null, null, [], lineNum, isInMethod)); return; }
        if (first.Is("assert"))   { out_.Add(new PythonStatementInfo("assert",   [], null, null, [], lineNum, isInMethod)); return; }
        if (first.Is("global"))   { out_.Add(new PythonStatementInfo("global",   [], null, null, [], lineNum, isInMethod)); return; }
        if (first.Is("nonlocal")) { out_.Add(new PythonStatementInfo("nonlocal", [], null, null, [], lineNum, isInMethod)); return; }

        // raise
        if (first.Is("raise"))
        {
            string? typeName = lt.Count > 1 && lt[1].Kind == TK.Name ? lt[1].Text : null;
            out_.Add(new PythonStatementInfo("throw", [], typeName, null, [], lineNum, isInMethod));
            return;
        }

        // Call detection (at start of line, optionally preceded by await)
        var (ok, tn, mn) = DetectCall(lt);
        if (ok) out_.Add(new PythonStatementInfo("call", [], tn, mn, [], lineNum, isInMethod));
    }

    // ── Call and comprehension detection ────────────────────────────────────

    private static (bool Ok, string? TypeName, string? MemberName) DetectCall(List<Tok> lt)
    {
        if (lt.Count == 0) return (false, null, null);
        int i = 0;

        // Optional "await"
        if (lt[i].Is("await") && lt.Count > 1) i++;

        // Must start with a Name token
        if (i >= lt.Count || lt[i].Kind != TK.Name) return (false, null, null);

        // Collect dotted path: Name (.Name)*
        var parts = new List<string> { lt[i].Text };
        i++;
        while (i + 1 < lt.Count && lt[i].Kind == TK.Dot && lt[i + 1].Kind == TK.Name)
        {
            parts.Add(lt[i + 1].Text);
            i += 2;
        }

        // Must be followed by '('
        if (i >= lt.Count || lt[i].Kind != TK.LParen) return (false, null, null);

        string member = parts[^1];

        // Exclude control-flow keywords
        if (member is "if" or "for" or "while" or "with" or "elif" or "def" or "class"
                   or "return" or "assert" or "del" or "except" or "raise"
                   or "yield" or "import" or "from")
            return (false, null, null);

        string? typeName = parts.Count > 1 ? string.Join(".", parts[..^1]) : null;
        return (true, typeName, member);
    }

    private static bool HasForInBrackets(List<Tok> lt)
    {
        int depth = 0;
        foreach (var t in lt)
        {
            if (t.Kind is TK.LParen or TK.LBrack or TK.LBrace) depth++;
            else if (t.Kind is TK.RParen or TK.RBrack or TK.RBrace) depth--;
            else if (depth > 0 && t.Is("for")) return true;
        }
        return false;
    }

    // ── Decorator / parameter / type helpers ────────────────────────────────

    private string ParseDecoratorName()
    {
        Consume(); // '@'
        var parts = new List<string>();
        while (At(TK.Name))
        {
            parts.Add(Consume().Text);
            if (At(TK.Dot)) { Consume(); continue; }
            break;
        }
        return string.Join(".", parts);
    }

    /// <summary>Collect tokens until a matching close bracket (without consuming it).</summary>
    private List<Tok> CollectUntilBalanced(TK closeKind)
    {
        var result = new List<Tok>();
        int depth  = 0;
        while (!At(TK.Eof))
        {
            var t = Peek();
            if (t.Kind == closeKind && depth == 0) break;
            if (t.Kind is TK.LParen or TK.LBrack or TK.LBrace) depth++;
            if (t.Kind is TK.RParen or TK.RBrack or TK.RBrace) { if (depth == 0) break; depth--; }
            result.Add(Consume());
        }
        return result;
    }

    private List<string> ParseBaseTypes(List<Tok> tokens)
    {
        var result     = new List<string>();
        var nameParts  = new List<string>();
        int depth      = 0;
        bool inKwarg   = false;

        foreach (var t in tokens)
        {
            if (t.Kind is TK.LParen or TK.LBrack or TK.LBrace) { depth++;   continue; }
            if (t.Kind is TK.RParen or TK.RBrack or TK.RBrace) { depth--;   continue; }
            if (depth > 0) continue;
            if (t.Kind == TK.Eq)    { inKwarg = true; continue; }
            if (inKwarg)             { continue; }
            if (t.Kind == TK.Comma)
            {
                if (nameParts.Count > 0) result.Add(string.Join(".", nameParts));
                nameParts = []; inKwarg = false;
                continue;
            }
            if (t.Kind == TK.Name) nameParts.Add(t.Text);
        }
        if (nameParts.Count > 0 && !inKwarg) result.Add(string.Join(".", nameParts));
        return result;
    }

    private List<ParameterDeclaration> ParseParameters(List<Tok> paramToks)
    {
        var result = new List<ParameterDeclaration>();

        // Split on top-level commas
        var segments = new List<List<Tok>>();
        var cur      = new List<Tok>();
        int depth    = 0;
        foreach (var t in paramToks)
        {
            if (t.Kind is TK.LParen or TK.LBrack or TK.LBrace) { depth++; cur.Add(t); }
            else if (t.Kind is TK.RParen or TK.RBrack or TK.RBrace) { depth--; cur.Add(t); }
            else if (t.Kind == TK.Comma && depth == 0) { segments.Add(cur); cur = []; }
            else cur.Add(t);
        }
        if (cur.Count > 0) segments.Add(cur);

        foreach (var seg in segments)
        {
            if (seg.Count == 0) continue;
            int i        = 0;
            bool isKw    = false;
            bool isVar   = false;

            if (i < seg.Count && seg[i].Kind == TK.StarStar) { isKw  = true; i++; }
            else if (i < seg.Count && seg[i].Kind == TK.Star) { isVar = true; i++; }

            if (i >= seg.Count || seg[i].Kind != TK.Name) continue;
            string name = seg[i].Text; i++;

            if (name is "self" or "cls") continue;

            // Optional type annotation: name: Type = default
            string? typeName = null;
            if (i < seg.Count && seg[i].Kind == TK.Colon)
            {
                i++;
                var tp = new List<string>();
                while (i < seg.Count && seg[i].Kind != TK.Eq)
                {
                    if (seg[i].Kind == TK.Name) tp.Add(seg[i].Text);
                    i++;
                }
                if (tp.Count > 0) typeName = string.Join("", tp);
            }

            var typeRef = typeName is not null ? new TypeReference(typeName, null, [], typeName) : null;
            result.Add(new ParameterDeclaration(name, typeRef, isVar, isKw, false, 0));
        }

        return result;
    }

    /// <summary>Collect tokens for a return type annotation (up to ':' or NEWLINE).</summary>
    private string? CollectReturnType()
    {
        if (!At(TK.Name)) { SkipToNewline(); return null; }
        string rt = Consume().Text;
        int depth = 0;
        while (!At(TK.Eof))
        {
            var t = Peek();
            if (t.Kind == TK.Colon && depth == 0) break;
            if (t.Kind == TK.Newline) break;
            if (t.Kind is TK.LParen or TK.LBrack or TK.LBrace) depth++;
            if (t.Kind is TK.RParen or TK.RBrack or TK.RBrace) depth--;
            Consume();
        }
        return rt;
    }

    // ── Region extraction (same logic as original) ───────────────────────────

    private static List<RegionInfo> ExtractRegions(string sourceText)
    {
        var lines   = sourceText.Split('\n');
        var regions = new List<RegionInfo>();
        var stack   = new Stack<(string Name, int Line)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("# [START"))
            {
                var name = ParseRegionMarker(trimmed, "START");
                if (name is not null) stack.Push((name, i + 1));
            }
            else if (trimmed.StartsWith("# [END") && stack.Count > 0)
            {
                var endName = ParseRegionMarker(trimmed, "END");
                if (endName is not null)
                {
                    var items = new List<(string Name, int Line)>();
                    while (stack.Count > 0)
                    {
                        var top = stack.Pop();
                        if (top.Name == endName)
                        {
                            var contentLines = new List<string>();
                            for (int j = top.Line; j < i && j < lines.Length; j++)
                                contentLines.Add(lines[j].TrimEnd('\r'));
                            regions.Add(new RegionInfo(endName, top.Line, i + 1,
                                string.Join('\n', contentLines)));
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

    private static string? ParseRegionMarker(string text, string marker)
    {
        int i = 0;
        if (i >= text.Length || text[i] != '#') return null;
        i++;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        var prefix = "[" + marker;
        if (i + prefix.Length > text.Length) return null;
        if (!text.AsSpan(i, prefix.Length).SequenceEqual(prefix)) return null;
        i += prefix.Length;
        if (i >= text.Length || !char.IsWhiteSpace(text[i])) return null;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        var end = text.IndexOf(']', i);
        return end > i ? text[i..end] : null;
    }
}