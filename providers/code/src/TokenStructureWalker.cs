// Shares the block and call nesting walk that token-based parsers use to structure statements.

namespace Cop.Providers.SourceModel;

/// <summary>
/// Walks a token range of a body, tracking brace blocks and open call ranges so a language parser can
/// attach control-flow statements and parsed calls to their enclosing statements.
/// </summary>
/// <typeparam name="TToken">The language token type.</typeparam>
public abstract class TokenStructureWalker<TToken>
{
    private readonly Stack<StatementInfo?> _scopes = new Stack<StatementInfo?>();
    private readonly Stack<(int End, StatementInfo Call)> _callScopes = new Stack<(int End, StatementInfo Call)>();
    private readonly Dictionary<int, int> _callStarts = new Dictionary<int, int>();

    /// <summary>
    /// Initializes a walker over a token range.
    /// </summary>
    /// <param name="tokens">The tokens of the source file.</param>
    /// <param name="start">The first token index of the range.</param>
    /// <param name="end">The exclusive end token index of the range.</param>
    /// <param name="statements">The statements of the range; call statements are replaced by the walk.</param>
    /// <param name="inMethod">True when the range is a method body.</param>
    protected TokenStructureWalker(List<TToken> tokens, int start, int end, List<StatementInfo> statements, bool inMethod)
    {
        Tokens = tokens;
        Start = start;
        End = end;
        Statements = statements;
        InMethod = inMethod;
    }

    /// <summary>Gets the tokens of the source file.</summary>
    protected List<TToken> Tokens { get; }

    /// <summary>Gets the first token index of the walked range.</summary>
    protected int Start { get; }

    /// <summary>Gets the exclusive end token index of the walked range.</summary>
    protected int End { get; }

    /// <summary>Gets the statements of the walked range.</summary>
    protected List<StatementInfo> Statements { get; }

    /// <summary>Gets a value indicating whether the walked range is a method body.</summary>
    protected bool InMethod { get; }

    /// <summary>Gets the statement owning the current brace block.</summary>
    protected StatementInfo? Parent { get; private set; }

    /// <summary>Gets or sets the control statement that will own the next brace block.</summary>
    protected StatementInfo? Pending { get; set; }

    /// <summary>
    /// Replaces the range's call statements with freshly parsed ones, nests every statement under its
    /// enclosing statement, and sorts the statements by line.
    /// </summary>
    public void Walk()
    {
        Statements.RemoveAll(s => s.Kind == "call");
        for (int i = Start; i < End; i++)
        {
            while (_callScopes.Count > 0 && _callScopes.Peek().End <= i)
            {
                _callScopes.Pop();
            }

            TToken token = Tokens[i];
            if (IsComment(token))
            {
                continue;
            }

            string value = ValueOf(token);
            if (value == "{")
            {
                _scopes.Push(Parent);
                Parent = Pending ?? Parent;
                Pending = null;
                continue;
            }
            if (value == "}")
            {
                if (_scopes.Count > 0)
                {
                    Parent = _scopes.Pop();
                }

                Pending = null;
                continue;
            }
            VisitToken(i);
        }
        Statements.Sort((a, b) => a.Line.CompareTo(b.Line));
    }

    /// <summary>Returns true when the token is a comment.</summary>
    /// <param name="token">The token to test.</param>
    /// <returns>True for comment tokens.</returns>
    protected abstract bool IsComment(TToken token);

    /// <summary>Returns true when the token is a string literal or comment, whose text never nests brackets.</summary>
    /// <param name="token">The token to test.</param>
    /// <returns>True for string literal and comment tokens.</returns>
    protected abstract bool IsStringOrComment(TToken token);

    /// <summary>Returns the source text of a token.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The token text.</returns>
    protected abstract string ValueOf(TToken token);

    /// <summary>Processes a non-comment token that is not a brace.</summary>
    /// <param name="index">The token index.</param>
    protected abstract void VisitToken(int index);

    /// <summary>Attaches a statement to a parent statement.</summary>
    /// <param name="statement">The child statement.</param>
    /// <param name="parent">The parent statement, or <c>null</c> for a top-level statement.</param>
    protected static void Attach(StatementInfo statement, StatementInfo? parent)
    {
        statement.Parent = parent;
        if (parent is not null && !parent.MutableChildren.Contains(statement))
        {
            parent.MutableChildren.Add(statement);
        }
    }

    /// <summary>
    /// Adds a parsed call nested under the innermost enclosing call, else the pending control statement,
    /// else the block owner, and opens its argument range for nested calls.
    /// </summary>
    /// <param name="call">The call statement.</param>
    /// <param name="close">The index of the call's closing parenthesis.</param>
    /// <param name="chainStart">The token index where a call chained onto this call starts.</param>
    protected void AddCall(StatementInfo call, int close, int chainStart)
    {
        Attach(call, _callScopes.Count > 0 ? _callScopes.Peek().Call : Pending ?? Parent);
        Statements.Add(call);
        _callScopes.Push((close, call));
        _callStarts[close] = chainStart;
    }

    /// <summary>Gets the chain start recorded for a call whose closing parenthesis is at an index.</summary>
    /// <param name="close">The candidate closing parenthesis index.</param>
    /// <param name="chainStart">The recorded chain start.</param>
    /// <returns>True when a call closes at the index.</returns>
    protected bool TryGetCallStart(int close, out int chainStart) => _callStarts.TryGetValue(close, out chainStart);

    /// <summary>Finds the token closing the bracket opened at <paramref name="start"/>.</summary>
    /// <param name="start">The index of the opening bracket.</param>
    /// <param name="end">The exclusive search end.</param>
    /// <param name="open">The opening bracket text.</param>
    /// <param name="close">The closing bracket text.</param>
    /// <returns>The closing bracket index, or <paramref name="end"/> when unmatched.</returns>
    protected int Matching(int start, int end, string open, string close)
    {
        int depth = 0;
        for (int i = start; i < end; i++)
        {
            if (IsStringOrComment(Tokens[i]))
            {
                continue;
            }

            string value = ValueOf(Tokens[i]);
            if (value == open)
            {
                depth++;
            }

            if (value == close && --depth == 0)
            {
                return i;
            }
        }
        return end;
    }

    /// <summary>Splits a token range into top-level comma-separated arguments.</summary>
    /// <param name="start">The first argument token index.</param>
    /// <param name="end">The exclusive end index.</param>
    /// <returns>The argument token ranges.</returns>
    protected List<(int Start, int End)> Arguments(int start, int end)
    {
        List<(int, int)> result = new List<(int, int)>();
        int depth = 0, first = start;
        for (int i = start; i < end; i++)
        {
            if (IsStringOrComment(Tokens[i]))
            {
                continue;
            }

            string value = ValueOf(Tokens[i]);
            if (value is "(" or "[" or "{")
            {
                depth++;
            }

            if (value is ")" or "]" or "}")
            {
                depth--;
            }

            if (value == "," && depth == 0)
            {
                result.Add((first, i));
                first = i + 1;
            }
        }
        if (first < end)
        {
            result.Add((first, end));
        }

        return result;
    }

    /// <summary>Concatenates the text of a token range.</summary>
    /// <param name="start">The first token index.</param>
    /// <param name="end">The exclusive end index.</param>
    /// <returns>The concatenated token text.</returns>
    protected string Text(int start, int end) => string.Concat(Tokens.GetRange(start, end - start).Select(ValueOf));
}
