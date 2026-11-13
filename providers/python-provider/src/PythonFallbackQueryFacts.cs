// Conservative query facts from lexer tokens when no Python interpreter is available.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal static class PythonFallbackQueryFacts
{
    private sealed record SuiteLine(List<Tok> Tokens, List<SuiteLine> Body);
    [Flags]
    private enum Completion { Next = 1, Rethrow = 2, Exit = 4, Unknown = 8 }

    internal static void Attach(SourceFile file, List<Tok> tokens)
    {
        int position = 0;
        List<SuiteLine> suite = ParseSuite(tokens, ref position);
        Dictionary<int, List<PythonStatementInfo>> statements = file.Statements
            .OfType<PythonStatementInfo>().GroupBy(s => s.Line).ToDictionary(g => g.Key, g => g.ToList());
        Visit(suite, statements, unsafeCleanup: false);
    }

    private static List<SuiteLine> ParseSuite(List<Tok> tokens, ref int position)
    {
        List<SuiteLine> result = [];
        while (position < tokens.Count)
        {
            TK kind = tokens[position].Kind;
            if (kind is TK.Dedent or TK.Eof)
            {
                position++;
                break;
            }
            if (kind == TK.Newline)
            {
                position++;
                continue;
            }
            if (kind == TK.Indent)
            {
                position++;
                List<SuiteLine> body = ParseSuite(tokens, ref position);
                if (result.Count > 0)
                {
                    result[^1].Body.AddRange(body);
                }
                continue;
            }
            List<Tok> header = [];
            while (position < tokens.Count && tokens[position].Kind is not
                (TK.Newline or TK.Indent or TK.Dedent or TK.Eof))
            {
                header.Add(tokens[position++]);
            }
            result.Add(new(header, []));
        }
        return result;
    }

    private static void Visit(List<SuiteLine> suite,
        Dictionary<int, List<PythonStatementInfo>> statements, bool unsafeCleanup)
    {
        for (int i = 0; i < suite.Count; i++)
        {
            SuiteLine node = suite[i];
            List<Tok> header = node.Tokens;
            if (header.Count == 0)
            {
                continue;
            }
            bool localUnsafe = unsafeCleanup;
            if (header[0].Is("except"))
            {
                for (int next = i + 1; next < suite.Count && Starts(suite[next], "except", "else", "finally"); next++)
                {
                    if (Starts(suite[next], "finally") && Flow(suite[next].Body, null) != Completion.Next)
                    {
                        localUnsafe = true;
                    }
                }
            }
            if (statements.TryGetValue(header[0].Line, out List<PythonStatementInfo>? lineStatements))
            {
                foreach (PythonStatementInfo statement in lineStatements)
                {
                    if (statement.Kind == "catch")
                    {
                        int aliasIndex = header.FindIndex(t => t.Is("as"));
                        string? alias = aliasIndex >= 0 && aliasIndex + 1 < header.Count
                            ? header[aliasIndex + 1].Text : null;
                        bool rebound = alias is not null && AllTokens(node.Body).Any(t =>
                            t.Kind is TK.Eq or TK.Walrus || t.Is("del") || t.Is("import") || t.Is("global") || t.Is("nonlocal")
                            || t.Is("def") || t.Is("class"));
                        statement.IsCancellationHandler = statement.TypeName is
                            "asyncio.CancelledError" or "asyncio.exceptions.CancelledError" or "concurrent.futures.CancelledError";
                        statement.IsOriginalExceptionAlwaysRethrown = !localUnsafe && !rebound
                            && !header.Any(t => t.Kind == TK.Star)
                            && Flow(node.Body, alias) == Completion.Rethrow;
                        statement.IsCallerCancellationPreserved = statement.IsOriginalExceptionAlwaysRethrown;
                    }
                    if (statement.Kind == "call")
                    {
                        int start = header.FindIndex(t => t.Col == statement.Column);
                        int end = CallEnd(header, start);
                        statement.IsEnvironmentVariableRead = statement.TypeName == "os" && statement.MemberName == "getenv"
                            || statement.TypeName == "os.environ" && statement.MemberName == "get";
                        statement.IsEnvironmentVariableAccess = statement.IsEnvironmentVariableRead
                            || statement.TypeName == "os" && statement.MemberName is "putenv" or "unsetenv"
                            || statement.TypeName == "os.environ" && statement.MemberName is
                                "pop" or "setdefault" or "update" or "clear" or "items" or "keys" or "values" or "copy";
                        bool with = header[0].Is("with") || header.Count > 1 && header[0].Is("async") && header[1].Is("with");
                        int expressionStart = with ? header[0].Is("async") ? 2 : 1 : -1;
                        // Only the first complete context expression is supported in simpler analysis.
                        statement.IsUsingResource = with && start == expressionStart && end > start
                            && (end + 1 == header.Count - 1 && header[end + 1].Kind == TK.Colon
                                || end + 3 == header.Count - 1 && header[end + 1].Is("as")
                                    && header[end + 2].Kind == TK.Name && header[end + 3].Kind == TK.Colon);
                        statement.IsOwnershipTransferred = !localUnsafe && header[0].Is("return")
                            && start == 1 && end == header.Count - 1;
                    }
                }
            }
            bool definition = Starts(node, "def", "class") || header.Count > 1 && header[0].Is("async") && header[1].Is("def");
            bool nestedUnsafe = localUnsafe || Starts(node, "with", "try")
                || header.Count > 1 && header[0].Is("async") && header[1].Is("with");
            Visit(node.Body, statements, definition ? false : nestedUnsafe);
        }
    }

    private static bool Starts(SuiteLine node, params string[] names) =>
        node.Tokens.Count > 0 && names.Any(node.Tokens[0].Is);

    private static IEnumerable<Tok> AllTokens(List<SuiteLine> body) =>
        body.SelectMany(line => line.Tokens.Concat(AllTokens(line.Body)));

    private static Completion Flow(List<SuiteLine> body, string? alias)
    {
        Completion result = Completion.Next;
        for (int i = 0; i < body.Count && result.HasFlag(Completion.Next); i++)
        {
            SuiteLine line = body[i];
            List<Tok> tokens = line.Tokens;
            Completion next = Completion.Unknown;
            if (tokens.Count == 0)
            {
                continue;
            }
            if (tokens.Any(t => t.Kind is TK.Semi or TK.Walrus || t.Is("await") || t.Is("yield")))
            {
                next = Completion.Unknown;
            }
            else if (tokens[0].Is("raise"))
            {
                next = tokens.Count == 1 || tokens.Count == 2 && alias is not null && tokens[1].Is(alias)
                    ? Completion.Rethrow : Completion.Exit;
            }
            else if (tokens[0].Is("if") || tokens[0].Is("elif"))
            {
                next = tokens[^1].Kind == TK.Colon && line.Body.Count > 0
                    ? Flow(line.Body, alias) : Completion.Unknown;
                if (i + 1 < body.Count && Starts(body[i + 1], "else") && body[i + 1].Tokens.Count == 2)
                {
                    next |= Flow(body[++i].Body, alias);
                }
                else
                {
                    next |= Completion.Next;
                }
            }
            else if (tokens[0].Is("return") || tokens[0].Is("break") || tokens[0].Is("continue"))
            {
                next = Completion.Exit;
            }
            else if (line.Body.Count == 0 && !tokens.Any(t => t.Kind == TK.Colon)
                && !tokens[0].Is("else") && !tokens[0].Is("elif") && !tokens[0].Is("finally"))
            {
                next = Completion.Next;
            }
            result = (result & ~Completion.Next) | next;
        }
        return result;
    }

    private static int CallEnd(List<Tok> tokens, int start)
    {
        if (start < 0)
        {
            return -1;
        }
        int i = start + 1;
        while (i + 1 < tokens.Count && tokens[i].Kind == TK.Dot && tokens[i + 1].Kind == TK.Name)
        {
            i += 2;
        }
        if (i >= tokens.Count || tokens[i].Kind != TK.LParen)
        {
            return -1;
        }
        int depth = 0;
        for (; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TK.LParen)
            {
                depth++;
            }
            else if (tokens[i].Kind == TK.RParen && --depth == 0)
            {
                return i;
            }
        }
        return -1;
    }
}
