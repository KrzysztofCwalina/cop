// Selects Python AST or source-only parsing and tracks toolchain cache identity.

using System.Text.Json;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>
/// Parses Python through an installed interpreter's standard-library AST without executing
/// target code. When unavailable, uses the explicitly warned simpler lexer/recursive-descent parser.
/// </summary>
public class PythonSourceParser : ISourceParser
{
    private readonly PythonToolchain _toolchain;

    /// <summary>
    /// Creates a Python parser with the requested toolchain discovery policy.
    /// </summary>
    public PythonSourceParser(
        PythonAnalysisBackend backend = PythonAnalysisBackend.PreferNative,
        string? searchPath = null) => _toolchain = new(backend, searchPath);

    /// <inheritdoc />
    public override string GetCacheIdentity(string filePath) => base.GetCacheIdentity(filePath) + ":" + _toolchain.CacheIdentity;

    /// <inheritdoc />
    public override bool CanCache(string filePath) => false;

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions => [".py", ".pyi"];
    /// <inheritdoc />
    public override string Language => "python";

    /// <inheritdoc />
    public override async ValueTask<SourceFile?> ParseAsync(
        string filePath, string sourceText, CancellationToken cancellationToken = default)
    {
        using JsonDocument? analysis = await _toolchain.AnalyzeAsync(
            new(filePath, sourceText), cancellationToken).ConfigureAwait(false);
        if (analysis is not null)
        {
            SourceFile file = new PythonAstMapper(filePath, sourceText).Parse(analysis.RootElement);
            PythonSemanticBinding.Attach(file, _toolchain);
            return file;
        }
        PythonLexer lexer = new PythonLexer(sourceText, filePath);
        List<Tok> tokens = lexer.Tokenize();
        Stack<Tok> brackets = new Stack<Tok>();
        foreach (Tok token in tokens)
        {
            if (token.Kind is TK.LParen or TK.LBrack or TK.LBrace)
            {
                brackets.Push(token);
            }

            if (token.Kind is TK.RParen or TK.RBrack or TK.RBrace)
            {
                if (brackets.Count == 0 || (brackets.Peek().Kind, token.Kind) is not
                    ((TK.LParen, TK.RParen) or (TK.LBrack, TK.RBrack) or (TK.LBrace, TK.RBrace)))
                {
                    lexer.LexErrors.Add($"{filePath}({token.Line},{token.Col}): error: unmatched closing bracket");
                }
                else
                {
                    brackets.Pop();
                }
            }
        }
        foreach (Tok bracket in brackets)
        {
            lexer.LexErrors.Add($"{filePath}({bracket.Line},{bracket.Col}): error: unclosed '{bracket.Text}'");
        }

        SourceFile fallback = new PythonParserCore(filePath, tokens, lexer.CommentLines, lexer.LexErrors)
            .ParseModule(sourceText);
        foreach (StatementInfo statement in fallback.Statements)
        {
            statement.DeferSemanticAnalysis(new(() =>
            {
                Cop.Core.ProviderWarnings.ReportOnce("python", "binding",
                    "Python semantic facts are unavailable in simpler analysis; install a usable Python 3 interpreter.");
                return true;
            }));
        }

        return fallback;
    }
}
