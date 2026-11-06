// Controls the immutable comparison used by a review.

namespace Cop.Core;

/// <summary>Controls the immutable comparison used by a review.</summary>
public sealed record ReviewOptions
{
    /// <summary>
    /// The change source to analyze, such as automatic discovery or a pull request.
    /// </summary>
    public string Scope { get; init; } = "auto";
    /// <summary>
    /// The optional Git revision used as the base of a change comparison.
    /// </summary>
    public string? Base { get; init; }
    /// <summary>
    /// The pull request number or URL used for pull-request-scoped analysis.
    /// </summary>
    public string? Pr { get; init; }
    /// <summary>
    /// Whether diagnostics cover all analyzed code or only changed lines.
    /// </summary>
    public string ReportScope { get; init; } = "all";
    /// <summary>
    /// The optional path to the AI review configuration.
    /// </summary>
    public string? AiConfigPath { get; init; }

    /// <summary>
    /// Rejects unsupported scope values and incompatible review selections.
    /// </summary>
    public void Validate()
    {
        if (AiConfigPath is not null && string.IsNullOrWhiteSpace(AiConfigPath))
        {
            throw new ArgumentException("The AI configuration path must not be empty.");
        }

        if (Scope is not ("auto" or "branch" or "pr" or "full"))
        {
            throw new ArgumentException("Review scope must be auto, branch, pr, or full.");
        }

        if (ReportScope is not ("all" or "changes"))
        {
            throw new ArgumentException("Report scope must be all or changes.");
        }

        if (Base is not null && (string.IsNullOrWhiteSpace(Base) || Base.StartsWith('-')))
        {
            throw new ArgumentException("The comparison base must be a nonempty Git revision, not an option.");
        }

        if (Pr is not null && !IsPullRequest(Pr))
        {
            throw new ArgumentException("The PR must be a positive number or an HTTPS pull request URL.");
        }

        if (Base is not null && Pr is not null)
        {
            throw new ArgumentException("--base and --pr cannot be combined.");
        }

        if (Scope == "full" && (Base is not null || Pr is not null || ReportScope == "changes"))
        {
            throw new ArgumentException("Full scope cannot be combined with a base, PR, or changed report scope.");
        }

        if (Pr is not null && Scope != "pr")
        {
            throw new ArgumentException("--pr requires --scope pr.");
        }

        if (Scope == "pr" && (Pr is null || Base is not null))
        {
            throw new ArgumentException("PR scope requires --pr and cannot be combined with --base.");
        }
    }

    private static bool IsPullRequest(string value)
    {
        if (long.TryParse(value, out long number) && number > 0)
        {
            return value.All(char.IsAsciiDigit);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }

        string[] parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 4 && parts[2] == "pull"
            && long.TryParse(parts[3], out number) && number > 0;
    }
}
