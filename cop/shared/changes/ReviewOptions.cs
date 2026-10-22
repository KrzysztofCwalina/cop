namespace Cop.Core;

/// <summary>Controls the immutable comparison used by a review.</summary>
public sealed record ReviewOptions
{
    public string Scope { get; init; } = "auto";
    public string? Base { get; init; }
    public string? Pr { get; init; }
    public string ReportScope { get; init; } = "all";

    public void Validate()
    {
        if (Scope is not ("auto" or "branch" or "pr" or "full"))
            throw new ArgumentException("Review scope must be auto, branch, pr, or full.");
        if (ReportScope is not ("all" or "changes"))
            throw new ArgumentException("Report scope must be all or changes.");
        if (Base is not null && (string.IsNullOrWhiteSpace(Base) || Base.StartsWith('-')))
            throw new ArgumentException("The comparison base must be a nonempty Git revision, not an option.");
        if (Pr is not null && !IsPullRequest(Pr))
            throw new ArgumentException("The PR must be a positive number or an HTTPS pull request URL.");
        if (Base is not null && Pr is not null)
            throw new ArgumentException("--base and --pr cannot be combined.");
        if (Scope == "full" && (Base is not null || Pr is not null || ReportScope == "changes"))
            throw new ArgumentException("Full scope cannot be combined with a base, PR, or changed report scope.");
        if (Pr is not null && Scope != "pr")
            throw new ArgumentException("--pr requires --scope pr.");
        if (Scope == "pr" && (Pr is null || Base is not null))
            throw new ArgumentException("PR scope requires --pr and cannot be combined with --base.");
    }

    private static bool IsPullRequest(string value)
    {
        if (long.TryParse(value, out var number) && number > 0)
            return value.All(char.IsAsciiDigit);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 4 && parts[2] == "pull"
            && long.TryParse(parts[3], out number) && number > 0;
    }
}

/// <summary>Per-run context. Ordinary, full-scope runs never need to discover Git.</summary>
public sealed class AnalysisContext
{
    private readonly Lazy<ChangeSnapshot> changes;

    public AnalysisContext(string rootPath, ReviewOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RootPath = Path.GetFullPath(rootPath);
        Options = options;
        Action<string> logger = log ?? (_ => { });
        changes = new Lazy<ChangeSnapshot>(() => new ChangeSnapshot(RootPath, Options, logger));
    }

    public string RootPath { get; }
    public ReviewOptions Options { get; }
    public ChangeSnapshot Changes => changes.Value;

    public void AssertUnchanged()
    {
        if (changes.IsValueCreated)
            changes.Value.AssertUnchanged();
    }
}

public sealed record ChangedRange(int OldStart, int OldCount, int NewStart, int NewCount);

public sealed record ChangedFile(
    string Path,
    string? OldPath,
    string Status,
    string Patch,
    IReadOnlyList<ChangedRange> Ranges,
    bool IsBinary)
{
    public bool IsDeleted => Status == "D";
}
