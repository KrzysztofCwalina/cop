// Captures changed files and source ranges for revision-based analysis.

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

    private static readonly StringComparison s_pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer s_pathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);
    private readonly ChangeCommandRunner _command;
    private readonly Action<string> _logger;
    private readonly ReviewOptions _options;
    private readonly Dictionary<string, string> _fingerprints = new(s_pathComparer);
    private string _targetPrefix = "";
    private string _initialGitState = "";
    private PullRequestMetadata? _pullRequest;

    /// <summary>Asynchronously captures a complete, validated change snapshot.</summary>
    public static ValueTask<ChangeSnapshot> CaptureAsync(string rootPath, ReviewOptions options,
        Action<string>? log = null, CancellationToken cancellationToken = default) =>
        CaptureAsync(rootPath, options, new ChangeCommand(), log, cancellationToken);

    internal static async ValueTask<ChangeSnapshot> CaptureAsync(string rootPath, ReviewOptions options,
        ChangeCommandRunner command, Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChangeSnapshot snapshot = new(rootPath, options, command, log);
        await snapshot.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private ChangeSnapshot(string rootPath, ReviewOptions options, ChangeCommandRunner command,
        Action<string>? log)
    {
        options.Validate();
        if (options.Scope == "full")
        {
            throw new InvalidOperationException("Full-scope reviews do not have a change snapshot.");
        }

        this._options = options;
        this._command = command;
        _logger = log ?? (_ => { });
        RootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(RootPath))
        {
            throw new InvalidOperationException($"The review target directory does not exist: {RootPath}");
        }

        RejectReparsePoints(RootPath);
    }

    private async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ReviewOptions options = _options;
        RepositoryRoot = Path.GetFullPath((await RequireGitAtAsync(cancellationToken, RootPath, "rev-parse", "--show-toplevel")).TrimEnd('\r', '\n'));
        if (!IsWithin(RootPath, RepositoryRoot))
        {
            throw new InvalidOperationException("The review target must be inside the Git worktree.");
        }

        _targetPrefix = Normalize(Path.GetRelativePath(RepositoryRoot, RootPath));
        if (_targetPrefix == ".")
        {
            _targetPrefix = "";
        }

        if ((await GitAsync(cancellationToken, "rev-parse", "--is-shallow-repository")).Trim() == "true")
        {
            throw new InvalidOperationException("Change-scoped review requires complete history. Run git fetch --unshallow, then retry.");
        }

        HeadCommit = (await ResolveCommitAsync("HEAD", cancellationToken: cancellationToken));
        _initialGitState = (await CaptureGitStateAsync(cancellationToken: cancellationToken));
        if (options.Pr is not null)
        {
            _pullRequest = (await LoadPullRequestAsync(cancellationToken: cancellationToken));
            if (!string.Equals(HeadCommit, _pullRequest.Head, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"PR review requires its exact head {_pullRequest.Head}; the current HEAD is {HeadCommit}. Check out the PR explicitly, then retry.");
            }

            if ((await GitAsync(cancellationToken, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", TargetPathspec)).Length != 0)
            {
                throw new InvalidOperationException("PR review requires a clean target, including nonignored untracked files. Commit or move local edits; no checkout or stash was performed.");
            }

            string remote = (await FindPullRequestRemoteAsync(_pullRequest, cancellationToken: cancellationToken));
            await EnsurePullRequestCommitAsync(_pullRequest.Base, remote, _pullRequest.Base, cancellationToken: cancellationToken);
            await EnsurePullRequestCommitAsync(_pullRequest.Head, remote, $"refs/pull/{_pullRequest.Number}/head", cancellationToken: cancellationToken);
            BaseCommit = (await MergeBaseAsync(_pullRequest.Base, _pullRequest.Head, cancellationToken: cancellationToken));
        }
        else
        {
            BaseCommit = (await MergeBaseAsync((await ResolveCommitAsync(options.Base ?? (await DiscoverDefaultBaseAsync(cancellationToken: cancellationToken)), cancellationToken: cancellationToken)), HeadCommit, cancellationToken: cancellationToken));
        }

        foreach (string path in (await CandidatePathsAsync(cancellationToken: cancellationToken)))
        {
            if (!IsEligible(path))
            {
                continue;
            }

            string fullPath = ResolveRepositoryPath(path);
            RejectReparsePoints(fullPath);
            _fingerprints[path] = (await FingerprintAsync(fullPath, cancellationToken: cancellationToken));
        }
        Files = Array.AsReadOnly((await BuildChangesAsync(cancellationToken: cancellationToken)).ToArray());
        await AssertUnchangedAsync(cancellationToken: cancellationToken);
        string mode = _pullRequest is null ? "branch" : "pr";
        string localEditPolicy = _pullRequest is null
            ? "included (staged, unstaged, nonignored untracked)"
            : "excluded (exact PR head; clean target required)";
        Log($"Change-scoped review: mode={mode}; scope={options.Scope}; base={BaseCommit}; head={HeadCommit}; "
            + $"local edits={localEditPolicy}; files={Files.Count}; hunks={Files.Sum(file => file.Ranges.Count)}.");
        if (Files.Count == 0)
        {
            Log("Change-scoped review: no applicable changes (zero eligible changed files); no change-scoped review performed; no full-scope fallback.");
        }
    }

    /// <summary>
    /// The absolute root directory of the Git repository being compared.
    /// </summary>
    public string RepositoryRoot { get; private set; } = "";
    /// <summary>
    /// The analyzed target directory within the repository.
    /// </summary>
    public string RootPath { get; }
    /// <summary>
    /// The resolved commit used as the left side of the comparison.
    /// </summary>
    public string BaseCommit { get; private set; } = "";
    /// <summary>
    /// The resolved commit used as the right side of the comparison.
    /// </summary>
    public string HeadCommit { get; private set; } = "";
    /// <summary>
    /// The files changed within the selected analysis scope.
    /// </summary>
    public IReadOnlyList<ChangedFile> Files { get; private set; } = [];

    /// <summary>
    /// Sends a change-discovery diagnostic to the configured logger.
    /// </summary>
    public void Log(string message) => _logger(message);

    /// <summary>Finding paths are target-relative; absolute finding paths must remain in the target.</summary>
    public string ToRepositoryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RejectTraversal(path);
        string fullPath = Path.GetFullPath(path, RootPath);
        if (!IsWithin(fullPath, RootPath))
        {
            throw new InvalidOperationException("The finding path is outside the review target.");
        }

        RejectReparsePoints(fullPath);
        string relative = Normalize(Path.GetRelativePath(RepositoryRoot, fullPath));
        if (!IsEligible(relative))
        {
            throw new InvalidOperationException("The finding path is excluded from source access.");
        }

        return relative;
    }

    /// <summary>
    /// Tests whether a path and source line fall within the selected changes.
    /// </summary>
    public bool IsChanged(string path, int line)
    {
        string relative = ToRepositoryPath(path);
        foreach (ChangedFile file in Files)
        {
            if (!s_pathComparer.Equals(file.Path, relative) && !s_pathComparer.Equals(file.OldPath, relative))
            {
                continue;
            }

            if (line <= 0 || file.IsDeleted)
            {
                return true;
            }

            if (file.Ranges.Any(range => range.NewCount == 0
                ? line == Math.Max(1, range.NewStart)
                : line >= range.NewStart && (long)line < (long)range.NewStart + range.NewCount))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Rejects a snapshot if the repository state no longer matches its capture.
    /// </summary>
    public async ValueTask AssertUnchangedAsync(CancellationToken cancellationToken = default)
    {
        if (!string.Equals((await ResolveCommitAsync("HEAD", cancellationToken: cancellationToken)), HeadCommit, StringComparison.Ordinal)
            || (await CaptureGitStateAsync(cancellationToken: cancellationToken)) != _initialGitState)
        {
            throw Stale();
        }

        HashSet<string> paths = (await CandidatePathsAsync(cancellationToken: cancellationToken)).Where(IsEligible).ToHashSet(s_pathComparer);
        if (!paths.SetEquals(_fingerprints.Keys))
        {
            throw Stale();
        }

        foreach ((string? path, string? fingerprint) in _fingerprints)
        {
            string fullPath = ResolveRepositoryPath(path);
            RejectReparsePoints(fullPath);
            if ((await FingerprintAsync(fullPath, cancellationToken: cancellationToken)) != fingerprint)
            {
                throw Stale();
            }
        }
        if (_pullRequest is not null && (await LoadPullRequestAsync(cancellationToken: cancellationToken)) != _pullRequest)
        {
            throw new InvalidOperationException("The PR base/head metadata changed during review. Discard the review and retry.");
        }
    }

    private string TargetPathspec => _targetPrefix.Length == 0 ? "." : $":(literal){_targetPrefix}";

    private async ValueTask<IEnumerable<string>> CandidatePathsAsync(CancellationToken cancellationToken = default) =>
        SplitNull((await GitAsync(cancellationToken, "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", TargetPathspec)))
            .Distinct(s_pathComparer);

    private async ValueTask<List<ChangedFile>> BuildChangesAsync(CancellationToken cancellationToken = default)
    {
        List<ChangedFile> result = new List<ChangedFile>();
        string[] tokens = SplitNull((await GitAsync(cancellationToken, "diff", "--no-ext-diff", "--no-textconv", "--name-status", "-z",
            "--find-renames", BaseCommit, "--", TargetPathspec)));
        long patchBytes = 0;
        int excluded = 0;
        for (int index = 0; index < tokens.Length;)
        {
            string rawStatus = tokens[index++];
            if (index >= tokens.Length)
            {
                throw new InvalidOperationException("Git returned an incomplete changed-file record.");
            }

            string path = tokens[index++];
            string? oldPath = null;
            string status = rawStatus[..1];
            if (status is "R" or "C")
            {
                oldPath = path;
                if (index >= tokens.Length)
                {
                    throw new InvalidOperationException("Git returned an incomplete rename record.");
                }

                path = tokens[index++];
            }
            if (!IsEligible(path) || (oldPath is not null && !IsEligible(oldPath)))
            {
                excluded++;
                continue;
            }
            RejectReparsePoints(ResolveRepositoryPath(path));
            if (oldPath is not null)
            {
                RejectReparsePoints(ResolveRepositoryPath(oldPath));
            }

            string[] paths = oldPath is null ? new[] { path } : new[] { oldPath, path };
            // Zero-context hunks define exact ranges; the separately retained patch includes context.
            string patch = (await DiffWithContextAsync(3, paths, cancellationToken: cancellationToken));
            IReadOnlyList<ChangedRange> ranges = ParseRanges((await DiffWithContextAsync(0, paths, cancellationToken: cancellationToken)));
            Add(new(path, oldPath, status, patch, ranges, IsBinaryPatch(patch)));
        }
        foreach (string path in SplitNull((await GitAsync(cancellationToken, "ls-files", "-z", "--others", "--exclude-standard", "--", TargetPathspec))))
        {
            if (!IsEligible(path))
            {
                excluded++;
                continue;
            }
            string fullPath = ResolveRepositoryPath(path);
            RejectReparsePoints(fullPath);
            byte[] bytes = (await ReadFileBoundedAsync(fullPath, cancellationToken: cancellationToken));
            EnsureFingerprint(path, bytes);
            bool binary = !TryText(bytes, out string? source);
            if (binary)
            {
                Add(new(path, null, "A", $"Binary file added: {path}\n", Array.Empty<ChangedRange>(), true));
                continue;
            }
            // A patch retains original CRs/BOMs and missing-final-newline information.
            source = s_strictUtf8.GetString(bytes);
            string[] lines = source.Length == 0 ? Array.Empty<string>() : source.Split('\n');
            if (lines.Length != 0 && lines[^1].Length == 0)
            {
                lines = lines[..^1];
            }

            string patch = $"diff --git {QuotePatchPath("a/" + path)} {QuotePatchPath("b/" + path)}\nnew file mode 100644\n";
            if (lines.Length != 0)
            {
                patch += $"--- /dev/null\n+++ {QuotePatchPath("b/" + path)}\n@@ -0,0 +1,{lines.Length} @@\n"
                    + string.Join('\n', lines.Select(line => "+" + line)) + "\n";
                if (!source.EndsWith('\n'))
                {
                    patch += "\\ No newline at end of file\n";
                }
            }
            Add(new(path, null, "A", patch,
                lines.Length == 0 ? Array.Empty<ChangedRange>()
                    : Array.AsReadOnly(new[] { new ChangedRange(0, 0, 1, lines.Length) }), false));
        }
        if (excluded != 0)
        {
            Log($"Change-scoped review: excluded {excluded} changed paths outside the eligible source set (generated, sensitive, or outside target).");
        }

        return result.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();

        void Add(ChangedFile file)
        {
            patchBytes += Encoding.UTF8.GetByteCount(file.Patch);
            if (result.Count >= MaxChangedFiles || patchBytes > MaxPatchBytes)
            {
                throw new InvalidOperationException(
                    $"The change exceeds the review limits ({MaxChangedFiles} files / {MaxPatchBytes} patch bytes). Narrow the target or split the change; no patch was truncated.");
            }

            result.Add(file);
        }
    }

    private async ValueTask<string> DiffWithContextAsync(int context, string[] paths, CancellationToken cancellationToken = default) =>
        (await GitAsync(cancellationToken, new[] { "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames",
            $"--unified={context}", BaseCommit, "--" }.Concat(paths.Select(path => $":(literal){path}")).ToArray()));

    internal static IReadOnlyList<ChangedRange> ParseRanges(string patch)
    {
        List<ChangedRange> ranges = new List<ChangedRange>();
        foreach (string line in patch.Split('\n'))
        {
            if (!line.StartsWith("@@", StringComparison.Ordinal))
            {
                continue;
            }

            string[] parts = line.Split(' ', 4, StringSplitOptions.None);
            if (parts.Length != 4 || parts[0] != "@@" || !parts[3].StartsWith("@@", StringComparison.Ordinal))
            {
                throw InvalidHunk();
            }

            (int Start, int Count) oldRange = ParseHunkRange(parts[1], '-');
            (int Start, int Count) newRange = ParseHunkRange(parts[2], '+');
            ranges.Add(new(oldRange.Start, oldRange.Count, newRange.Start, newRange.Count));
        }
        return ranges.AsReadOnly();
    }

    private static (int Start, int Count) ParseHunkRange(string value, char sign)
    {
        if (value.Length < 2 || value[0] != sign)
        {
            throw InvalidHunk();
        }

        int comma = value.IndexOf(',');
        ReadOnlySpan<char> startText = comma < 0 ? value.AsSpan(1) : value.AsSpan(1, comma - 1);
        if (!int.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out int start))
        {
            throw InvalidHunk();
        }

        int count = 1;
        if (comma >= 0 && !int.TryParse(value.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out count))
        {
            throw InvalidHunk();
        }

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

    private async ValueTask<string> CaptureGitStateAsync(CancellationToken cancellationToken = default) => Hash(Encoding.UTF8.GetBytes(
        (await GitAsync(cancellationToken, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", TargetPathspec))
        + "\0" + (await GitAsync(cancellationToken, "diff", "--no-ext-diff", "--no-textconv", "--binary", "--cached", "--", TargetPathspec))));

    private async ValueTask<string> ResolveCommitAsync(string revision, CancellationToken cancellationToken = default) =>
        (await RequireGitAsync(cancellationToken, "rev-parse", "--verify", "--end-of-options", revision + "^{commit}")).Trim();

    private async ValueTask<string> MergeBaseAsync(string first, string second, CancellationToken cancellationToken = default)
    {
        string[] bases = (await RequireGitAsync(cancellationToken, "merge-base", "--all", first, second)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (bases.Length != 1)
        {
            throw new InvalidOperationException("The comparison has no unique merge base. Fetch complete history or choose an unambiguous --base.");
        }

        return bases[0];
    }

    private async ValueTask<string> DiscoverDefaultBaseAsync(CancellationToken cancellationToken = default)
    {
        string[] remotes = (await GitAsync(cancellationToken, "remote")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ChangeCommandResult branch = (await RunGitAsync(cancellationToken, "symbolic-ref", "--quiet", "--short", "HEAD"));
        string? remote = null;
        if (branch.ExitCode == 0)
        {
            ChangeCommandResult configured = (await RunGitAsync(cancellationToken, "config", "--get", $"branch.{branch.Output.Trim()}.remote"));
            if (configured.ExitCode == 0 && remotes.Contains(configured.Output.Trim(), StringComparer.Ordinal))
            {
                remote = configured.Output.Trim();
            }
        }
        if (remote is null && remotes.Length == 1)
        {
            remote = remotes[0];
        }

        if (remote is null)
        {
            throw new InvalidOperationException("Cannot determine the branch's default remote unambiguously. Configure its upstream or pass --base explicitly.");
        }

        ChangeCommandResult symbolicHead = (await RunGitAsync(cancellationToken, "symbolic-ref", "--quiet", $"refs/remotes/{remote}/HEAD"));
        if (symbolicHead.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Remote '{remote}' has no symbolic default HEAD. Run git remote set-head {remote} --auto after fetching, or pass --base.");
        }

        return symbolicHead.Output.Trim();
    }

    private async ValueTask<ChangeCommandResult> RunGitAsync(CancellationToken cancellationToken, params string[] arguments) =>
        (await _command.RunAsync(RepositoryRoot, "git", new[] { "--no-pager", "-c", "core.fsmonitor=false" }.Concat(arguments).ToArray(), cancellationToken: cancellationToken));

    private async ValueTask<string> GitAsync(CancellationToken cancellationToken, params string[] arguments) => (await RequireGitAsync(cancellationToken, arguments));

    private async ValueTask<string> RequireGitAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        ChangeCommandResult result = (await RunGitAsync(cancellationToken, arguments));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git comparison failed ({arguments[0]}): {result.Error.Trim()}. Fetch the required history/refs or pass an explicit --base; full scope is never selected implicitly.");
        }

        return result.Output;
    }

    private async ValueTask<string> RequireGitAtAsync(CancellationToken cancellationToken, string directory, params string[] arguments)
    {
        ChangeCommandResult result = (await _command.RunAsync(directory, "git", arguments, cancellationToken: cancellationToken));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Change-scoped review requires a Git worktree: {result.Error.Trim()}. Use --scope full explicitly for a full review.");
        }

        return result.Output;
    }

    private static string[] SplitNull(string value) => value.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    private static string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static InvalidOperationException Stale() =>
        new("Source or Git state changed during review. Discard the review and retry against a fresh snapshot.");
}
