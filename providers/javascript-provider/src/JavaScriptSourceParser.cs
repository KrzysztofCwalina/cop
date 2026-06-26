using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

public class JavaScriptSourceParser : ISourceParser
{
    public override IReadOnlyList<string> Extensions => [".js", ".ts", ".tsx", ".jsx"];
    public override string Language => "javascript";

    public override SourceFile? Parse(string filePath, string sourceText)
    {
        var lexer = new JsLexer(sourceText, filePath);
        var allToks = lexer.Tokenize();
        var p = new JsParser(allToks, lexer.Errors, filePath, sourceText);
        p.Parse();
        return new SourceFile(filePath, "javascript", p.Types, p.Statements, sourceText)
        {
            Usings = p.Usings,
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
        private int _idx;

        public List<TypeDeclaration> Types { get; } = [];
        public List<StatementInfo> Statements { get; } = [];
        public List<string> Usings { get; } = [];
        public List<string> Errors { get; }
        public HashSet<int> CommentLines { get; } = [];
        public List<RegionInfo> Regions { get; } = [];

        public JsParser(List<JsToken> allToks, List<string> lexErrors, string fp, string src)
        {
            _fp = fp; _src = src;
            Errors = new List<string>(lexErrors);
            PreProcess(allToks);
            _tok = allToks.Where(t => !t.IsComment && !t.IsEof).ToList();
        }

        public void Parse()
        {
            while (_idx < _tok.Count) ParseTopLevel();
            CheckBracketBalance();
        }
        private void PreProcess(List<JsToken> all)
        {
            var lines = _src.Split('\n');
            var stack = new Stack<(string Name, int Line)>();

            foreach (var t in all)
            {
                if (t.Kind == JsTok.LineComment)
                {
                    CommentLines.Add(t.Line);
                    string txt = t.Value.AsSpan().TrimStart('/').ToString().Trim();
                    if (txt.StartsWith("[START ") && txt.Contains(']'))
                    {
                        int s = txt.IndexOf("[START ") + 7, e = txt.IndexOf(']', s);
                        if (e > s) stack.Push((txt[s..e], t.Line));
                    }
                    else if (txt.StartsWith("[END ") && txt.Contains(']') && stack.Count > 0)
                    {
                        int s = txt.IndexOf("[END ") + 5, e = txt.IndexOf(']', s);
                        if (e > s)
                        {
                            string endName = txt[s..e];
                            var items = new List<(string, int)>();
                            while (stack.Count > 0)
                            {
                                var top = stack.Pop();
                                if (top.Name == endName)
                                {
                                    int startL = top.Line, endL = t.Line;
                                    var cl = new List<string>();
                                    for (int j = startL; j < endL - 1 && j < lines.Length; j++)
                                        cl.Add(lines[j].TrimEnd('\r'));
                                    Regions.Add(new RegionInfo(endName, startL, endL, string.Join('\n', cl)));
                                    for (int k = items.Count - 1; k >= 0; k--) stack.Push(items[k]);
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
                    foreach (char c in t.Value) if (c == '\n') endL++;
                    for (int l = startL; l <= endL; l++) CommentLines.Add(l);
                }
            }
        }

        private void CheckBracketBalance()
        {
            int bd = 0, pd = 0, rd = 0;
            int bl = 0, bc = 0, pl = 0, pc = 0, rl = 0, rc = 0;
            foreach (var t in _tok)
            {
                if (t.IsPunct("{")) { if (bd++ == 0) { bl = t.Line; bc = t.Col; } }
                else if (t.IsPunct("}")) { if (--bd < 0) { Errors.Add($"{_fp}({t.Line},{t.Col}): error: Unexpected '}}'"); bd = 0; } }
                else if (t.IsPunct("(")) { if (pd++ == 0) { pl = t.Line; pc = t.Col; } }
                else if (t.IsPunct(")")) { if (--pd < 0) { Errors.Add($"{_fp}({t.Line},{t.Col}): error: Unexpected ')'"); pd = 0; } }
                else if (t.IsPunct("[")) { if (rd++ == 0) { rl = t.Line; rc = t.Col; } }
                else if (t.IsPunct("]")) { if (--rd < 0) { Errors.Add($"{_fp}({t.Line},{t.Col}): error: Unexpected ']'"); rd = 0; } }
            }
            if (bd > 0) Errors.Add($"{_fp}({bl},{bc}): error: Unclosed '{{'");
            if (pd > 0) Errors.Add($"{_fp}({pl},{pc}): error: Unclosed '('");
            if (rd > 0) Errors.Add($"{_fp}({rl},{rc}): error: Unclosed '['");
        }
        private void ParseTopLevel()
        {
            if (_idx >= _tok.Count) return;

            bool isExported = false, isAbstract = false;
            while (_idx < _tok.Count)
            {
                var t = _tok[_idx];
                if (t.IsWord("export")) { isExported = true; _idx++; }
                else if (t.IsWord("default") || t.IsWord("declare")) { _idx++; }
                else if (t.IsWord("abstract")) { isAbstract = true; _idx++; }
                else break;
            }
            if (_idx >= _tok.Count) return;

            var tok = _tok[_idx];

            if (tok.IsWord("import") && !isExported) { ParseImport(); return; }
            if (tok.IsWord("class")) { var ty = ParseClass(isExported, isAbstract); if (ty != null) Types.Add(ty); return; }
            if (tok.IsWord("interface") || tok.IsWord("enum")) { ParseTsType(isExported); return; }
            if (tok.IsWord("type")) { SkipTypeDecl(); return; }

            bool isAsync = false;
            if (tok.IsWord("async"))
            {
                isAsync = true; _idx++;
                if (_idx < _tok.Count) tok = _tok[_idx]; else return;
            }

            if (tok.IsWord("function")) { ParseFunction(isExported, isAsync); return; }

            if (tok.IsWord("const") || tok.IsWord("let") || tok.IsWord("var"))
            {
                int stmtEnd = FindStatementEnd(_idx);
                ExtractRequireFromRange(_idx, stmtEnd);
                _idx = stmtEnd;
                return;
            }

            // Unknown top-level token: skip to next statement boundary; always advance at least 1
            int se = FindStatementEnd(_idx);
            ExtractRequireFromRange(_idx, se);
            _idx = se > _idx ? se : _idx + 1;
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
            if (_idx >= _tok.Count) return;

            if (_tok[_idx].IsStr)
            {
                string m = UnquoteStr(_tok[_idx].Value);
                if (!string.IsNullOrEmpty(m)) Usings.Add(m);
                _idx++;
                if (_idx < _tok.Count && _tok[_idx].IsPunct(";")) _idx++;
                return;
            }

            while (_idx < _tok.Count && !_tok[_idx].IsWord("from") && !_tok[_idx].IsPunct(";"))
                _idx++;

            if (_idx < _tok.Count && _tok[_idx].IsWord("from"))
            {
                _idx++;
                if (_idx < _tok.Count && _tok[_idx].IsStr)
                {
                    string m = UnquoteStr(_tok[_idx].Value);
                    if (!string.IsNullOrEmpty(m)) Usings.Add(m);
                    _idx++;
                }
            }
            if (_idx < _tok.Count && _tok[_idx].IsPunct(";")) _idx++;
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

            bool isGeneric = false;
            if (_idx < _tok.Count && _tok[_idx].IsPunct("<"))
            { isGeneric = true; _idx = FindMatchingClose(_idx) + 1; }

            string? baseType = null;
            if (_idx < _tok.Count && _tok[_idx].IsWord("extends"))
            {
                _idx++;
                if (_idx < _tok.Count && _tok[_idx].IsIdent)
                {
                    baseType = _tok[_idx].Value; _idx++;
                    if (_idx < _tok.Count && _tok[_idx].IsPunct("<"))
                        _idx = FindMatchingClose(_idx) + 1;
                }
            }

            bool hasImplements = false;
            if (_idx < _tok.Count && _tok[_idx].IsWord("implements"))
            {
                hasImplements = true; _idx++;
                while (_idx < _tok.Count && !_tok[_idx].IsPunct("{")) _idx++;
            }

            while (_idx < _tok.Count && !_tok[_idx].IsPunct("{")) _idx++;

            if (_idx >= _tok.Count)
            {
                Errors.Add($"{_fp}({classLine},1): error: Missing body for class '{className}'");
                return null;
            }

            int openBrace = _idx;
            int closeBrace = FindMatchingBrace(openBrace);
            _idx = closeBrace + 1;

            var methods = new List<MethodDeclaration>();
            var constructors = new List<MethodDeclaration>();
            ParseClassBody(openBrace, closeBrace, methods, constructors);

            var baseTypes = baseType != null ? new List<string> { baseType } : new List<string>();
            var modifiers = isExported ? Modifier.Public : Modifier.None;

            return new TypeDeclaration(className, TypeKind.Class, modifiers,
                baseTypes, [], constructors, methods, [], [], classLine)
                .AsJavaScript(isExported: isExported, hasBaseClass: baseTypes.Count > 0,
                    isAbstract: isAbstract, isGeneric: isGeneric, hasImplements: hasImplements);
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
                    while (idx < closeBrace && (_tok[idx].IsIdent || _tok[idx].IsPunct("."))) idx++;
                    if (idx < closeBrace && _tok[idx].IsPunct("("))
                        idx = FindMatchingClose(idx) + 1;
                }
                if (idx >= closeBrace) break;

                if (_tok[idx].IsPunct(";")) { idx++; continue; }

                bool isStatic = false, isAsync = false, isAbstractM = false;
                bool isGenerator = false, isGetter = false, isSetter = false;

                while (idx < closeBrace)
                {
                    var t = _tok[idx];
                    if (t.IsWord("static")) { isStatic = true; idx++; }
                    else if (t.IsWord("async")) { isAsync = true; idx++; }
                    else if (t.IsWord("abstract")) { isAbstractM = true; idx++; }
                    else if (t.IsWord("override") || t.IsWord("readonly")
                          || t.IsWord("public") || t.IsWord("protected") || t.IsWord("private"))
                        idx++;
                    else break;
                }
                if (idx >= closeBrace) break;

                if (_tok[idx].IsPunct("*")) { isGenerator = true; idx++; }
                if (idx >= closeBrace) break;

                if (!isGenerator && (_tok[idx].IsWord("get") || _tok[idx].IsWord("set")))
                {
                    if (idx + 2 < closeBrace && _tok[idx + 1].IsIdent && _tok[idx + 2].IsPunct("("))
                    {
                        isGetter = _tok[idx].IsWord("get");
                        isSetter = _tok[idx].IsWord("set");
                        idx++;
                    }
                }
                if (idx >= closeBrace) break;

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
                        var parms = ParseParametersFromTokens(idx, closeP);
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
                                while (idx < closeBrace && !_tok[idx].IsPunct(";") && !_tok[idx].IsPunct("}")) idx++;
                                if (idx < closeBrace && _tok[idx].IsPunct(";")) idx++;
                            }
                            var mod2 = Modifier.Public;
                            if (isStatic) mod2 |= Modifier.Static;
                            if (isAsync) mod2 |= Modifier.Async;
                            var m2 = NewMethod(memberName, mod2, [], null, parms, memberLine, arrowStmts)
                                .AsJavaScript(isArrow: true);
                            if (memberName == "constructor") constructors.Add(m2); else methods.Add(m2);
                            continue;
                        }
                    }
                    while (idx < closeBrace && !_tok[idx].IsPunct(";") && !_tok[idx].IsPunct("}")) idx++;
                    if (idx < closeBrace && _tok[idx].IsPunct(";")) idx++;
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
                        while (idx < closeBrace && !_tok[idx].IsPunct(";") && !_tok[idx].IsPunct("}")) idx++;
                    }
                    if (idx < closeBrace && _tok[idx].IsPunct(";")) idx++;
                    continue;
                }

                // Regular method: name(...) { ... }
                if (idx < closeBrace && _tok[idx].IsPunct("("))
                {
                    int closeP = FindMatchingClose(idx);
                    var parms = ParseParametersFromTokens(idx, closeP);
                    idx = closeP + 1;

                    if (idx < closeBrace && _tok[idx].IsPunct(":"))
                    { idx++; SkipTsTypeAt(ref idx, closeBrace); }

                    if (idx >= closeBrace || _tok[idx].IsPunct(";")) { if (idx < closeBrace) idx++; continue; }

                    if (idx < closeBrace && _tok[idx].IsPunct("{"))
                    {
                        int bc = FindMatchingBrace(idx);
                        var bStmts = ParseBodyStatements(idx, bc, isInMethod: true);
                        Statements.AddRange(bStmts);
                        idx = bc + 1;

                        var mod = Modifier.Public;
                        if (isStatic) mod |= Modifier.Static;
                        if (isAsync) mod |= Modifier.Async;
                        if (isAbstractM) mod |= Modifier.Abstract;

                        var meth = NewMethod(memberName, mod, [], null, parms, memberLine, bStmts)
                            .AsJavaScript(isGenerator: isGenerator, isGetter: isGetter, isSetter: isSetter);
                        if (memberName == "constructor") constructors.Add(meth); else methods.Add(meth);
                    }
                    continue;
                }

                while (idx < closeBrace && !_tok[idx].IsPunct(";") && !_tok[idx].IsPunct("}")) idx++;
                if (idx < closeBrace && _tok[idx].IsPunct(";")) idx++;
            }
        }

        private void SkipMemberRemainder(ref int idx, int closeBrace)
        {
            if (idx < closeBrace && _tok[idx].IsPunct("("))
            {
                idx = FindMatchingClose(idx) + 1;
                if (idx < closeBrace && _tok[idx].IsPunct(":")) { idx++; SkipTsTypeAt(ref idx, closeBrace); }
                if (idx < closeBrace && _tok[idx].IsPunct("{")) idx = FindMatchingBrace(idx) + 1;
                else if (idx < closeBrace && _tok[idx].IsPunct(";")) idx++;
            }
            else
            {
                while (idx < closeBrace && !_tok[idx].IsPunct(";") && !_tok[idx].IsPunct("}")) idx++;
                if (idx < closeBrace && _tok[idx].IsPunct(";")) idx++;
            }
        }
        private void ParseFunction(bool isExported, bool isAsync)
        {
            _idx++;
            bool isGenerator = false;
            if (_idx < _tok.Count && _tok[_idx].IsPunct("*")) { isGenerator = true; _idx++; }

            if (_idx >= _tok.Count || !_tok[_idx].IsIdent) { SkipToBlockEnd(); return; }
            _idx++;

            if (_idx < _tok.Count && _tok[_idx].IsPunct("<"))
                _idx = FindMatchingClose(_idx) + 1;

            if (_idx >= _tok.Count || !_tok[_idx].IsPunct("(")) { SkipToBlockEnd(); return; }

            int closeP = FindMatchingClose(_idx);
            _idx = closeP + 1;

            if (_idx < _tok.Count && _tok[_idx].IsPunct(":"))
            { _idx++; SkipTsTypeGlobal(); }

            if (_idx >= _tok.Count || !_tok[_idx].IsPunct("{")) return;

            int openBrace = _idx;
            int closeBrace = FindMatchingBrace(openBrace);
            _idx = closeBrace + 1;

            var bStmts = ParseBodyStatements(openBrace, closeBrace, isInMethod: true);
            Statements.AddRange(bStmts);
        }

        private void ParseTsType(bool isExported)
        {
            TypeKind kind = _tok[_idx].IsWord("interface") ? TypeKind.Interface : TypeKind.Enum;
            int typeLine = _tok[_idx].Line;
            _idx++;

            if (_idx >= _tok.Count || !_tok[_idx].IsIdent) { SkipToBlockOrSemi(); return; }

            string typeName = _tok[_idx].Value; _idx++;

            if (_idx < _tok.Count && _tok[_idx].IsPunct("<"))
                _idx = FindMatchingClose(_idx) + 1;

            while (_idx < _tok.Count && (_tok[_idx].IsWord("extends") || _tok[_idx].IsWord("implements")))
            {
                _idx++;
                while (_idx < _tok.Count && !_tok[_idx].IsPunct("{") && !_tok[_idx].IsPunct(";")) _idx++;
            }

            if (_idx < _tok.Count && _tok[_idx].IsPunct("{"))
            { int cb = FindMatchingBrace(_idx); _idx = cb + 1; }
            else if (_idx < _tok.Count && _tok[_idx].IsPunct(";"))
                _idx++;

            var modifiers = isExported ? Modifier.Public : Modifier.None;
            Types.Add(new TypeDeclaration(typeName, kind, modifiers, [], [], [], [], [], [], typeLine)
                .AsJavaScript(isExported: isExported));
        }

        private List<StatementInfo> ParseBodyStatements(int openBrace, int closeBrace, bool isInMethod)
        {
            var stmts = new List<StatementInfo>();
            int idx = openBrace + 1;

            while (idx < closeBrace && idx < _tok.Count)
            {
                var t = _tok[idx];

                if (t.IsWord("catch"))
                {
                    int catchLine = t.Line; idx++;
                    if (idx < closeBrace && _tok[idx].IsPunct("("))
                        idx = FindMatchingClose(idx) + 1;
                    bool hasRethrow = false;
                    if (idx < closeBrace && _tok[idx].IsPunct("{"))
                    {
                        int catchClose = FindMatchingBrace(idx);
                        hasRethrow = ScanForRethrow(idx + 1, catchClose);
                    }
                    stmts.Add(new JavaScriptStatementInfo("catch", [], null, null, [], catchLine, isInMethod)
                    { HasRethrow = hasRethrow, IsErrorHandler = true, IsGenericErrorHandler = true });
                    continue;
                }

                if (t.IsWord("try"))
                {
                    stmts.Add(new JavaScriptStatementInfo("try", [], null, null, [], t.Line, isInMethod));
                    idx++; continue;
                }

                if (t.IsWord("for"))
                {
                    int forLine = t.Line; idx++;
                    if (idx < closeBrace && _tok[idx].IsPunct("("))
                    {
                        int pe = FindMatchingClose(idx);
                        bool hasOf = HasWordInRange(idx + 1, pe, "of");
                        bool hasIn = HasWordInRange(idx + 1, pe, "in");
                        if (hasOf)
                            stmts.Add(new JavaScriptStatementInfo("for-of", ["for"], null, null, [], forLine, isInMethod));
                        else if (hasIn)
                            stmts.Add(new JavaScriptStatementInfo("for-in", ["for"], null, null, [], forLine, isInMethod));
                        idx = pe + 1;
                    }
                    continue;
                }

                if (t.IsWord("debugger"))
                {
                    stmts.Add(new JavaScriptStatementInfo("call", ["debugger"], null, "debugger", [], t.Line, isInMethod));
                    idx++; continue;
                }

                if (t.IsWord("throw"))
                {
                    int throwLine = t.Line; idx++;
                    string? throwType = null;
                    if (idx < closeBrace && _tok[idx].IsWord("new"))
                    {
                        idx++;
                        if (idx < closeBrace && _tok[idx].IsIdent) throwType = _tok[idx].Value;
                    }
                    stmts.Add(new JavaScriptStatementInfo("throw", [], throwType, null, [], throwLine, isInMethod));
                    idx = SkipToSemi(idx, closeBrace);
                    continue;
                }

                if (t.IsWord("return") && idx + 1 < closeBrace && _tok[idx + 1].IsWord("await"))
                {
                    int awaitLine = t.Line; idx += 2;
                    EmitAwait(stmts, idx, closeBrace, isInMethod, awaitLine);
                    idx = SkipToSemi(idx, closeBrace);
                    continue;
                }

                if (t.IsWord("const") || t.IsWord("let") || t.IsWord("var"))
                {
                    string kw = t.Value; int declLine = t.Line; idx++;
                    string? varName = null;
                    if (idx < closeBrace && _tok[idx].IsIdent) { varName = _tok[idx].Value; idx++; }
                    else if (idx < closeBrace && (_tok[idx].IsPunct("{") || _tok[idx].IsPunct("[")))
                        idx = FindMatchingClose(idx) + 1;

                    if (idx < closeBrace && _tok[idx].IsPunct(":"))
                    { idx++; SkipTsTypeAt(ref idx, closeBrace); }

                    if (varName != null)
                        stmts.Add(new JavaScriptStatementInfo("declaration", [kw], null, varName, [], declLine, isInMethod));

                    if (idx < closeBrace && _tok[idx].IsPunct("="))
                    {
                        idx++;
                        if (idx < closeBrace && _tok[idx].IsWord("await"))
                        { int awaitLine = _tok[idx].Line; idx++; EmitAwait(stmts, idx, closeBrace, isInMethod, awaitLine); }
                        else
                        { TryEmitCallAt(stmts, idx, closeBrace, isInMethod, declLine, out _); }
                    }
                    idx = SkipToSemi(idx, closeBrace);
                    continue;
                }

                if (t.IsWord("await") && idx + 1 < closeBrace)
                {
                    int awaitLine = t.Line; idx++;
                    EmitAwait(stmts, idx, closeBrace, isInMethod, awaitLine);
                    idx = SkipToSemi(idx, closeBrace);
                    continue;
                }

                if (t.IsIdent && !IsCtrlKw(t.Value))
                {
                    if (TryEmitCallAt(stmts, idx, closeBrace, isInMethod, t.Line, out int afterCall))
                        idx = afterCall;
                    else idx++;
                    continue;
                }

                idx++;
            }

            return stmts;
        }

        private void EmitAwait(List<StatementInfo> stmts, int idx, int endIdx, bool isInMethod, int line)
        {
            string? typeName = null, memberName = null;
            if (idx < endIdx && _tok[idx].IsIdent)
            {
                var parts = new List<string> { _tok[idx].Value };
                int j = idx + 1;
                while (j + 1 < endIdx && _tok[j].IsPunct(".") && _tok[j + 1].IsIdent)
                { parts.Add(_tok[j + 1].Value); j += 2; }
                if (j < endIdx && _tok[j].IsPunct("("))
                {
                    memberName = parts[^1];
                    typeName = parts.Count > 1 ? string.Join(".", parts[..^1]) : null;
                }
            }

            if (memberName != null && !IsCallKw(memberName))
            {
                stmts.Add(new JavaScriptStatementInfo("await", [], typeName, memberName, [], line, isInMethod));
                stmts.Add(new JavaScriptStatementInfo("call", [], typeName, memberName, [], line, isInMethod));
            }
            else
                stmts.Add(new JavaScriptStatementInfo("await", [], null, memberName, [], line, isInMethod));
        }

        private bool TryEmitCallAt(List<StatementInfo> stmts, int idx, int endIdx,
            bool isInMethod, int line, out int newIdx)
        {
            newIdx = idx + 1;
            if (idx >= endIdx || !_tok[idx].IsIdent) return false;

            var parts = new List<string> { _tok[idx].Value };
            int j = idx + 1;
            while (j + 1 < endIdx && _tok[j].IsPunct(".") && _tok[j + 1].IsIdent)
            { parts.Add(_tok[j + 1].Value); j += 2; }

            if (j >= endIdx || !_tok[j].IsPunct("(")) return false;

            string memberName = parts[^1];
            string? typeName = parts.Count > 1 ? string.Join(".", parts[..^1]) : null;

            if (!IsCallKw(memberName))
                stmts.Add(new JavaScriptStatementInfo("call", [], typeName, memberName, [], line, isInMethod));

            newIdx = FindMatchingClose(j) + 1;
            return true;
        }
        private List<ParameterDeclaration> ParseParametersFromTokens(int openParen, int closeParen)
        {
            var result = new List<ParameterDeclaration>();
            int idx = openParen + 1;

            while (idx < closeParen && idx < _tok.Count)
            {
                if (_tok[idx].IsPunct(",")) { idx++; continue; }

                bool isVariadic = false;
                if (_tok[idx].IsPunct("...")) { isVariadic = true; idx++; }

                if (idx >= closeParen || !_tok[idx].IsIdent) { idx++; continue; }

                string name = _tok[idx].Value; idx++;

                if (idx < closeParen && _tok[idx].IsPunct("?")) idx++;

                TypeReference? typeRef = null;
                if (idx < closeParen && _tok[idx].IsPunct(":"))
                { idx++; typeRef = ParseTypeAnnotationAt(ref idx, closeParen); }

                bool hasDefault = false;
                if (idx < closeParen && _tok[idx].IsPunct("="))
                {
                    hasDefault = true; idx++;
                    int depth = 0;
                    while (idx < closeParen)
                    {
                        if (_tok[idx].IsPunct("(") || _tok[idx].IsPunct("[") || _tok[idx].IsPunct("{")) depth++;
                        else if (_tok[idx].IsPunct(")") || _tok[idx].IsPunct("]") || _tok[idx].IsPunct("}"))
                        { if (depth == 0) break; depth--; }
                        else if (_tok[idx].IsPunct(",") && depth == 0) break;
                        idx++;
                    }
                }

                result.Add(new ParameterDeclaration(name, typeRef, isVariadic, false, hasDefault, 0));
            }

            return result;
        }

        private TypeReference? ParseTypeAnnotationAt(ref int idx, int endIdx)
        {
            if (idx >= endIdx || !_tok[idx].IsIdent) return null;
            string name = _tok[idx].Value; idx++;

            if (idx < endIdx && _tok[idx].IsPunct("<"))
                idx = FindMatchingClose(idx) + 1;

            while (idx < endIdx && _tok[idx].IsPunct("[") && idx + 1 < endIdx && _tok[idx + 1].IsPunct("]"))
                idx += 2;

            while (idx < endIdx && (_tok[idx].IsPunct("|") || _tok[idx].IsPunct("&")))
            {
                idx++;
                if (idx < endIdx && _tok[idx].IsIdent) { idx++; if (idx < endIdx && _tok[idx].IsPunct("<")) idx = FindMatchingClose(idx) + 1; }
            }

            return new TypeReference(name, null, [], name);
        }

        private bool ScanForRethrow(int start, int end)
        {
            int depth = 0;
            for (int i = start; i < end && i < _tok.Count; i++)
            {
                var t = _tok[i];
                if (t.IsPunct("{")) depth++;
                else if (t.IsPunct("}")) { depth--; if (depth < 0) return false; }
                else if (t.IsWord("throw")) return true;
            }
            return false;
        }

        private void SkipTsTypeAt(ref int idx, int endIdx)
        {
            int depth = 0;
            while (idx < endIdx && idx < _tok.Count)
            {
                var t = _tok[idx];
                if (t.IsPunct("(") || t.IsPunct("[") || t.IsPunct("<")) { depth++; idx++; continue; }
                if (t.IsPunct(">") || t.IsPunct(")") || t.IsPunct("]"))
                { if (depth > 0) { depth--; idx++; continue; } break; }
                if (t.IsPunct("{") && depth == 0) break;
                if (t.IsPunct("{")) { depth++; idx++; continue; }
                if (t.IsPunct("}")) { if (depth > 0) { depth--; idx++; continue; } break; }
                if ((t.IsPunct(";") || t.IsPunct(",") || t.IsPunct("=")) && depth == 0) break;
                idx++;
            }
        }

        private void SkipTsTypeGlobal()
        {
            int depth = 0;
            while (_idx < _tok.Count)
            {
                var t = _tok[_idx];
                if (t.IsPunct("(") || t.IsPunct("[") || t.IsPunct("<")) { depth++; _idx++; continue; }
                if (t.IsPunct(">") || t.IsPunct(")") || t.IsPunct("]"))
                { if (depth > 0) { depth--; _idx++; continue; } break; }
                if ((t.IsPunct("{") || t.IsPunct(";")) && depth == 0) break;
                if (t.IsPunct("{")) { depth++; _idx++; continue; }
                if (t.IsPunct("}")) { if (depth > 0) { depth--; _idx++; continue; } break; }
                _idx++;
            }
        }

        private int FindMatchingBrace(int openIdx)
        {
            int depth = 1, idx = openIdx + 1;
            while (idx < _tok.Count && depth > 0)
            {
                if (_tok[idx].IsPunct("{")) depth++;
                else if (_tok[idx].IsPunct("}")) depth--;
                idx++;
            }
            return idx - 1;
        }

        private int FindMatchingClose(int openIdx)
        {
            if (openIdx >= _tok.Count) return openIdx;
            string open = _tok[openIdx].Value;
            string close = open == "(" ? ")" : open == "[" ? "]" : open == "<" ? ">" : "}";
            int depth = 1, idx = openIdx + 1;
            while (idx < _tok.Count && depth > 0)
            {
                if (_tok[idx].IsPunct(open)) depth++;
                else if (_tok[idx].IsPunct(close)) depth--;
                idx++;
            }
            return idx - 1;
        }

        private int FindStatementEnd(int startIdx)
        {
            int depth = 0, idx = startIdx;
            while (idx < _tok.Count)
            {
                var t = _tok[idx];
                if (t.IsPunct("(") || t.IsPunct("[") || t.IsPunct("{")) depth++;
                else if (t.IsPunct(")") || t.IsPunct("]")) { if (depth > 0) depth--; }
                else if (t.IsPunct("}")) { if (depth > 0) depth--; else return idx; }
                else if (t.IsPunct(";") && depth == 0) return idx + 1;
                idx++;
            }
            return idx;
        }

        private int SkipToSemi(int idx, int endIdx)
        {
            int depth = 0;
            while (idx < endIdx && idx < _tok.Count)
            {
                var t = _tok[idx];
                if (t.IsPunct("(") || t.IsPunct("[") || t.IsPunct("{")) depth++;
                else if (t.IsPunct(")") || t.IsPunct("]") || t.IsPunct("}"))
                { if (depth == 0) return idx; depth--; }
                else if (t.IsPunct(";") && depth == 0) return idx + 1;
                idx++;
            }
            return idx;
        }

        private void SkipToSemiConsuming()
        {
            int depth = 0;
            while (_idx < _tok.Count)
            {
                var t = _tok[_idx];
                if (t.IsPunct("{") || t.IsPunct("(") || t.IsPunct("[")) { depth++; _idx++; }
                else if (t.IsPunct("}") || t.IsPunct(")") || t.IsPunct("]"))
                { if (depth > 0) { depth--; _idx++; } else break; }
                else if (t.IsPunct(";") && depth == 0) { _idx++; return; }
                else _idx++;
            }
        }

        // Skip a TypeScript 'type Foo = ...' declaration (may contain { ... } objects)
        private void SkipTypeDecl()
        {
            // _idx currently points to 'type'; skip name and = ... ; (depth-aware)
            int depth = 0;
            while (_idx < _tok.Count)
            {
                var t = _tok[_idx];
                if (t.IsPunct("{") || t.IsPunct("(") || t.IsPunct("[")) { depth++; _idx++; }
                else if (t.IsPunct("}") || t.IsPunct(")") || t.IsPunct("]"))
                { if (depth > 0) { depth--; _idx++; } else break; }
                else if (t.IsPunct(";") && depth == 0) { _idx++; return; }
                else _idx++;
            }
        }

        private void SkipToBlockEnd()
        {
            while (_idx < _tok.Count && !_tok[_idx].IsPunct("{")) _idx++;
            if (_idx < _tok.Count && _tok[_idx].IsPunct("{")) _idx = FindMatchingBrace(_idx) + 1;
        }

        private void SkipToBlockOrSemi()
        {
            while (_idx < _tok.Count && !_tok[_idx].IsPunct("{") && !_tok[_idx].IsPunct(";")) _idx++;
            if (_idx < _tok.Count && _tok[_idx].IsPunct("{")) _idx = FindMatchingBrace(_idx) + 1;
            else if (_idx < _tok.Count && _tok[_idx].IsPunct(";")) _idx++;
        }

        private bool HasWordInRange(int start, int end, string word)
        {
            for (int i = start; i < end && i < _tok.Count; i++)
                if (_tok[i].IsWord(word)) return true;
            return false;
        }

        private static string UnquoteStr(string s)
        {
            if (s.Length < 2) return s;
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
            List<ParameterDeclaration> parms, int line, List<StatementInfo> stmts)
        {
            var m = new MethodDeclaration(name, mods, decorators, returnType, parms, line);
            m.Statements = stmts;
            return m;
        }
    }
}
