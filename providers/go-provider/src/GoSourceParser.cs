// Tokenizes Go source and projects declarations and statements into the code model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>
/// Parses Go source and enriches it with available native compiler facts.
/// </summary>
public class GoSourceParser(string? toolchainSearchPath = null,
    Func<string, CancellationToken, ValueTask<Cop.Core.ToolchainResult>>? nativeRequest = null) : ISourceParser
{
    /// <summary>Creates a Go parser using the default toolchain discovery path.</summary>
    public GoSourceParser() : this(null, null) { }
    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions => [".go"];
    /// <inheritdoc />
    public override string Language => "go";
    /// <inheritdoc />
    public override bool CanCache(string filePath) => false;
    /// <inheritdoc />
    public override bool SupportsCrossFileMembers => true;
    /// <inheritdoc />
    public override string GetCacheIdentity(string filePath) =>
        base.GetCacheIdentity(filePath) + ":" +
        GoNativeAnalysis.Identity(toolchainSearchPath, nativeRequest);

    /// <inheritdoc />
    public override async ValueTask<SourceFile?> ParseAsync(
        string filePath, string sourceText, CancellationToken cancellationToken = default)
    {
        GoLexer lexer = new GoLexer(sourceText);
        List<GoToken> tokens = lexer.Tokenize();
        GoParser parser = new GoParser(tokens, sourceText, filePath, lexer.LexErrors);
        cancellationToken.ThrowIfCancellationRequested();
        return await GoNativeAnalysis.EnrichAsync(
            parser.Parse(filePath), toolchainSearchPath, nativeRequest, cancellationToken)
            .ConfigureAwait(false);
    }
}
