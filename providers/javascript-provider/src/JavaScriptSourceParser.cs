// Projects JavaScript and TypeScript syntax into shared declarations and statements.

using System.Text;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>
/// Parses JavaScript and TypeScript with optional compiler-backed projection.
/// </summary>
public class JavaScriptSourceParser : ISourceParser
{
    private readonly TypeScriptProjection _compiler;

    /// <summary>Creates a parser using the specified compiler discovery path.</summary>
    public JavaScriptSourceParser(string? toolchainSearchPath = null) => _compiler = new(toolchainSearchPath);

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions => [".js", ".ts", ".tsx", ".jsx", ".mjs", ".cjs", ".mts", ".cts"];
    /// <inheritdoc />
    public override string Language => "javascript";
    /// <inheritdoc />
    public override bool CanCache(string filePath) => _compiler.GetCacheIdentity(filePath) is null;

    /// <inheritdoc />
    public override string GetCacheIdentity(string filePath)
    {
        string? identity = _compiler.GetCacheIdentity(filePath);
        if (identity is null)
        {
            WarnFallback();
        }

        return base.GetCacheIdentity(filePath) + ":query-parity-v1:" + (identity ?? "simpler-syntax");
    }

    private static void WarnFallback() => Cop.Core.ProviderWarnings.ReportOnce("javascript", "typescript",
        "Node.js and an installed TypeScript compiler were not both found; using simpler syntax analysis. " +
        "Type inference and complete JS/TS/JSX/TSX syntax facts are unavailable. " +
        "Semantic helpers support simple explicit catch flow, direct process.env accesses and direct resource initializers/transfers; " +
        "complex generic/computed syntax, typed or multiple using declarations and parenthesized resource initializers are not fully analyzed.");

    /// <inheritdoc />
    public override async ValueTask<SourceFile?> ParseAsync(
        string filePath, string sourceText, CancellationToken cancellationToken = default)
    {
        SourceFile? compiled = await _compiler.ParseAsync(filePath, sourceText, cancellationToken)
            .ConfigureAwait(false);
        JsLexer lexer = new JsLexer(sourceText, filePath);
        List<JsToken> allToks = lexer.Tokenize();
        JsParser p = new JsParser(allToks, lexer.Errors, filePath, sourceText);
        if (compiled is not null)
        {
            return compiled with { Regions = p.Regions };
        }

        WarnFallback();
        p.Parse();
        return new SourceFile(filePath, "javascript", p.Types, p.Statements, sourceText)
        {
            Usings = p.Usings,
            Methods = p.Methods,
            Regions = p.Regions,
            CommentLines = p.CommentLines,
            ParseErrors = p.Errors
        };
    }

    private sealed class JsParser
    {
        private readonly List<JsToken> _tok;
        private readonly string _fp;
        private readonly string _src;
        private readonly int[] _lineOffsets;
        private int _idx;

        /// <summary>Gets the type declarations collected from the source.</summary>
        public List<TypeDeclaration> Types { get; } = [];
        /// <summary>Gets the functions and methods collected from the source.</summary>
        public List<MethodDeclaration> Methods { get; } = [];
        /// <summary>Gets the statements collected from the source.</summary>
        public List<StatementInfo> Statements { get; } = [];
        /// <summary>Gets the imported module names found in the source.</summary>
        public List<string> Usings { get; } = [];
        /// <summary>Gets lexical and parser diagnostics for the source.</summary>
        public List<string> Errors { get; }
        /// <summary>Gets line numbers occupied by source comments.</summary>
        public HashSet<int> CommentLines { get; } = [];
        /// <summary>Gets source regions identified during parsing.</summary>
        public List<RegionInfo> Regions { get; } = [];

        /// <summary>
        /// Creates a syntax projector over tokens and existing lexer diagnostics.
        /// </summary>
        public JsParser(List<JsToken> allToks, List<string> lexErrors, string fp, string src)
        {
            _fp = fp; _src = src;
            _lineOffsets = new[] { 0 }.Concat(src.Select((ch, index) => (ch, index))
                .Where(x => x.ch == '\n').Select(x => x.index + 1)).ToArray();
            Errors = new List<string>(lexErrors);
            PreProcess(allToks);
            _tok = allToks.Where(t => !t.IsComment && !t.IsEof).ToList();
        }

        /// <summary>
        /// Collects declarations, statements, imports, and regions from the tokens.
        /// </summary>
        public void Parse()
        {
            while (_idx < _tok.Count)
            {
                ParseTopLevel();
            }

            CheckBracketBalance();
        }
        private void PreProcess(List<JsToken> all)
        {
            string[] lines = _src.Split('\n');
            Stack<(string Name, int Line)> stack = new Stack<(string Name, int Line)>();

            foreach (JsToken t in all)
            {
                if (t.Kind == JsTok.LineComment)
                {
                    CommentLines.Add(t.Line);
                    string txt = t.Value.AsSpan().TrimStart('/').ToString().Trim();
                    if (txt.StartsWith("[START ") && txt.Contains(']'))
                    {
                        int s = txt.IndexOf("[START ") + 7, e = txt.IndexOf(']', s);
                        if (e > s)
                        {
                            stack.Push((txt[s..e], t.Line));
                        }
                    }
                    else if (txt.StartsWith("[END ") && txt.Contains(']') && stack.Count > 0)
                    {
                        int s = txt.IndexOf("[END ") + 5, e = txt.IndexOf(']', s);
                        if (e > s)
                        {
                            string endName = txt[s..e];
                            List<(string, int)> items = new List<(string, int)>();
                            while (stack.Count > 0)
                            {
                                (string Name, int Line) top = stack.Pop();
                                if (top.Name == endName)
                                {
                                    int startL = top.Line, endL = t.Line;
                                    List<string> cl = new List<string>();
                                    for (int j = startL; j < endL - 1 && j < lines.Length; j++)
                                    {
                                        cl.Add(lines[j].TrimEnd('\r'));
                                    }

                                    Regions.Add(new RegionInfo(endName, startL, endL, string.Join('\n', cl)));
                                    for (int k = items.Count - 1; k >= 0; k--)
                                    {
                                        stack.Push(items[k]);
                                    }

                                    break;
                                }
                                items.Add(top);
                            }
                        }
                    }
                }
                else if (t.Kind == JsTok.BlockComment)
                {
                    int startL = t.Line, endL = startL;
                    foreach (char c in t.Value)
                    {
                        if (c == '\n')
                        {
                            endL++;
                        }
                    }

                    for (int l = startL; l <= endL; l++)
                    {
                        CommentLines.Add(l);
                    }
                }
            }
        }

        private void CheckBracketBalance()
        {
            int bd = 0, pd = 0, rd = 0;
            int bl = 0, bc = 0, pl = 0, pc = 0, rl = 0, rc = 0;
            foreach (JsToken t in _tok)
            {
                if (t.IsPunct("{")) { if (bd++ == 0) { bl = t.Line; bc = t.Col; } }
                else if (t.IsPunct("}")) { if (--bd < 0) { Errors.Add($"{_fp}({t.Line},{t.Col}): error: Unexpected '}}'"); bd = 0; } }
                else if (t.IsPunct("(")) { if (pd++ == 0) { pl = t.Line; pc = t.Col; } }
                else if (t.IsPunct(")")) { if (--pd < 0) { Errors.Add($"{_fp}({t.Line},{t.Col}): error: Unexpected ')'"); pd = 0; } }
                else if (t.IsPunct("[")) { if (rd++ == 0) { rl = t.Line; rc = t.Col; } }
                else if (t.IsPunct("]")) { if (--rd < 0) { Errors.Add($"{_fp}({t.Line},{t.Col}): error: Unexpected ']'"); rd = 0; } }
            }
            if (bd > 0)
            {
                Errors.Add($"{_fp}({bl},{bc}): error: Unclosed '{{'");
            }

            if (pd > 0)
            {
                Errors.Add($"{_fp}({pl},{pc}): error: Unclosed '('");
            }

            if (rd > 0)
            {
                Errors.Add($"{_fp}({rl},{rc}): error: Unclosed '['");
            }
        }
        private void ParseTopLevel()
        {
            if (_idx >= _tok.Count)
            {
                return;
            }

            bool isExported = false, isAbstract = false;
            while (_idx < _tok.Count)
            {
                JsToken t = _tok[_idx];
                if (t.IsWord("export")) { isExported = true; _idx++; }
                else if (t.IsWord("default") || t.IsWord("declare")) { _idx++; }
                else if (t.IsWord("abstract")) { isAbstract = true; _idx++; }
                else
                {
                    break;
                }
            }
            if (_idx >= _tok.Count)
            {
                return;
            }

            JsToken tok = _tok[_idx];

            if (tok.IsWord("import") && !isExported) { ParseImport(); return; }
            if (tok.IsWord("class")) { TypeDeclaration? ty = ParseClass(isExported, isAbstract); if (ty != null) { Types.Add(ty); } return; }
            if (tok.IsWord("interface") || tok.IsWord("enum")) { ParseTsType(isExported); return; }
            if (tok.IsWord("type")) { ParseTypeAlias(isExported); return; }

            bool isAsync = false;
            if (tok.IsWord("async"))
            {
                isAsync = true; _idx++;
                if (_idx < _tok.Count)
                {
                    tok = _tok[_idx];
                }
                else
                {
                    return;
                }
            }

            if (tok.IsWord("function")) { ParseFunction(isExported, isAsync); return; }

            if (tok.IsWord("const") || tok.IsWord("let") || tok.IsWord("var"))
            {
                int stmtEnd = FindStatementEnd(_idx);
                ExtractRequireFromRange(_idx, stmtEnd);
                if (!ParseVariableFunction(_idx, stmtEnd, isExported))
                {
                    Statements.AddRange(ParseStatements(_idx, stmtEnd, false));
                }

                _idx = stmtEnd;
                return;
            }

            // Unknown top-level token: skip to next statement boundary; always advance at least 1
            int se = FindStatementEnd(_idx);
            ExtractRequireFromRange(_idx, se);
            Statements.AddRange(ParseStatements(_idx, se, false));
            _idx = se > _idx ? se : _idx + 1;
        }

        private bool ParseVariableFunction(int start, int end, bool isExported)
        {
            if (start + 2 >= end || !_tok[start + 1].IsIdent)
            {
                return false;
            }

            int equals = start + 2;
            while (equals < end && !_tok[equals].IsPunct("="))
            {
                equals++;
            }

            if (equals >= end)
            {
                return false;
            }

            int body = equals + 1;
            bool isAsync = body < end && _tok[body].IsWord("async");
            if (isAsync)
            {
                body++;
            }

            bool isFunction = body < end && _tok[body].IsWord("function");
            bool isGenerator = false;
            if (isFunction)
            {
                body++;
                if (body < end && _tok[body].IsPunct("*")) { isGenerator = true; body++; }
                if (body < end && _tok[body].IsIdent)
                {
                    body++;
                }
            }
            List<string> typeParameters = ParseTypeParametersAt(ref body, end);
            List<ParameterDeclaration> parameters;
            if (body < end && _tok[body].IsPunct("("))
            {
                int close = FindMatchingClose(body);
                parameters = ParseParametersFromTokens(body, close);
                body = close + 1;
            }
            else if (body + 1 < end && _tok[body].IsIdent && _tok[body + 1].IsPunct("=>"))
            {
                parameters = [new(_tok[body].Value, null, false, false, false, _tok[body].Line)];
                body++;
            }
            else
            {
                return false;
            }

            TypeReference? returnType = null;
            if (body < end && _tok[body].IsPunct(":"))
            {
                body++;
                returnType = ParseTypeAnnotationAt(ref body, end);
            }
            if (!isFunction)
            {
                if (body >= end || !_tok[body].IsPunct("=>"))
                {
                    return false;
                }

                body++;
            }
            List<StatementInfo> statements = body < end && _tok[body].IsPunct("{")
                ? ParseBodyStatements(body, FindMatchingBrace(body), true)
                : ParseStatements(body, end, true);
            Modifier modifiers = isExported ? Modifier.Public : Modifier.None;
            if (isAsync)
            {
                modifiers |= Modifier.Async;
            }

            JavaScriptMethodDeclaration method = NewMethod(_tok[start + 1].Value, modifiers, [], returnType, parameters,
                _tok[start + 1].Line, statements, typeParameters).AsJavaScript(isArrow: !isFunction, isGenerator: isGenerator);
            foreach (StatementInfo statement in statements)
            {
                statement.Method = method;
            }

            Methods.Add(method);
            Statements.AddRange(statements);
            return true;
        }

        private void ExtractRequireFromRange(int start, int end)
        {
            for (int i = start; i + 3 < end && i < _tok.Count; i++)
            {
                if (_tok[i].IsWord("require") && _tok[i + 1].IsPunct("(")
                    && _tok[i + 2].IsStr && _tok[i + 3].IsPunct(")"))
                {
                    string m = UnquoteStr(_tok[i + 2].Value);
                    if (!string.IsNullOrEmpty(m)) { Usings.Add(m); return; }
                }
            }
        }

        private void ParseImport()
        {
            _idx++;
            if (_idx >= _tok.Count)
            {
                return;
            }

            if (_tok[_idx].IsStr)
            {
                string m = UnquoteStr(_tok[_idx].Value);
                if (!string.IsNullOrEmpty(m))
                {
                    Usings.Add(m);
                }

                _idx++;
                if (_idx < _tok.Count && _tok[_idx].IsPunct(";"))
                {
                    _idx++;
                }

                return;
            }

            while (_idx < _tok.Count && !_tok[_idx].IsWord("from") && !_tok[_idx].IsPunct(";"))
            {
                _idx++;
            }

            if (_idx < _tok.Count && _tok[_idx].IsWord("from"))
            {
                _idx++;
                if (_idx < _tok.Count && _tok[_idx].IsStr)
                {
                    string m = UnquoteStr(_tok[_idx].Value);
                    if (!string.IsNullOrEmpty(m))
                    {
                        Usings.Add(m);
                    }

                    _idx++;
                }
            }
            if (_idx < _tok.Count && _tok[_idx].IsPunct(";"))
            {
                _idx++;
            }
        }
        private TypeDeclaration? ParseClass(bool isExported, bool isAbstract)
        {
            int classLine = _tok[_idx].Line;
            _idx++;

            if (_idx >= _tok.Count || !_tok[_idx].IsIdent)
            {
                Errors.Add($"{_fp}({classLine},1): error: Expected class name");
                SkipToBlockEnd(); return null;
            }

            string className = _tok[_idx].Value; _idx++;

            List<string> typeParameters = ParseTypeParametersAt(ref _idx, _tok.Count);
            bool isGeneric = typeParameters.Count > 0;

            string? baseType = null;
            if (_idx < _tok.Count && _tok[_idx].IsWord("extends"))
            {
                _idx++;
                if (_idx < _tok.Count && _tok[_idx].IsIdent)
                {
                    baseType = _tok[_idx].Value; _idx++;
                    if (_idx < _tok.Count && _tok[_idx].IsPunct("<"))
                    {
                        _idx = FindMatchingClose(_idx) + 1;
                    }
                }
            }

            bool hasImplements = false;
            if (_idx < _tok.Count && _tok[_idx].IsWord("implements"))
            {
                hasImplements = true; _idx++;
                while (_idx < _tok.Count && !_tok[_idx].IsPunct("{"))
                {
                    _idx++;
                }
            }

            while (_idx < _tok.Count && !_tok[_idx].IsPunct("{"))
            {
                _idx++;
            }

            if (_idx >= _tok.Count)
            {
                Errors.Add($"{_fp}({classLine},1): error: Missing body for class '{className}'");
                return null;
            }

            int openBrace = _idx;
            int closeBrace = FindMatchingBrace(openBrace);
            _idx = closeBrace + 1;

            List<MethodDeclaration> methods = new List<MethodDeclaration>();
            List<MethodDeclaration> constructors = new List<MethodDeclaration>();
            ParseClassBody(openBrace, closeBrace, methods, constructors);

            List<string> baseTypes = baseType != null ? new List<string> { baseType } : new List<string>();
            Modifier modifiers = isExported ? Modifier.Public : Modifier.None;
            if (isAbstract)
            {
                modifiers |= Modifier.Abstract;
            }

            return new JavaScriptTypeDeclaration(new TypeDeclaration(className, TypeKind.Class, modifiers,
                baseTypes, [], constructors, methods, [], [], classLine))
            {
                IsExported = isExported, HasBaseClass = baseTypes.Count > 0,
                IsAbstract = isAbstract, IsGeneric = isGeneric, HasImplements = hasImplements,
                TypeParameters = typeParameters
            };
        }

        private void ParseClassBody(int openBrace, int closeBrace,
            List<MethodDeclaration> methods, List<MethodDeclaration> constructors)
        {
            int idx = openBrace + 1;
            while (idx < closeBrace && idx < _tok.Count)
            {
                while (idx < closeBrace && _tok[idx].IsPunct("@"))
                {
                    idx++;
                    while (idx < closeBrace && (_tok[idx].IsIdent || _tok[idx].IsPunct(".")))
                    {
                        idx++;
                    }

                    if (idx < closeBrace && _tok[idx].IsPunct("("))
                    {
                        idx = FindMatchingClose(idx) + 1;
                    }
                }
                if (idx >= closeBrace)
                {
                    break;
                }

                if (_tok[idx].IsPunct(";")) { idx++; continue; }

                bool isStatic = false, isAsync = false, isAbstractM = false;
                bool isGenerator = false, isGetter = false, isSetter = false;
                Modifier visibility = Modifier.Public;

                while (idx < closeBrace)
                {
                    JsToken t = _tok[idx];
                    if (t.IsWord("static")) { isStatic = true; idx++; }
                    else if (t.IsWord("async")) { isAsync = true; idx++; }
                    else if (t.IsWord("abstract")) { isAbstractM = true; idx++; }
                    else if (t.IsWord("private")) { visibility = Modifier.Private; idx++; }
                    else if (t.IsWord("protected")) { visibility = Modifier.Protected; idx++; }
                    else if (t.IsWord("override") || t.IsWord("readonly") || t.IsWord("public"))
                    {
                        idx++;
                    }
                    else
                    {
                        break;
                    }
                }
                if (idx >= closeBrace)
                {
                    break;
                }

                if (_tok[idx].IsPunct("*")) { isGenerator = true; idx++; }
                if (idx >= closeBrace)
                {
                    break;
                }

                if (!isGenerator && (_tok[idx].IsWord("get") || _tok[idx].IsWord("set")))
                {
                    if (idx + 2 < closeBrace && _tok[idx + 1].IsIdent && _tok[idx + 2].IsPunct("("))
                    {
                        isGetter = _tok[idx].IsWord("get");
                        isSetter = _tok[idx].IsWord("set");
                        idx++;
                    }
                }
                if (idx >= closeBrace)
                {
                    break;
                }

                if (_tok[idx].IsPunct("["))
                {
                    idx = FindMatchingClose(idx) + 1;
                    SkipMemberRemainder(ref idx, closeBrace);
                    continue;
                }

                if (!_tok[idx].IsIdent) { idx++; continue; }

                string memberName = _tok[idx].Value;
                int memberLine = _tok[idx].Line;
                idx++;

                // Arrow class field: name = (...) => ...
                if (idx < closeBrace && _tok[idx].IsPunct("="))
                {
                    idx++;
                    if (idx < closeBrace && _tok[idx].IsWord("async")) { isAsync = true; idx++; }

                    if (idx < closeBrace && _tok[idx].IsPunct("("))
                    {
                        int closeP = FindMatchingClose(idx);
                        List<ParameterDeclaration> parms = ParseParametersFromTokens(idx, closeP);
                        idx = closeP + 1;

                        if (idx < closeBrace && _tok[idx].IsPunct(":")) { idx++; SkipTsTypeAt(ref idx, closeBrace); }

                        if (idx < closeBrace && _tok[idx].IsPunct("=>"))
                        {
                            idx++;
                            List<StatementInfo> arrowStmts;
                            if (idx < closeBrace && _tok[idx].IsPunct("{"))
                            {
                                int bc2 = FindMatchingBrace(idx);
                                arrowStmts = ParseBodyStatements(idx, bc2, isInMethod: true);
                                Statements.AddRange(arrowStmts);
                                idx = bc2 + 1;
                            }
                            else
                            {
                                arrowStmts = [];
                                while (idx < closeBrace && !_tok[idx].IsPunct(";") && !_tok[idx].IsPunct("}"))
                                {
                                    idx++;
                                }

                                if (idx < closeBrace && _tok[idx].IsPunct(";"))
                                {
                                    idx++;
                                }
                            }
                            Modifier mod2 = visibility;
                            if (isStatic)
                            {
                                mod2 |= Modifier.Static;
                            }

                            if (isAsync)
                            {
                                mod2 |= Modifier.Async;
                            }

                            JavaScriptMethodDeclaration m2 = NewMethod(memberName, mod2, [], null, parms, memberLine, arrowStmts)
                                .AsJavaScript(isArrow: true);
                            foreach (StatementInfo statement in arrowStmts)
                            {
                                statement.Method = m2;
                            }

                            if (memberName == "constructor")
                            {
                                constructors.Add(m2);
                            }
                            else
                            {
                                methods.Add(m2);
                            }

                            continue;
                        }
                    }
                    while (idx < closeBrace && !_tok[idx].IsPunct(";") && !_tok[idx].IsPunct("}"))
                    {
                        idx++;
                    }

                    if (idx < closeBrace && _tok[idx].IsPunct(";"))
                    {
                        idx++;
                    }

                    continue;
                }

                // TS class field: name: Type;
                if (idx < closeBrace && _tok[idx].IsPunct(":") && (idx + 1 >= closeBrace || !_tok[idx + 1].IsPunct(":")))
                {
                    idx++;
                    SkipTsTypeAt(ref idx, closeBrace);
                    if (idx < closeBrace && _tok[idx].IsPunct("="))
                    {
                        idx++;
                        while (idx < closeBrace && !_tok[idx].IsPunct(";") && !_tok[idx].IsPunct("}"))
                        {
                            idx++;
                        }
                    }
                    if (idx < closeBrace && _tok[idx].IsPunct(";"))
                    {
                        idx++;
                    }

                    continue;
                }

                // Regular method: name(...) { ... }
                List<string> typeParameters = ParseTypeParametersAt(ref idx, closeBrace);

                if (idx < closeBrace && _tok[idx].IsPunct("("))
                {
                    int closeP = FindMatchingClose(idx);
                    List<ParameterDeclaration> parms = ParseParametersFromTokens(idx, closeP);
                    idx = closeP + 1;

                    TypeReference? returnType = null;
                    if (idx < closeBrace && _tok[idx].IsPunct(":"))
                    { idx++; returnType = ParseTypeAnnotationAt(ref idx, closeBrace); }

                    if (idx >= closeBrace || _tok[idx].IsPunct(";"))
                    {
                        JavaScriptMethodDeclaration signature = NewMethod(memberName, visibility |
                            (isStatic ? Modifier.Static : Modifier.None) |
                            (isAsync ? Modifier.Async : Modifier.None) |
                            (isAbstractM ? Modifier.Abstract : Modifier.None), [], returnType, parms, memberLine, [], typeParameters)
                            .AsJavaScript(isGenerator: isGenerator, isGetter: isGetter, isSetter: isSetter);
                        methods.Add(signature);
                        if (idx < closeBrace) { idx++; }
                        continue;
                    }

                    if (idx < closeBrace && _tok[idx].IsPunct("{"))
                    {
                        int bc = FindMatchingBrace(idx);
                        List<StatementInfo> bStmts = ParseBodyStatements(idx, bc, isInMethod: true);
                        Statements.AddRange(bStmts);
                        idx = bc + 1;

                        Modifier mod = visibility;
                        if (isStatic)
                        {
                            mod |= Modifier.Static;
                        }

                        if (isAsync)
                        {
                            mod |= Modifier.Async;
                        }

                        if (isAbstractM)
                        {
                            mod |= Modifier.Abstract;
                        }

                        JavaScriptMethodDeclaration meth = NewMethod(memberName, mod, [], returnType, parms, memberLine, bStmts, typeParameters)
                            .AsJavaScript(isGenerator: isGenerator, isGetter: isGetter, isSetter: isSetter);
                        foreach (StatementInfo statement in bStmts)
                        {
                            statement.Method = meth;
                        }

                        if (memberName == "constructor")
                        {
                            constructors.Add(meth);
                        }
                        else
                        {
                            methods.Add(meth);
                        }
                    }
                    continue;
                }

                while (idx < closeBrace && !_tok[idx].IsPunct(";") && !_tok[idx].IsPunct("}"))
                {
                    idx++;
                }

                if (idx < closeBrace && _tok[idx].IsPunct(";"))
                {
                    idx++;
                }
            }
        }

        private void SkipMemberRemainder(ref int idx, int closeBrace)
        {
            if (idx < closeBrace && _tok[idx].IsPunct("("))
            {
                idx = FindMatchingClose(idx) + 1;
                if (idx < closeBrace && _tok[idx].IsPunct(":")) { idx++; SkipTsTypeAt(ref idx, closeBrace); }
                if (idx < closeBrace && _tok[idx].IsPunct("{"))
                {
                    idx = FindMatchingBrace(idx) + 1;
                }
                else if (idx < closeBrace && _tok[idx].IsPunct(";"))
                {
                    idx++;
                }
            }
            else
            {
                while (idx < closeBrace && !_tok[idx].IsPunct(";") && !_tok[idx].IsPunct("}"))
                {
                    idx++;
                }

                if (idx < closeBrace && _tok[idx].IsPunct(";"))
                {
                    idx++;
                }
            }
        }
        private void ParseFunction(bool isExported, bool isAsync)
        {
            int functionLine = _tok[_idx].Line;
            _idx++;
            bool isGenerator = false;
            if (_idx < _tok.Count && _tok[_idx].IsPunct("*")) { isGenerator = true; _idx++; }

            if (_idx >= _tok.Count || !_tok[_idx].IsIdent) { SkipToBlockEnd(); return; }
            string functionName = _tok[_idx].Value;
            _idx++;

            List<string> typeParameters = ParseTypeParametersAt(ref _idx, _tok.Count);

            if (_idx >= _tok.Count || !_tok[_idx].IsPunct("(")) { SkipToBlockEnd(); return; }

            int closeP = FindMatchingClose(_idx);
            List<ParameterDeclaration> parameters = ParseParametersFromTokens(_idx, closeP);
            _idx = closeP + 1;

            TypeReference? returnType = null;
            if (_idx < _tok.Count && _tok[_idx].IsPunct(":"))
            { _idx++; returnType = ParseTypeAnnotationAt(ref _idx, _tok.Count); }

            if (_idx >= _tok.Count || !_tok[_idx].IsPunct("{"))
            {
                return;
            }

            int openBrace = _idx;
            int closeBrace = FindMatchingBrace(openBrace);
            _idx = closeBrace + 1;

            List<StatementInfo> bStmts = ParseBodyStatements(openBrace, closeBrace, isInMethod: true);
            Modifier modifiers = isExported ? Modifier.Public : Modifier.None;
            if (isAsync)
            {
                modifiers |= Modifier.Async;
            }

            JavaScriptMethodDeclaration method = NewMethod(functionName, modifiers, [], returnType, parameters, functionLine, bStmts, typeParameters)
                .AsJavaScript(isGenerator: isGenerator);
            foreach (StatementInfo statement in bStmts)
            {
                statement.Method = method;
            }

            Methods.Add(method);
            Statements.AddRange(bStmts);
        }

        private void ParseTsType(bool isExported)
        {
            TypeKind kind = _tok[_idx].IsWord("interface") ? TypeKind.Interface : TypeKind.Enum;
            int typeLine = _tok[_idx].Line;
            _idx++;

            if (_idx >= _tok.Count || !_tok[_idx].IsIdent) { SkipToBlockOrSemi(); return; }

            string typeName = _tok[_idx].Value; _idx++;
            List<string> typeParameters = ParseTypeParametersAt(ref _idx, _tok.Count);

            while (_idx < _tok.Count && (_tok[_idx].IsWord("extends") || _tok[_idx].IsWord("implements")))
            {
                _idx++;
                while (_idx < _tok.Count && !_tok[_idx].IsPunct("{") && !_tok[_idx].IsPunct(";"))
                {
                    _idx++;
                }
            }

            List<MethodDeclaration> methods = [];
            if (_idx < _tok.Count && _tok[_idx].IsPunct("{"))
            {
                int cb = FindMatchingBrace(_idx);
                if (kind == TypeKind.Interface) { ParseClassBody(_idx, cb, methods, []); }
                _idx = cb + 1;
            }
            else if (_idx < _tok.Count && _tok[_idx].IsPunct(";"))
            {
                _idx++;
            }

            Modifier modifiers = isExported ? Modifier.Public : Modifier.None;
            Types.Add(new JavaScriptTypeDeclaration(new TypeDeclaration(typeName, kind, modifiers, [], [], [], methods, [], [], typeLine))
            {
                IsExported = isExported, IsGeneric = typeParameters.Count > 0, TypeParameters = typeParameters
            });
        }

        private List<StatementInfo> ParseBodyStatements(int openBrace, int closeBrace, bool isInMethod)
            => ParseStatements(openBrace + 1, closeBrace, isInMethod);

        private List<StatementInfo> ParseStatements(int start, int end, bool isInMethod)
        {
            List<StatementInfo> statements = new List<StatementInfo>();
            Stack<(StatementInfo Statement, int End)> parents = new Stack<(StatementInfo Statement, int End)>();
            for (int idx = start; idx < end && idx < _tok.Count; idx++)
            {
                while (parents.Count > 0 && idx >= parents.Peek().End)
                {
                    parents.Pop();
                }

                JsToken token = _tok[idx];
                StatementInfo? statement = null;
                int statementEnd = idx + 1;
                if (token.IsWord("if") || token.IsWord("while") || token.IsWord("for") ||
                    token.IsWord("switch") || token.IsWord("catch"))
                {
                    int open = idx + 1;
                    if (token.IsWord("for") && open < end && _tok[open].IsWord("await")) { open++; }
                    if (open < end && _tok[open].IsPunct("("))
                    {
                        int close = FindMatchingClose(open);
                        int body = close + 1;
                        statementEnd = body < end && _tok[body].IsPunct("{")
                            ? FindMatchingBrace(body) + 1 : SkipToSemi(body, end);
                        string kind = token.Value;
                        if (kind == "for" && HasWordInRange(open + 1, close, "of"))
                        {
                            kind = "for-of";
                        }
                        else if (kind == "for" && HasWordInRange(open + 1, close, "in"))
                        {
                            kind = "for-in";
                        }

                        List<string> keywords = token.IsWord("for") && open == idx + 2 ? ["for", "await"] : [token.Value];
                        statement = new JavaScriptStatementInfo(new StatementInfo(kind, keywords, null, null, [], token.Line, isInMethod))
                        {
                            Condition = TokenText(open + 1, close),
                            IsBraced = body < end && _tok[body].IsPunct("{"),
                            IsErrorHandler = kind == "catch", IsGenericErrorHandler = kind == "catch",
                            HasRethrow = kind == "catch" && ScanForRethrow(body, statementEnd),
                            IsOriginalExceptionAlwaysRethrown = kind == "catch" && close == open + 2 &&
                                _tok[open + 1].IsIdent && IsSafeCatch(body, statementEnd) &&
                                !HasEnclosingFinally(start, idx) &&
                                CatchFlow(body, statementEnd, _tok[open + 1].Value) == 2,
                            IsCancellationHandler = kind == "catch" && close == open + 2 &&
                                IsAbortHandler(body, statementEnd, _tok[open + 1].Value, out _, out _),
                            IsCallerCancellationPreserved = kind == "catch" && close == open + 2 &&
                                IsSafeCatch(body, statementEnd) && !HasEnclosingFinally(start, idx) &&
                                IsAbortHandler(body, statementEnd, _tok[open + 1].Value, out int branchStart, out int branchEnd) &&
                                CatchFlow(branchStart, branchEnd, _tok[open + 1].Value) == 2
                        };
                    }
                }
                else if (token.IsWord("try"))
                {
                    statementEnd = idx + 1 < end && _tok[idx + 1].IsPunct("{")
                        ? FindMatchingBrace(idx + 1) + 1 : idx + 1;
                    statement = new JavaScriptStatementInfo(new StatementInfo("try", [], null, null, [], token.Line, isInMethod))
                    { IsBraced = true };
                }
                else if (token.IsWord("return") || token.IsWord("throw") || token.IsWord("yield") ||
                    token.IsWord("await") && (idx == start || !_tok[idx - 1].IsWord("for")))
                {
                    statementEnd = SkipToSemi(idx + 1, end);
                    int expressionEnd = statementEnd > idx && _tok[statementEnd - 1].IsPunct(";")
                        ? statementEnd - 1 : statementEnd;
                    string? typeName = token.IsWord("throw") && idx + 2 < end && _tok[idx + 1].IsWord("new")
                        ? _tok[idx + 2].Value : null;
                    statement = new JavaScriptStatementInfo(new StatementInfo(token.Value, [], typeName, null, [], token.Line, isInMethod))
                    { Expression = TokenText(idx + 1, expressionEnd), ValueExpression = ParseExpression(idx + 1, expressionEnd) };
                }
                else if (token.IsWord("const") || token.IsWord("let") || token.IsWord("var") || token.IsWord("using"))
                {
                    if (idx + 1 < end && _tok[idx + 1].IsIdent)
                    {
                        statement = new JavaScriptStatementInfo(new StatementInfo("declaration", [token.Value], null,
                            _tok[idx + 1].Value, [], token.Line, isInMethod));
                    }
                }
                else if (token.IsWord("debugger"))
                {
                    statement = new JavaScriptStatementInfo(new StatementInfo("call", ["debugger"], null, "debugger", [], token.Line, isInMethod));
                }
                else if (TryEnvironmentAccess(idx, end, out int accessEnd))
                {
                    int targetStart = idx, targetEnd = accessEnd;
                    while (targetStart > start && _tok[targetStart - 1].IsPunct("(") &&
                        FindMatchingClose(targetStart - 1) == targetEnd)
                    {
                        targetStart--;
                        targetEnd++;
                    }
                    bool writeOnly = targetEnd < end && _tok[targetEnd].IsPunct("=") ||
                        targetStart > start && _tok[targetStart - 1].IsWord("delete");
                    statement = new JavaScriptStatementInfo(new StatementInfo("environment-access", [], null, null, [], token.Line, isInMethod))
                    {
                        ValueExpression = ParseExpression(idx, accessEnd),
                        IsEnvironmentVariableAccess = true, IsEnvironmentVariableRead = !writeOnly
                    };
                }
                else if (token.IsIdent && (!IsCtrlKw(token.Value) || token.IsWord("this") || token.IsWord("require")) &&
                    (idx == start || !_tok[idx - 1].IsPunct(".") && !_tok[idx - 1].IsPunct("?.")))
                {
                    int calleeEnd = idx + 1;
                    while (calleeEnd + 1 < end && (_tok[calleeEnd].IsPunct(".") || _tok[calleeEnd].IsPunct("?.")) &&
                        _tok[calleeEnd + 1].IsIdent)
                    {
                        calleeEnd += 2;
                    }

                    int open = calleeEnd;
                    if (open < end && _tok[open].IsPunct("<"))
                    {
                        open = FindMatchingClose(open) + 1;
                    }

                    if (open < end && _tok[open].IsPunct("(") && !IsCallKw(_tok[calleeEnd - 1].Value))
                    {
                        int close = FindMatchingClose(open);
                        List<(int Start, int End)> ranges = SplitArguments(open + 1, close);
                        string? typeName = ExpressionName(idx > start && _tok[idx - 1].IsWord("new")
                            ? ParseExpression(idx, calleeEnd) : ParseExpression(idx, calleeEnd).Target);
                        statement = new JavaScriptStatementInfo(new StatementInfo("call", [], typeName, _tok[calleeEnd - 1].Value,
                            ranges.Select(r => TokenText(r.Start, r.End)).ToList(), token.Line, isInMethod))
                        {
                            CallTarget = ParseExpression(idx, calleeEnd),
                            ArgumentExpressions = ranges.Select(r => ParseExpression(r.Start, r.End)).ToList(),
                            IsOwnershipTransferred = idx > start && _tok[idx - 1].IsWord("new") &&
                                IsDirectTransfer(start, idx - 1, close + 1) && !HasEnclosingFinally(start, idx),
                            IsUsingResource = IsDirectUsingInitializer(start, idx, close + 1)
                        };
                        statementEnd = close + 1;
                        idx = open;
                    }
                }
                if (statement is null)
                {
                    continue;
                }

                if (parents.Count > 0)
                {
                    statement.Parent = parents.Peek().Statement;
                    statement.Parent.MutableChildren.Add(statement);
                }
                statements.Add(statement);
                if (statementEnd > idx + 1)
                {
                    parents.Push((statement, statementEnd));
                }
            }
            return statements;
        }

        private static string? ExpressionName(ExpressionInfo? expression) => expression switch
        {
            { Kind: "identifier" } => expression.Name,
            { Kind: "memberAccess", Target: not null } when ExpressionName(expression.Target) is string target =>
                target + "." + expression.Name,
            _ => null
        };

        private List<string> ParseTypeParametersAt(ref int index, int end)
        {
            List<string> names = [];
            if (index >= end || !_tok[index].IsPunct("<")) { return names; }
            int close = FindMatchingClose(index);
            int current = index + 1;
            bool expectName = true;
            for (; current < close; current++)
            {
                if (expectName && (_tok[current].IsWord("const") || _tok[current].IsWord("in") ||
                    _tok[current].IsWord("out"))) { continue; }
                if (expectName && _tok[current].IsIdent)
                {
                    names.Add(_tok[current].Value);
                    expectName = false;
                }
                else if (_tok[current].IsPunct(","))
                {
                    expectName = true;
                }
                else if (_tok[current].IsPunct("<") || _tok[current].IsPunct("(") ||
                    _tok[current].IsPunct("[") || _tok[current].IsPunct("{"))
                {
                    current = FindMatchingClose(current);
                }
            }
            index = close + 1;
            return names;
        }

        private bool TryEnvironmentAccess(int start, int end, out int accessEnd)
        {
            accessEnd = start;
            if (!_tok[start].IsWord("process") || start > 0 &&
                (_tok[start - 1].IsPunct(".") || _tok[start - 1].IsPunct("?."))) { return false; }
            int index = start + 1;
            if (index + 1 < end && (_tok[index].IsPunct(".") || _tok[index].IsPunct("?.")) &&
                _tok[index + 1].IsWord("env"))
            {
                index += 2;
            }
            else if (index + 2 < end && _tok[index].IsPunct("[") && _tok[index + 1].IsStr &&
                DecodeString(_tok[index + 1].Value) == "env" && _tok[index + 2].IsPunct("]"))
            {
                index += 3;
            }
            else { return false; }
            if (index + 1 < end && (_tok[index].IsPunct(".") || _tok[index].IsPunct("?.")) &&
                _tok[index + 1].IsIdent)
            {
                index += 2;
            }
            else if (index < end && _tok[index].IsPunct("["))
            {
                index = FindMatchingClose(index) + 1;
            }
            accessEnd = index;
            return true;
        }

        private bool IsDirectTransfer(int start, int creation, int callEnd)
        {
            if (callEnd < _tok.Count && !_tok[callEnd].IsPunct(";") && !_tok[callEnd].IsPunct("}"))
            { return false; }
            int previous = creation - 1;
            if (previous >= start && _tok[previous].IsWord("return")) { return true; }
            if (previous < start || !_tok[previous].IsPunct("=")) { return false; }
            if (previous - 3 >= start && _tok[previous - 3].IsWord("this") &&
                _tok[previous - 2].IsPunct(".") && _tok[previous - 1].IsIdent) { return true; }
            for (int index = previous - 2; index >= start && !_tok[index].IsPunct(";") &&
                !_tok[index].IsPunct("{"); index--)
            {
                if (_tok[index].IsWord("this") && _tok[index + 1].IsPunct("[") &&
                    FindMatchingClose(index + 1) == previous - 1) { return true; }
            }
            return false;
        }

        private bool IsDirectUsingInitializer(int start, int call, int callEnd)
        {
            if (callEnd < _tok.Count && !_tok[callEnd].IsPunct(";") && !_tok[callEnd].IsPunct("}"))
            { return false; }
            int value = call > start && _tok[call - 1].IsWord("new") ? call - 1 : call;
            return value - 3 >= start && _tok[value - 1].IsPunct("=") &&
                _tok[value - 2].IsIdent && _tok[value - 3].IsWord("using");
        }

        private bool HasEnclosingFinally(int start, int position)
        {
            for (int index = start; index < position; index++)
            {
                if (!_tok[index].IsWord("try") || index + 1 >= _tok.Count ||
                    !_tok[index + 1].IsPunct("{")) { continue; }
                int next = FindMatchingBrace(index + 1) + 1;
                if (next < _tok.Count && _tok[next].IsWord("catch"))
                {
                    next++;
                    if (next < _tok.Count && _tok[next].IsPunct("(")) { next = FindMatchingClose(next) + 1; }
                    if (next < _tok.Count && _tok[next].IsPunct("{")) { next = FindMatchingBrace(next) + 1; }
                }
                if (position < next && next < _tok.Count && _tok[next].IsWord("finally")) { return true; }
            }
            return false;
        }

        private bool IsSafeCatch(int start, int end)
        {
            if (start >= end || !_tok[start].IsPunct("{") || FindMatchingBrace(start) != end - 1)
            { return false; }
            for (int index = start; index < end; index++)
            {
                JsToken token = _tok[index];
                if (token.IsWord("eval") && index + 1 < end && _tok[index + 1].IsPunct("(")) { return false; }
                if (token.Kind == JsTok.Word && token.Value is "function" or "class" or "const" or "let" or
                    "var" or "using" or "try" or "delete" or "await" or "yield" ||
                    token.Kind == JsTok.Punct && token.Value is "=>" or "=" or "+=" or "-=" or "*=" or "/=" or
                    "%=" or "**=" or "&&=" or "||=" or "??=" or "&=" or "|=" or "^=" or "<<=" or ">>=" or
                    ">>>=" or "++" or "--") { return false; }
            }
            return true;
        }

        private int CatchFlow(int start, int end, string caught)
        {
            int index = start;
            int result = 1;
            while (index < end && (result & 1) != 0)
            {
                int outcome = CatchStatementFlow(ref index, end, caught);
                result = (result & ~1) | outcome;
            }
            return result;
        }

        private int CatchStatementFlow(ref int index, int end, string caught)
        {
            JsToken token = _tok[index];
            if (token.IsPunct("{"))
            {
                int close = FindMatchingBrace(index);
                int outcome = CatchFlow(index + 1, Math.Min(close, end), caught);
                index = close + 1;
                return outcome;
            }
            if (token.IsPunct(";")) { index++; return 1; }
            if (token.IsWord("if") && index + 1 < end && _tok[index + 1].IsPunct("("))
            {
                index = FindMatchingClose(index + 1) + 1;
                if (index >= end) { return 4; }
                int thenFlow = CatchStatementFlow(ref index, end, caught);
                int elseFlow = 1;
                if (index < end && _tok[index].IsWord("else"))
                {
                    index++;
                    elseFlow = index < end ? CatchStatementFlow(ref index, end, caught) : 4;
                }
                return thenFlow | elseFlow;
            }
            if (token.IsWord("return") || token.IsWord("throw"))
            {
                int value = ++index;
                while (index < end && !_tok[index].IsPunct(";") && !_tok[index].IsPunct("}"))
                {
                    if (_tok[index].IsPunct("(") || _tok[index].IsPunct("[") || _tok[index].IsPunct("{"))
                    { index = FindMatchingClose(index); }
                    index++;
                }
                ExpressionInfo expression = ParseExpression(value, index);
                int result = token.IsWord("throw") && value < end && token.Line == _tok[value].Line &&
                    expression.Kind == "identifier" && expression.Name == caught ? 2 : 4;
                if (index < end && _tok[index].IsPunct(";")) { index++; }
                return result;
            }
            if (token.IsWord("while") || token.IsWord("for") || token.IsWord("do") || token.IsWord("switch") ||
                token.IsWord("break") || token.IsWord("continue") || token.IsWord("try") || token.IsWord("with"))
            { index = end; return 4; }
            int first = index;
            while (index < end && !_tok[index].IsPunct(";") && !_tok[index].IsPunct("}"))
            {
                if (_tok[index].IsPunct("(") || _tok[index].IsPunct("[") || _tok[index].IsPunct("{"))
                { index = FindMatchingClose(index); }
                index++;
            }
            // Only a parsed call is accepted as a neutral expression statement.
            bool neutral = ParseExpression(first, index).Kind == "invocation";
            if (index < end && _tok[index].IsPunct(";")) { index++; }
            else if (index == first) { index = end; }
            return neutral ? 1 : 4;
        }

        private bool IsAbortHandler(int body, int end, string caught, out int branchStart, out int branchEnd)
        {
            branchStart = branchEnd = 0;
            if (body >= end || !_tok[body].IsPunct("{")) { return false; }
            int first = body + 1;
            if (first + 1 >= end || !_tok[first].IsWord("if") || !_tok[first + 1].IsPunct("(")) { return false; }
            int close = FindMatchingClose(first + 1);
            for (int index = first + 2; index < close; index++)
            {
                if (!_tok[index].IsPunct("===") && !_tok[index].IsPunct("==")) { continue; }
                ExpressionInfo left = ParseExpression(first + 2, index);
                ExpressionInfo right = ParseExpression(index + 1, close);
                bool Pair(ExpressionInfo access, ExpressionInfo value) =>
                    (access.Kind == "memberAccess" && access.Name == "name" ||
                        access.Kind == "elementAccess" && access.Children is [{ Kind: "stringLiteral", StringValue: "name" }]) &&
                    access.Target is { Kind: "identifier" } target && target.Name == caught &&
                    value.Kind == "stringLiteral" && value.StringValue == "AbortError";
                if (!Pair(left, right) && !Pair(right, left)) { return false; }
                branchStart = close + 1;
                branchEnd = branchStart;
                if (branchStart >= end) { return false; }
                CatchStatementFlow(ref branchEnd, end, caught);
                return true;
            }
            return false;
        }

        private string TokenText(int start, int end)
        {
            if (start >= end || start >= _tok.Count)
            {
                return string.Empty;
            }

            int Offset(JsToken token) => _lineOffsets[token.Line - 1] + token.Col - 1;
            int from = Offset(_tok[start]);
            int to = end < _tok.Count ? Offset(_tok[end]) : _src.Length;
            return _src[from..to].Trim();
        }

        private List<(int Start, int End)> SplitArguments(int start, int end)
        {
            List<(int, int)> result = new List<(int, int)>();
            int first = start;
            for (int idx = start; idx < end; idx++)
            {
                if (_tok[idx].IsPunct("(") || _tok[idx].IsPunct("[") || _tok[idx].IsPunct("{"))
                {
                    idx = FindMatchingClose(idx);
                }
                else if (_tok[idx].IsPunct(","))
                {
                    result.Add((first, idx));
                    first = idx + 1;
                }
            }
            if (first < end)
            {
                result.Add((first, end));
            }

            return result;
        }

        private ExpressionInfo ParseExpression(int start, int end)
        {
            if (start >= end || start >= _tok.Count)
            {
                return new("other");
            }

            if (_tok[start].IsPunct("(") && FindMatchingClose(start) == end - 1)
            {
                return ParseExpression(start + 1, end - 1);
            }

            if (_tok[start].IsWord("new") && start + 1 < end)
            {
                return ParseExpression(start + 1, end);
            }
            if (end == start + 1)
            {
                JsToken token = _tok[start];
                if (token.IsStr)
                {
                    return new("stringLiteral") { StringValue = DecodeString(token.Value) };
                }

                if (token.IsWord("true") || token.IsWord("false"))
                {
                    return new("booleanLiteral") { Name = token.Value };
                }

                if (token.IsWord("null"))
                {
                    return new("nullLiteral");
                }

                if (token.IsIdent)
                {
                    return new("identifier") { Name = token.Value };
                }

                if (token.Kind == JsTok.Number)
                {
                    return new("numericLiteral") { Name = token.Value };
                }

                return new("other");
            }
            if (_tok[start].IsPunct("[") && FindMatchingClose(start) == end - 1)
            {
                return new("arrayLiteral")
                { Children = SplitArguments(start + 1, end - 1).Select(r => ParseExpression(r.Start, r.End)).ToList() };
            }

            if (_tok[start].IsPunct("{") && FindMatchingClose(start) == end - 1)
            {
                return new("objectLiteral");
            }

            if (_tok[end - 1].IsPunct(")"))
            {
                for (int open = start + 1; open < end; open++)
                {
                    if (!_tok[open].IsPunct("(") || FindMatchingClose(open) != end - 1)
                    {
                        continue;
                    }

                    return new("invocation")
                    {
                        Target = ParseExpression(start, open),
                        Children = SplitArguments(open + 1, end - 1).Select(r => ParseExpression(r.Start, r.End)).ToList()
                    };
                }
            }
            if (end - start >= 3 && (_tok[end - 2].IsPunct(".") || _tok[end - 2].IsPunct("?.")) &&
                _tok[end - 1].IsIdent)
            {
                return new("memberAccess") { Name = _tok[end - 1].Value, Target = ParseExpression(start, end - 2) };
            }
            if (_tok[end - 1].IsPunct("]"))
            {
                for (int open = start + 1; open < end - 1; open++)
                {
                    if (_tok[open].IsPunct("[") && FindMatchingClose(open) == end - 1)
                    {
                        return new("elementAccess")
                        { Target = ParseExpression(start, open), Children = [ParseExpression(open + 1, end - 1)] };
                    }
                }
            }

            return new("other");
        }

        private static string DecodeString(string value)
        {
            string content = UnquoteStr(value);
            StringBuilder result = new System.Text.StringBuilder();
            for (int idx = 0; idx < content.Length; idx++)
            {
                if (content[idx] != '\\' || idx + 1 >= content.Length) { result.Append(content[idx]); continue; }
                char escaped = content[++idx];
                int digits = escaped == 'u' ? 4 : escaped == 'x' ? 2 : 0;
                if (digits > 0 && idx + digits < content.Length &&
                    int.TryParse(content.AsSpan(idx + 1, digits), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out int code))
                { result.Append((char)code); idx += digits; }
                else if (escaped == '\r' || escaped == '\n')
                { if (escaped == '\r' && idx + 1 < content.Length && content[idx + 1] == '\n')
                    {
                        idx++;
                    }
                }
                else
                {
                    result.Append(escaped switch
                {
                    'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 'v' => '\v', '0' => '\0', _ => escaped
                });
                }
            }
            return result.ToString();
        }
        private List<ParameterDeclaration> ParseParametersFromTokens(int openParen, int closeParen)
        {
            List<ParameterDeclaration> result = new List<ParameterDeclaration>();
            int idx = openParen + 1;

            while (idx < closeParen && idx < _tok.Count)
            {
                if (_tok[idx].IsPunct(",")) { idx++; continue; }

                bool isVariadic = false;
                if (_tok[idx].IsPunct("...")) { isVariadic = true; idx++; }

                if (idx >= closeParen || !_tok[idx].IsIdent) { idx++; continue; }

                string name = _tok[idx].Value;
                int parameterLine = _tok[idx].Line;
                idx++;

                bool optional = idx < closeParen && _tok[idx].IsPunct("?");
                if (optional)
                {
                    idx++;
                }

                TypeReference? typeRef = null;
                if (idx < closeParen && _tok[idx].IsPunct(":"))
                { idx++; typeRef = ParseTypeAnnotationAt(ref idx, closeParen); }

                bool hasDefault = false;
                string? defaultValue = null;
                if (idx < closeParen && _tok[idx].IsPunct("="))
                {
                    hasDefault = true; idx++;
                    int defaultStart = idx;
                    int depth = 0;
                    while (idx < closeParen)
                    {
                        if (_tok[idx].IsPunct("(") || _tok[idx].IsPunct("[") || _tok[idx].IsPunct("{"))
                        {
                            depth++;
                        }
                        else if (_tok[idx].IsPunct(")") || _tok[idx].IsPunct("]") || _tok[idx].IsPunct("}"))
                        { if (depth == 0) { break; } depth--; }
                        else if (_tok[idx].IsPunct(",") && depth == 0)
                        {
                            break;
                        }

                        idx++;
                    }
                    defaultValue = TokenText(defaultStart, idx);
                }

                result.Add(new ParameterDeclaration(name, typeRef, isVariadic, false, hasDefault, parameterLine)
                { IsOptional = optional || hasDefault, DefaultValueText = defaultValue });
            }

            return result;
        }

        private TypeReference? ParseTypeAnnotationAt(ref int idx, int endIdx)
        {
            if (idx >= endIdx || !_tok[idx].IsIdent)
            {
                return null;
            }

            int start = idx;
            string name = _tok[idx].Value; idx++;
            List<TypeReference> arguments = new List<TypeReference>();
            if (idx < endIdx && _tok[idx].IsPunct("<"))
            {
                int close = FindMatchingClose(idx);
                int argument = ++idx;
                while (argument < close)
                {
                    TypeReference? type = ParseTypeAnnotationAt(ref argument, close);
                    if (type is not null)
                    {
                        arguments.Add(type);
                    }

                    if (argument < close && _tok[argument].IsPunct(","))
                    {
                        argument++;
                    }
                    else if (type is null)
                    {
                        argument++;
                    }
                }
                idx = close + 1;
            }

            while (idx < endIdx && _tok[idx].IsPunct("[") && idx + 1 < endIdx && _tok[idx + 1].IsPunct("]"))
            {
                idx += 2;
            }

            while (idx < endIdx && (_tok[idx].IsPunct("|") || _tok[idx].IsPunct("&")))
            {
                idx++;
                if (idx < endIdx && _tok[idx].IsIdent) { idx++; if (idx < endIdx && _tok[idx].IsPunct("<"))
                    {
                        idx = FindMatchingClose(idx) + 1;
                    }
                }
            }

            return new TypeReference(name, null, arguments, TokenText(start, idx));
        }

        private bool ScanForRethrow(int start, int end)
        {
            int depth = 0;
            for (int i = start; i < end && i < _tok.Count; i++)
            {
                JsToken t = _tok[i];
                if (t.IsPunct("{"))
                {
                    depth++;
                }
                else if (t.IsPunct("}")) { depth--; if (depth < 0)
                    {
                        return false;
                    }
                }
                else if (t.IsWord("throw"))
                {
                    return true;
                }
            }
            return false;
        }

        private void SkipTsTypeAt(ref int idx, int endIdx)
        {
            int depth = 0;
            while (idx < endIdx && idx < _tok.Count)
            {
                JsToken t = _tok[idx];
                if (t.IsPunct("(") || t.IsPunct("[") || t.IsPunct("<")) { depth++; idx++; continue; }
                if (t.IsPunct(">") || t.IsPunct(")") || t.IsPunct("]"))
                { if (depth > 0) { depth--; idx++; continue; } break; }
                if (t.IsPunct("{") && depth == 0)
                {
                    break;
                }

                if (t.IsPunct("{")) { depth++; idx++; continue; }
                if (t.IsPunct("}")) { if (depth > 0) { depth--; idx++; continue; } break; }
                if ((t.IsPunct(";") || t.IsPunct(",") || t.IsPunct("=")) && depth == 0)
                {
                    break;
                }

                idx++;
            }
        }

        private int FindMatchingBrace(int openIdx)
        {
            int depth = 1, idx = openIdx + 1;
            while (idx < _tok.Count && depth > 0)
            {
                if (_tok[idx].IsPunct("{"))
                {
                    depth++;
                }
                else if (_tok[idx].IsPunct("}"))
                {
                    depth--;
                }

                idx++;
            }
            return idx - 1;
        }

        private int FindMatchingClose(int openIdx)
        {
            if (openIdx >= _tok.Count)
            {
                return openIdx;
            }

            string open = _tok[openIdx].Value;
            string close = open == "(" ? ")" : open == "[" ? "]" : open == "<" ? ">" : "}";
            int depth = 1, idx = openIdx + 1;
            while (idx < _tok.Count && depth > 0)
            {
                if (_tok[idx].IsPunct(open))
                {
                    depth++;
                }
                else if (_tok[idx].IsPunct(close))
                {
                    depth--;
                }
                else if (open == "<" && _tok[idx].IsPunct(">>"))
                {
                    depth -= 2;
                }
                else if (open == "<" && _tok[idx].IsPunct(">>>"))
                {
                    depth -= 3;
                }

                idx++;
            }
            return idx - 1;
        }

        private int FindStatementEnd(int startIdx)
        {
            int depth = 0, idx = startIdx;
            while (idx < _tok.Count)
            {
                JsToken t = _tok[idx];
                if (idx > startIdx && depth == 0 && IsNewStatement(idx))
                {
                    return idx;
                }

                if (t.IsPunct("(") || t.IsPunct("[") || t.IsPunct("{"))
                {
                    depth++;
                }
                else if (t.IsPunct(")") || t.IsPunct("]")) { if (depth > 0)
                    {
                        depth--;
                    }
                }
                else if (t.IsPunct("}")) { if (depth > 0)
                    {
                        depth--;
                    }
                    else
                    {
                        return idx;
                    }
                }
                else if (t.IsPunct(";") && depth == 0)
                {
                    return idx + 1;
                }

                idx++;
            }
            return idx;
        }

        private int SkipToSemi(int idx, int endIdx)
        {
            int start = idx;
            int depth = 0;
            while (idx < endIdx && idx < _tok.Count)
            {
                JsToken t = _tok[idx];
                if (idx > start && depth == 0 && IsNewStatement(idx))
                {
                    return idx;
                }

                if (t.IsPunct("(") || t.IsPunct("[") || t.IsPunct("{"))
                {
                    depth++;
                }
                else if (t.IsPunct(")") || t.IsPunct("]") || t.IsPunct("}"))
                { if (depth == 0) { return idx; } depth--; }
                else if (t.IsPunct(";") && depth == 0)
                {
                    return idx + 1;
                }

                idx++;
            }
            return idx;
        }

        private bool IsNewStatement(int idx)
        {
            if (idx == 0 || _tok[idx].Line <= _tok[idx - 1].Line)
            {
                return false;
            }

            if (_tok[idx - 1].Value is "=" or "." or "?." or "," or "+" or "-" or "*" or "/" or "=>" or
                "&&" or "||" or "??" or ":" or "?" or "return" or "throw" or "await" or "new")
            {
                return false;
            }

            return _tok[idx].IsIdent;
        }

        private void ParseTypeAlias(bool isExported)
        {
            int start = _idx++;
            if (_idx >= _tok.Count || !_tok[_idx].IsIdent)
            {
                return;
            }

            string name = _tok[_idx++].Value;
            List<string> parameters = new List<string>();
            if (_idx < _tok.Count && _tok[_idx].IsPunct("<"))
            {
                int close = FindMatchingClose(_idx);
                for (int parameter = _idx + 1; parameter < close; parameter++)
                {
                    if (_tok[parameter].IsIdent && (parameter == _idx + 1 || _tok[parameter - 1].IsPunct(",")))
                    {
                        parameters.Add(_tok[parameter].Value);
                    }
                }

                _idx = close + 1;
            }
            if (_idx >= _tok.Count || !_tok[_idx].IsPunct("="))
            {
                return;
            }

            int end = FindStatementEnd(start);
            int typeStart = ++_idx;
            int typeEnd = end > typeStart && _tok[end - 1].IsPunct(";") ? end - 1 : end;
            TypeReference? target = ParseTypeAnnotationAt(ref _idx, typeEnd);
            string original = TokenText(typeStart, typeEnd);
            if (target is null || _idx < typeEnd)
            {
                target = new(original, null, [], original);
            }

            Types.Add(new JavaScriptTypeDeclaration(new TypeDeclaration(name, TypeKind.Alias, isExported ? Modifier.Public : Modifier.None,
                [], [], [], [], [], [], _tok[start].Line)
            { AliasTarget = target, TypeParameters = parameters })
            {
                IsExported = isExported, IsGeneric = parameters.Count > 0
            });
            _idx = end;
        }

        private void SkipToBlockEnd()
        {
            while (_idx < _tok.Count && !_tok[_idx].IsPunct("{"))
            {
                _idx++;
            }

            if (_idx < _tok.Count && _tok[_idx].IsPunct("{"))
            {
                _idx = FindMatchingBrace(_idx) + 1;
            }
        }

        private void SkipToBlockOrSemi()
        {
            while (_idx < _tok.Count && !_tok[_idx].IsPunct("{") && !_tok[_idx].IsPunct(";"))
            {
                _idx++;
            }

            if (_idx < _tok.Count && _tok[_idx].IsPunct("{"))
            {
                _idx = FindMatchingBrace(_idx) + 1;
            }
            else if (_idx < _tok.Count && _tok[_idx].IsPunct(";"))
            {
                _idx++;
            }
        }

        private bool HasWordInRange(int start, int end, string word)
        {
            for (int i = start; i < end && i < _tok.Count; i++)
            {
                if (_tok[i].IsWord(word))
                {
                    return true;
                }
            }

            return false;
        }

        private static string UnquoteStr(string s)
        {
            if (s.Length < 2)
            {
                return s;
            }

            char f = s[0];
            return (f == '\'' || f == '"' || f == '`') ? s[1..^1] : s;
        }

        private static bool IsCtrlKw(string name) => name is
            "if" or "else" or "for" or "while" or "switch" or "do" or "function" or "class"
            or "return" or "new" or "typeof" or "import" or "require" or "catch" or "throw"
            or "try" or "const" or "let" or "var" or "this" or "super" or "export"
            or "default" or "break" or "continue" or "yield" or "async" or "await"
            or "debugger" or "delete" or "void" or "in" or "instanceof" or "of"
            or "true" or "false" or "null" or "undefined";

        private static bool IsCallKw(string name) => name is
            "if" or "for" or "while" or "switch" or "function" or "class"
            or "return" or "new" or "typeof" or "import" or "require" or "catch" or "throw";

        private static MethodDeclaration NewMethod(string name, Modifier mods,
            List<string> decorators, TypeReference? returnType,
            List<ParameterDeclaration> parms, int line, List<StatementInfo> stmts, List<string>? typeParameters = null)
        {
            MethodDeclaration m = new MethodDeclaration(name, mods, decorators, returnType, parms, line)
            { TypeParameters = typeParameters ?? [] };
            m.Statements = stmts;
            return m;
        }
    }
}
