using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cop.Core;

/// <summary>A pinned merge-base comparison against the final working tree, with guarded source access.</summary>
public sealed partial class ChangeSnapshot
{
    public const int MaxChangedFiles = 500;
    public const int MaxPatchBytes = 2 * 1024 * 1024;
    public const int MaxReadLines = 200;
    public const int MaxSourceResponseBytes = 32768;
    public const int MaxSearchResults = 40;
    public const int MaxSourceFileBytes = 16 * 1024 * 1024;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IChangeCommand command;
    private readonly Action<string> logger;
    private readonly ReviewOptions options;
    private readonly Dictionary<string, string> fingerprints = new(PathComparer);
    private readonly string targetPrefix;
    private readonly string initialGitState;
    private readonly PullRequestMetadata? pullRequest;

    internal ChangeSnapshot(string rootPath, ReviewOptions options, Action<string>? log = null)
        : this(rootPath, options, new ChangeCommand(), log) { }

    internal ChangeSnapshot(string rootPath, ReviewOptions options, IChangeCommand command, Action<string>? log = null)
    {
        options.Validate();
        if (options.Scope == "full")
            throw new InvalidOperationException("Full-scope reviews do not have a change snapshot.");
        this.options = options;
        this.command = command;
        logger = log ?? (_ => { });
        RootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(RootPath))
            throw new InvalidOperationException($"The review target directory does not exist: {RootPath}");
        RejectReparsePoints(RootPath);
        RepositoryRoot = Path.GetFullPath(RequireGitAt(RootPath, "rev-parse", "--show-toplevel").TrimEnd('\r', '\n'));
        if (!IsWithin(RootPath, RepositoryRoot))
            throw new InvalidOperationException("The review target must be inside the Git worktree.");
        targetPrefix = Normalize(Path.GetRelativePath(RepositoryRoot, RootPath));
        if (targetPrefix == ".")
            targetPrefix = "";
        if (Git("rev-parse", "--is-shallow-repository").Trim() == "true")
            throw new InvalidOperationException("Change-scoped review requires complete history. Run git fetch --unshallow, then retry.");
        HeadCommit = ResolveCommit("HEAD");
        initialGitState = CaptureGitState();
        if (options.Pr is not null)
        {
            pullRequest = LoadPullRequest();
            if (!string.Equals(HeadCommit, pullRequest.Head, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"PR review requires its exact head {pullRequest.Head}; the current HEAD is {HeadCommit}. Check out the PR explicitly, then retry.");
            if (Git("status", "--porcelain=v1", "-z", "--untracked-files=all", "--", TargetPathspec).Length != 0)
                throw new InvalidOperationException("PR review requires a clean target, including nonignored untracked files. Commit or move local edits; no checkout or stash was performed.");
            var remote = FindPullRequestRemote(pullRequest);
            EnsurePullRequestCommit(pullRequest.Base, remote, pullRequest.Base);
            EnsurePullRequestCommit(pullRequest.Head, remote, $"refs/pull/{pullRequest.Number}/head");
            BaseCommit = MergeBase(pullRequest.Base, pullRequest.Head);
        }
        else
        {
            BaseCommit = MergeBase(ResolveCommit(options.Base ?? DiscoverDefaultBase()), HeadCommit);
        }

        foreach (var path in CandidatePaths())
        {
            if (!IsEligible(path))
                continue;
            var fullPath = ResolveRepositoryPath(path);
            RejectReparsePoints(fullPath);
            fingerprints[path] = Fingerprint(fullPath);
        }
        Files = Array.AsReadOnly(BuildChanges().ToArray());
        AssertUnchanged();
        var mode = pullRequest is null ? "branch" : "pr";
        var localEditPolicy = pullRequest is null
            ? "included (staged, unstaged, nonignored untracked)"
            : "excluded (exact PR head; clean target required)";
        Log($"Change-scoped review: mode={mode}; scope={options.Scope}; base={BaseCommit}; head={HeadCommit}; "
            + $"local edits={localEditPolicy}; files={Files.Count}; hunks={Files.Sum(file => file.Ranges.Count)}.");
        if (Files.Count == 0)
            Log("Change-scoped review: no applicable changes (zero eligible changed files); no change-scoped review performed; no full-scope fallback.");
    }

    public string RepositoryRoot { get; }
    public string RootPath { get; }
    public string BaseCommit { get; }
    public string HeadCommit { get; }
    public IReadOnlyList<ChangedFile> Files { get; }

    public void Log(string message) => logger(message);

    /// <summary>Finding paths are target-relative; absolute finding paths must remain in the target.</summary>
    public string ToRepositoryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RejectTraversal(path);
        var fullPath = Path.GetFullPath(path, RootPath);
        if (!IsWithin(fullPath, RootPath))
            throw new InvalidOperationException("The finding path is outside the review target.");
        RejectReparsePoints(fullPath);
        var relative = Normalize(Path.GetRelativePath(RepositoryRoot, fullPath));
        if (!IsEligible(relative))
            throw new InvalidOperationException("The finding path is excluded from source access.");
        return relative;
    }

    public bool IsChanged(string path, int line)
    {
        var relative = ToRepositoryPath(path);
        foreach (var file in Files)
        {
            if (!PathComparer.Equals(file.Path, relative) && !PathComparer.Equals(file.OldPath, relative))
                continue;
            if (line <= 0 || file.IsDeleted)
                return true;
            if (file.Ranges.Any(range => range.NewCount == 0
                ? line == Math.Max(1, range.NewStart)
                : line >= range.NewStart && (long)line < (long)range.NewStart + range.NewCount))
                return true;
        }
        return false;
    }

    public void AssertUnchanged()
    {
        if (!string.Equals(ResolveCommit("HEAD"), HeadCommit, StringComparison.Ordinal)
            || CaptureGitState() != initialGitState)
            throw Stale();
        var paths = CandidatePaths().Where(IsEligible).ToHashSet(PathComparer);
        if (!paths.SetEquals(fingerprints.Keys))
            throw Stale();
        foreach (var (path, fingerprint) in fingerprints)
        {
            var fullPath = ResolveRepositoryPath(path);
            RejectReparsePoints(fullPath);
            if (Fingerprint(fullPath) != fingerprint)
                throw Stale();
        }
        if (pullRequest is not null && LoadPullRequest() != pullRequest)
            throw new InvalidOperationException("The PR base/head metadata changed during review. Discard the review and retry.");
    }

    private string TargetPathspec => targetPrefix.Length == 0 ? "." : $":(literal){targetPrefix}";

    private IEnumerable<string> CandidatePaths() =>
        SplitNull(Git("ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", TargetPathspec))
            .Distinct(PathComparer);

    private List<ChangedFile> BuildChanges()
    {
        var result = new List<ChangedFile>();
        var tokens = SplitNull(Git("diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z",
            "--find-renames", BaseCommit, "--", TargetPathspec));
        long patchBytes = 0;
        var excluded = 0;
        for (var index = 0; index < tokens.Length;)
        {
            var rawStatus = tokens[index++];
            if (index >= tokens.Length)
                throw new InvalidOperationException("Git returned an incomplete changed-file record.");
            var path = tokens[index++];
            string? oldPath = null;
            var status = rawStatus[..1];
            if (status is "R" or "C")
            {
                oldPath = path;
                if (index >= tokens.Length)
                    throw new InvalidOperationException("Git returned an incomplete rename record.");
                path = tokens[index++];
            }
            if (!IsEligible(path) || (oldPath is not null && !IsEligible(oldPath)))
            {
                excluded++;
                continue;
            }
            RejectReparsePoints(ResolveRepositoryPath(path));
            if (oldPath is not null)
                RejectReparsePoints(ResolveRepositoryPath(oldPath));
            var paths = oldPath is null ? new[] { path } : new[] { oldPath, path };
            // Zero-context hunks define exact ranges; the separately retained patch includes context.
            var patch = DiffWithContext(3, paths);
            var ranges = ParseRanges(DiffWithContext(0, paths));
            Add(new(path, oldPath, status, patch, ranges, IsBinaryPatch(patch)));
        }
        foreach (var path in SplitNull(Git("ls-files", "-z", "--others", "--exclude-standard", "--", TargetPathspec)))
        {
            if (!IsEligible(path))
            {
                excluded++;
                continue;
            }
            var fullPath = ResolveRepositoryPath(path);
            RejectReparsePoints(fullPath);
            var bytes = ReadFileBounded(fullPath);
            EnsureFingerprint(path, bytes);
            var binary = !TryText(bytes, out var source);
            if (binary)
            {
                Add(new(path, null, "A", $"Binary file added: {path}\n", Array.Empty<ChangedRange>(), true));
                continue;
            }
            // A patch retains original CRs/BOMs and missing-final-newline information.
            source = StrictUtf8.GetString(bytes);
            var lines = source.Length == 0 ? Array.Empty<string>() : source.Split('\n');
            if (lines.Length != 0 && lines[^1].Length == 0)
                lines = lines[..^1];
            var patch = $"diff --git {QuotePatchPath("a/" + path)} {QuotePatchPath("b/" + path)}\nnew file mode 100644\n";
            if (lines.Length != 0)
            {
                patch += $"--- /dev/null\n+++ {QuotePatchPath("b/" + path)}\n@@ -0,0 +1,{lines.Length} @@\n"
                    + string.Join('\n', lines.Select(line => "+" + line)) + "\n";
                if (!source.EndsWith('\n'))
                    patch += "\\ No newline at end of file\n";
            }
            Add(new(path, null, "A", patch,
                lines.Length == 0 ? Array.Empty<ChangedRange>()
                    : Array.AsReadOnly(new[] { new ChangedRange(0, 0, 1, lines.Length) }), false));
        }
        if (excluded != 0)
            Log($"Change-scoped review: excluded {excluded} changed paths outside the eligible source set (generated, sensitive, or outside target).");
        return result.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();

        void Add(ChangedFile file)
        {
            patchBytes += Encoding.UTF8.GetByteCount(file.Patch);
            if (result.Count >= MaxChangedFiles || patchBytes > MaxPatchBytes)
                throw new InvalidOperationException(
                    $"The change exceeds the review limits ({MaxChangedFiles} files / {MaxPatchBytes} patch bytes). Narrow the target or split the change; no patch was truncated.");
            result.Add(file);
        }
    }

    private string DiffWithContext(int context, string[] paths) =>
        Git(new[] { "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames",
            $"--unified={context}", BaseCommit, "--" }.Concat(paths.Select(path => $":(literal){path}")).ToArray());

    internal static IReadOnlyList<ChangedRange> ParseRanges(string patch)
    {
        var ranges = new List<ChangedRange>();
        foreach (var line in patch.Split('\n'))
        {
            if (!line.StartsWith("@@", StringComparison.Ordinal))
                continue;
            var parts = line.Split(' ', 4, StringSplitOptions.None);
            if (parts.Length != 4 || parts[0] != "@@" || !parts[3].StartsWith("@@", StringComparison.Ordinal))
                throw InvalidHunk();
            var oldRange = ParseHunkRange(parts[1], '-');
            var newRange = ParseHunkRange(parts[2], '+');
            ranges.Add(new(oldRange.Start, oldRange.Count, newRange.Start, newRange.Count));
        }
        return ranges.AsReadOnly();
    }

    private static (int Start, int Count) ParseHunkRange(string value, char sign)
    {
        if (value.Length < 2 || value[0] != sign)
            throw InvalidHunk();
        var comma = value.IndexOf(',');
        var startText = comma < 0 ? value.AsSpan(1) : value.AsSpan(1, comma - 1);
        if (!int.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
            throw InvalidHunk();
        var count = 1;
        if (comma >= 0 && !int.TryParse(value.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out count))
            throw InvalidHunk();
        return (start, count);
    }

    private static InvalidOperationException InvalidHunk() =>
        new("Git returned a malformed or unsupported diff hunk header; the change ranges cannot be reviewed safely.");

    private static bool IsBinaryPatch(string patch) =>
        patch.Contains("GIT binary patch", StringComparison.Ordinal)
        || patch.Split('\n').Any(line => line.StartsWith("Binary files ", StringComparison.Ordinal));

    private static string QuotePatchPath(string path) =>
        path.Any(character => char.IsControl(character) || character is '"' or '\\')
            ? "\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal) + "\""
            : path;

    private string CaptureGitState() => Hash(Encoding.UTF8.GetBytes(
        Git("status", "--porcelain=v1", "-z", "--untracked-files=all", "--", TargetPathspec)
        + "\0" + Git("diff", "--no-ext-diff", "--no-textconv", "--binary", "--cached", "--", TargetPathspec)));

    private string ResolveCommit(string revision) =>
        RequireGit("rev-parse", "--verify", "--end-of-options", revision + "^{commit}").Trim();

    private string MergeBase(string first, string second)
    {
        var bases = RequireGit("merge-base", "--all", first, second).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (bases.Length != 1)
            throw new InvalidOperationException("The comparison has no unique merge base. Fetch complete history or choose an unambiguous --base.");
        return bases[0];
    }

    private string DiscoverDefaultBase()
    {
        var remotes = Git("remote").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var branch = RunGit("symbolic-ref", "--quiet", "--short", "HEAD");
        string? remote = null;
        if (branch.ExitCode == 0)
        {
            var configured = RunGit("config", "--get", $"branch.{branch.Output.Trim()}.remote");
            if (configured.ExitCode == 0 && remotes.Contains(configured.Output.Trim(), StringComparer.Ordinal))
                remote = configured.Output.Trim();
        }
        if (remote is null && remotes.Length == 1)
            remote = remotes[0];
        if (remote is null)
            throw new InvalidOperationException("Cannot determine the branch's default remote unambiguously. Configure its upstream or pass --base explicitly.");
        var symbolicHead = RunGit("symbolic-ref", "--quiet", $"refs/remotes/{remote}/HEAD");
        if (symbolicHead.ExitCode != 0)
            throw new InvalidOperationException(
                $"Remote '{remote}' has no symbolic default HEAD. Run git remote set-head {remote} --auto after fetching, or pass --base.");
        return symbolicHead.Output.Trim();
    }

    private ChangeCommandResult RunGit(params string[] arguments) =>
        command.Run(RepositoryRoot, "git", new[] { "--no-pager", "-c", "core.fsmonitor=false" }.Concat(arguments).ToArray());

    private string Git(params string[] arguments) => RequireGit(arguments);

    private string RequireGit(params string[] arguments)
    {
        var result = RunGit(arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Git comparison failed ({arguments[0]}): {result.Error.Trim()}. Fetch the required history/refs or pass an explicit --base; full scope is never selected implicitly.");
        return result.Output;
    }

    private string RequireGitAt(string directory, params string[] arguments)
    {
        var result = command.Run(directory, "git", arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Change-scoped review requires a Git worktree: {result.Error.Trim()}. Use --scope full explicitly for a full review.");
        return result.Output;
    }

    private static string[] SplitNull(string value) => value.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static InvalidOperationException Stale() =>
        new("Source or Git state changed during review. Discard the review and retry against a fresh snapshot.");
}
