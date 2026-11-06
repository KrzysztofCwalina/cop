// Tokenizes Java source and projects its declarations and statements into the code model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>
/// Parses Java source and enriches it with available native compiler facts.
/// </summary>
public class JavaSourceParser(string? toolchainSearchPath = null,
    Func<string, CancellationToken, ValueTask<Cop.Core.ToolchainResult>>? nativeRequest = null) : ISourceParser
{
    /// <summary>
    /// Creates a Java parser using the default toolchain discovery path.
    /// </summary>
    public JavaSourceParser() : this(null, null) { }
    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions => [".java"];
    /// <inheritdoc />
    public override string Language => "java";
    /// <inheritdoc />
    public override bool CanCache(string filePath) => false;
    /// <inheritdoc />
    public override string GetCacheIdentity(string filePath) =>
        base.GetCacheIdentity(filePath) + ":" +
        JavaNativeAnalysis.Identity(toolchainSearchPath, nativeRequest);

    /// <inheritdoc />
    public override async ValueTask<SourceFile?> ParseAsync(
        string filePath, string sourceText, CancellationToken cancellationToken = default)
    {
        JavaLexer lexer = new JavaLexer(sourceText);
        List<JavaToken> tokens = lexer.Tokenize();
        JavaParser parser = new JavaParser(tokens, lexer.Errors, sourceText);
        cancellationToken.ThrowIfCancellationRequested();
        return await JavaNativeAnalysis.EnrichAsync(
            parser.Parse(filePath), toolchainSearchPath, nativeRequest, cancellationToken)
            .ConfigureAwait(false);
    }
}
