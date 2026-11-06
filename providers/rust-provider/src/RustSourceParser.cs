// Selects pinned frontend or source-only Rust parsing for the shared code model.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>Parses Rust using the pinned frontend with a structural fallback.</summary>
public class RustSourceParser : ISourceParser
{
    private readonly Lazy<RustFrontend?> _frontend;

    /// <summary>
    /// Overrides standard Rust toolchain inputs; null entries remove inherited values.
    /// A null dictionary leaves the process environment unchanged.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? ToolchainEnvironment { get; init; }

    /// <summary>
    /// Configures frontend deployment, project context, and standard libraries.
    /// </summary>
    public RustSourceParser(string? frontendPath = null, string? projectRoot = null, string? standardLibraryDirectory = null,
        Func<Stream?>? bundleFactory = null, string? cacheDirectory = null)
    {
        _frontend = new(() =>
        {
            RustFrontendDeployment? deployment = frontendPath is not null ? new RustFrontendDeployment(frontendPath)
                : RustFrontendBundle.Find(bundleFactory, cacheDirectory);
            return deployment is not null ? new(deployment.Executable, projectRoot ?? Directory.GetCurrentDirectory(),
                standardLibraryDirectory, deployment.PrepareStandardLibrary, ToolchainEnvironment) : null;
        });
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions => [".rs"];
    /// <inheritdoc />
    public override string Language => "rust";
    /// <inheritdoc />
    public override bool CanCache(string filePath) => _frontend.Value is null;
    /// <inheritdoc />
    public override string GetCacheIdentity(string filePath) =>
        base.GetCacheIdentity(filePath) + ":" + (_frontend.Value?.Executable ?? "structural-fallback");

    /// <inheritdoc />
    public override async ValueTask<SourceFile?> ParseAsync(
        string filePath, string sourceText, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_frontend.Value is { } frontend)
            {
                return await frontend.ParseAsync(filePath, sourceText, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new(filePath, "rust", [], [], sourceText) { ParseErrors = [$"{filePath}: error: {ex.Message}"] };
        }
        RustLexer lexer = new RustLexer(sourceText);
        List<RustToken> tokens = lexer.Tokenize();
        RustParser parser = new RustParser(tokens, sourceText, lexer.Errors);
        return parser.Parse(filePath);
    }
}
