using System.Text;
using System.Text.Json;
using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
[NonParallelizable]
public sealed class ChangeSnapshotTests
{
    private string root = null!;
    private string baseline = null!;
    private readonly ChangeCommand commands = new();

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Directory.GetCurrentDirectory(), ".change-snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Git("init", "--initial-branch=trunk");
        Git("config", "user.email", "snapshot@example.invalid");
        Git("config", "user.name", "Snapshot Test");
        Git("config", "core.autocrlf", "false");
        Git("config", "commit.gpgsign", "false");
        Write("source.cs", "one\ntwo\nthree\nfour\nfive\n");
        Write("unchanged.cs", "unchanged needle\nsecond\n");
        baseline = Commit("baseline");
    }

    [TearDown]
    public void TearDown()
    {
        if (!Directory.Exists(root))
            return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
        var parent = Path.GetDirectoryName(root)!;
        if (!Directory.EnumerateFileSystemEntries(parent).Any())
            Directory.Delete(parent);
    }

    [Test]
    public void BranchComparisonIncludesCommittedStagedUnstagedAndUntrackedOnce()
    {
        Write("source.cs", "one\ncommitted\nthree\nfour\nfive\n");
        Commit("feature");
        Write("source.cs", "one\ncommitted\nstaged\nfour\nfive\n");
        Git("add", "source.cs");
        Write("source.cs", "one\ncommitted\nstaged\nunstaged\nfive\n");
        Write("new file.cs", "new\ncontent\n");

        var snapshot = Snapshot();

        Assert.That(snapshot.BaseCommit, Is.EqualTo(baseline));
        Assert.That(snapshot.HeadCommit, Is.EqualTo(Git("rev-parse", "HEAD").Trim()));
        Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "new file.cs", "source.cs" }));
        var modified = snapshot.Files.Single(file => file.Path == "source.cs");
        Assert.Multiple(() =>
        {
            Assert.That(modified.Status, Is.EqualTo("M"));
            Assert.That(modified.Ranges, Is.EqualTo(new[] { new ChangedRange(2, 3, 2, 3) }));
            Assert.That(modified.Patch, Does.Contain(" one\n-two\n-three\n-four\n+committed\n+staged\n+unstaged\n five\n"));
            Assert.That(modified.IsBinary, Is.False);
            Assert.That(snapshot.IsChanged("source.cs", 1), Is.False);
            Assert.That(snapshot.IsChanged("source.cs", 2), Is.True);
            Assert.That(snapshot.IsChanged("source.cs", 4), Is.True);
            Assert.That(snapshot.IsChanged("source.cs", 5), Is.False);
            Assert.That(snapshot.IsChanged("new file.cs", 2), Is.True);
            Assert.That(snapshot.Files[0].Ranges, Is.EqualTo(new[] { new ChangedRange(0, 0, 1, 2) }));
            Assert.That(snapshot.ReadSource("new file.cs", 1, 2), Is.EqualTo("1: new\n2: content"));
        });
        snapshot.AssertUnchanged();
    }

    [Test]
    public void StagedEditCancelledByWorkingTreeProducesZeroReview()
    {
        Write("source.cs", "changed\n");
        Git("add", "source.cs");
        Write("source.cs", "one\ntwo\nthree\nfour\nfive\n");
        var logs = new List<string>();
        var snapshot = new AnalysisContext(root, new ReviewOptions { Base = baseline }, logs.Add).Changes;
        Assert.That(snapshot.Files, Is.Empty);
        Assert.That(logs, Is.EqualTo(new[]
        {
            $"Change-scoped review: mode=branch; scope=auto; base={baseline}; head={baseline}; "
                + "local edits=included (staged, unstaged, nonignored untracked); files=0; hunks=0.",
            "Change-scoped review: no applicable changes (zero eligible changed files); no change-scoped review performed; no full-scope fallback."
        }));
        Assert.That(snapshot.IsChanged("source.cs", 0), Is.False);
    }

    [Test]
    public void BranchScopeSummaryIsEmittedOnlyOnceWhenLazySnapshotResolves()
    {
        Write("source.cs", "one\nstaged\nthree\nfour\nfive\n");
        Git("add", "source.cs");
        Write("source.cs", "one\nstaged\nunstaged\nfour\nfive\n");
        Write("new.cs", "untracked\n");
        var logs = new List<string>();
        var context = new AnalysisContext(root, new ReviewOptions { Scope = "branch", Base = baseline }, logs.Add);
        context.AssertUnchanged();
        Assert.That(logs, Is.Empty);

        var snapshot = context.Changes;
        Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "new.cs", "source.cs" }));
        Assert.That(logs, Is.EqualTo(new[]
        {
            $"Change-scoped review: mode=branch; scope=branch; base={baseline}; head={baseline}; "
                + "local edits=included (staged, unstaged, nonignored untracked); files=2; hunks=2."
        }));

        Assert.That(context.Changes, Is.SameAs(snapshot));
        context.AssertUnchanged();
        Assert.That(logs, Has.Count.EqualTo(1));
    }

    [Test]
    public void RenamesAndDeletionsPreserveOldPathsAndPureDeletionAnchors()
    {
        Git("mv", "unchanged.cs", "renamed space.cs");
        Write("renamed space.cs", "unchanged needle\nsecond\nthird\n");
        Write("source.cs", "one\ntwo\nfour\nfive\n");
        var snapshot = Snapshot();
        var rename = snapshot.Files.Single(file => file.Status == "R");
        Assert.Multiple(() =>
        {
            Assert.That(rename.Path, Is.EqualTo("renamed space.cs"));
            Assert.That(rename.OldPath, Is.EqualTo("unchanged.cs"));
            Assert.That(rename.Ranges, Is.EqualTo(new[] { new ChangedRange(2, 0, 3, 1) }));
            Assert.That(snapshot.IsChanged("renamed space.cs", 3), Is.True);
            Assert.That(snapshot.IsChanged("unchanged.cs", 0), Is.True);
            Assert.That(snapshot.Files.Single(file => file.Path == "source.cs").Ranges,
                Is.EqualTo(new[] { new ChangedRange(3, 1, 2, 0) }));
            Assert.That(snapshot.IsChanged("source.cs", 2), Is.True);
            Assert.That(snapshot.IsChanged("source.cs", 3), Is.False);
        });
        Git("rm", "--force", "source.cs");
        snapshot = Snapshot();
        Assert.That(snapshot.Files.Single(file => file.Path == "source.cs").IsDeleted, Is.True);
        Assert.That(snapshot.IsChanged("source.cs", 4), Is.True);
        Assert.That(snapshot.ReadSource("source.cs", 2, 2, "base"), Is.EqualTo("2: two\n3: three"));
        Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource("source.cs", 1, 1));
    }

    [Test]
    public void SubfolderPathsAreTargetRelativeWhileSourcesAreRepositoryRelative()
    {
        Write("nested folder/inside.cs", "inside\n");
        Commit("nested baseline");
        var nestedBase = Git("rev-parse", "HEAD").Trim();
        Write("nested folder/inside.cs", "inside changed\n");
        Write("source.cs", "outside changed\n");
        var target = Path.Combine(root, "nested folder");
        var snapshot = new AnalysisContext(target, new ReviewOptions { Base = nestedBase }).Changes;
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "nested folder/inside.cs" }));
            Assert.That(snapshot.ToRepositoryPath("inside.cs"), Is.EqualTo("nested folder/inside.cs"));
            Assert.That(snapshot.ToRepositoryPath(Path.Combine(target, "inside.cs")), Is.EqualTo("nested folder/inside.cs"));
            Assert.That(snapshot.IsChanged("inside.cs", 1), Is.True);
            Assert.That(snapshot.ReadSource("nested folder/inside.cs", 1, 1), Is.EqualTo("1: inside changed"));
        });
        Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource("source.cs", 1, 1));
        Assert.Throws<InvalidOperationException>(() => snapshot.ToRepositoryPath(Path.Combine(root, "source.cs")));
        Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource("inside.cs", 1, 1));
    }

    [Test]
    public void SourceToolsReadUnchangedSourceAndBoundTheirResults()
    {
        Write("source.cs", "changed\n");
        Write("many.cs", string.Concat(Enumerable.Repeat("needle\n", 45)));
        var snapshot = Snapshot();
        Assert.That(snapshot.ReadSource("unchanged.cs", 1, 2), Is.EqualTo("1: unchanged needle\n2: second"));
        Assert.That(snapshot.ReadSource("source.cs", 1, 1, "base"), Is.EqualTo("1: one"));
        var matches = snapshot.SearchSource("needle", 2).Split('\n');
        Assert.That(matches, Has.Length.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(matches[0], Is.EqualTo("many.cs:1: needle"));
            Assert.That(matches[1], Is.EqualTo("many.cs:2: needle"));
            Assert.That(matches[2], Does.StartWith("[incomplete:"));
            Assert.That(snapshot.SearchSource("no-such-token"), Is.EqualTo("No matches."));
        });
        Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource("source.cs", 1, 201));
        Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource("source.cs", 0, 1));
        Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource("source.cs", 2, 1));
        Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource("source.cs", 1, 1, "worktree"));
        Assert.Throws<InvalidOperationException>(() => snapshot.SearchSource("needle", 41));
    }

    [Test]
    public void SourceByteLimitsFailExplicitlyRatherThanTruncating()
    {
        Write("large.cs", new string('é', 17000) + "\n");
        var snapshot = Snapshot();
        Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource("large.cs", 1, 1));
        var search = snapshot.SearchSource("é");
        Assert.That(search, Does.Contain("[incomplete:"));
        Assert.That(Encoding.UTF8.GetByteCount(search), Is.LessThanOrEqualTo(ChangeSnapshot.MaxSourceResponseBytes));
    }

    [Test]
    public void BinaryChangesAreExposedButCannotBeReadAsSource()
    {
        File.WriteAllBytes(Path.Combine(root, "binary.dat"), new byte[] { 0, 1, 2, 255 });
        var snapshot = Snapshot();
        Assert.That(snapshot.Files.Single().Path, Is.EqualTo("binary.dat"));
        Assert.That(snapshot.Files.Single().IsBinary, Is.True);
        Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource("binary.dat", 1, 1));
        Assert.That(snapshot.SearchSource("needle"), Does.Contain("[incomplete:"));
    }

    [Test]
    public void UnsafeAndSensitiveSourceRequestsAreRejected()
    {
        Write(".gitignore", "ignored.cs\n");
        Write("ignored.cs", "ignored needle\n");
        Write(".env", "SECRET=needle\n");
        Write(".env.production", "SECRET=needle\n");
        Write("bin/generated.cs", "generated needle\n");
        Write("private.key", "secret needle\n");
        var snapshot = Snapshot();
        foreach (var path in new[] { "../outside", ".git/config", ".env", ".env.production", "private.key", "bin/generated.cs", "ignored.cs" })
            Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource(path, 1, 1), path);
        Assert.That(snapshot.SearchSource("needle"), Is.EqualTo("unchanged.cs:1: unchanged needle"));
        Assert.Throws<InvalidOperationException>(() => snapshot.ToRepositoryPath("../source.cs"));
        Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource(Path.Combine(root, "source.cs"), 1, 1));
        if (OperatingSystem.IsWindows())
            Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource("source.cs:stream", 1, 1));
    }

    [Test]
    public void ReparsePointTraversalIsRejectedBeforeSourceIsRead()
    {
        var linked = Path.Combine(root, "linked");
        var actual = Path.Combine(root, "actual");
        Directory.CreateDirectory(actual);
        Write("actual/linked.cs", "must not traverse\n");
        var snapshot = Snapshot();
        if (OperatingSystem.IsWindows())
        {
            var junction = commands.Run(root, "cmd.exe", "/c", "mklink", "/J", linked, actual);
            Assert.That(junction.ExitCode, Is.EqualTo(0), junction.Error);
        }
        else
        {
            Directory.CreateSymbolicLink(linked, actual);
        }
        try
        {
            Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource("linked/linked.cs", 1, 1));
            Assert.Throws<InvalidOperationException>(() => snapshot.ToRepositoryPath("linked/linked.cs"));
            Assert.Throws<InvalidOperationException>(() =>
                _ = new AnalysisContext(linked, new ReviewOptions { Base = baseline }).Changes);
        }
        finally
        {
            Directory.Delete(linked);
        }
    }

    [Test]
    public void GitPathProtocolPreservesSpacesUnicodeAndPathspecMetacharacters()
    {
        Write("unicode λ [1].cs", "initial\n");
        var nameBase = Commit("names");
        Write("unicode λ [1].cs", "modified\n");
        Write("new [2] space.cs", "untracked\n");
        var snapshot = new AnalysisContext(root, new ReviewOptions { Base = nameBase }).Changes;
        Assert.That(snapshot.Files.Select(file => file.Path),
            Is.EqualTo(new[] { "new [2] space.cs", "unicode λ [1].cs" }));
        Assert.That(snapshot.ReadSource("unicode λ [1].cs", 1, 1), Is.EqualTo("1: modified"));
        Assert.That(snapshot.Files.Single(file => file.Status == "M").Patch, Does.Contain("+modified"));
    }

    [Test]
    public void PatchLimitFailsExplicitlyWithoutReturningAPartialSnapshot()
    {
        Write("over-limit.cs", new string('x', ChangeSnapshot.MaxPatchBytes) + "\n");
        var failure = Assert.Throws<InvalidOperationException>(() => _ = Snapshot());
        Assert.That(failure!.Message, Does.Contain("no patch was truncated"));
    }

    [Test]
    public void MergeBaseExcludesChangesMadeOnlyOnTheBaseBranch()
    {
        Git("checkout", "--quiet", "-b", "feature");
        Write("source.cs", "feature\n");
        var feature = Commit("feature");
        Git("checkout", "--quiet", "trunk");
        Write("base-only.cs", "base branch\n");
        Commit("base diverged");
        Git("checkout", "--quiet", "feature");
        var snapshot = new AnalysisContext(root, new ReviewOptions { Base = "trunk" }).Changes;
        Assert.That(snapshot.BaseCommit, Is.EqualTo(baseline));
        Assert.That(snapshot.HeadCommit, Is.EqualTo(feature));
        Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "source.cs" }));
    }

    [Test]
    public void EmptyAndNoFinalNewlineFilesHaveExactRanges()
    {
        Write("empty.cs", "");
        Write("no-newline.cs", "last line");
        var snapshot = Snapshot();
        Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "empty.cs", "no-newline.cs" }));
        Assert.That(snapshot.Files[0].Ranges, Is.Empty);
        Assert.That(snapshot.Files[1].Ranges, Is.EqualTo(new[] { new ChangedRange(0, 0, 1, 1) }));
        Assert.That(snapshot.Files[1].Patch, Does.Contain("+last line\n\\ No newline at end of file\n"));
        Assert.That(snapshot.ReadSource("no-newline.cs", 1, 1), Is.EqualTo("1: last line"));
        Assert.That(snapshot.IsChanged("empty.cs", 0), Is.True);
    }

    [TestCase("changed")]
    [TestCase("unchanged")]
    [TestCase("new")]
    [TestCase("index")]
    [TestCase("head")]
    public void SourceAndGitChangesInvalidateTheSnapshot(string edit)
    {
        Write("source.cs", "initial change\n");
        var snapshot = Snapshot();
        switch (edit)
        {
            case "changed": Write("source.cs", "later change\n"); break;
            case "unchanged": Write("unchanged.cs", "edited unchanged\n"); break;
            case "new": Write("later.cs", "new\n"); break;
            case "index": Git("add", "source.cs"); break;
            case "head": Commit("moved head"); break;
        }
        Assert.Throws<InvalidOperationException>(snapshot.AssertUnchanged);
        if (edit == "unchanged")
            Assert.Throws<InvalidOperationException>(() => snapshot.ReadSource("unchanged.cs", 1, 1));
    }

    [Test]
    public void FingerprintsDetectSameLengthContentChangesWithRestoredTimestamp()
    {
        var snapshot = Snapshot();
        var path = Path.Combine(root, "unchanged.cs");
        var time = File.GetLastWriteTimeUtc(path);
        Write("unchanged.cs", "UNCHANGED needle\nsecond\n");
        File.SetLastWriteTimeUtc(path, time);
        Assert.Throws<InvalidOperationException>(snapshot.AssertUnchanged);
        Assert.Throws<InvalidOperationException>(() => snapshot.SearchSource("needle"));
    }

    [Test]
    public void DefaultBaseUsesRemoteSymbolicHeadWithoutGuessingBranchNames()
    {
        Git("remote", "add", "upstream", "https://github.com/example/repository.git");
        Git("update-ref", "refs/remotes/upstream/develop", baseline);
        Git("symbolic-ref", "refs/remotes/upstream/HEAD", "refs/remotes/upstream/develop");
        Write("source.cs", "changed\n");
        var snapshot = new AnalysisContext(root, new ReviewOptions()).Changes;
        Assert.That(snapshot.BaseCommit, Is.EqualTo(baseline));
        Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "source.cs" }));
        Git("remote", "add", "other", "https://github.com/example/other.git");
        Assert.Throws<InvalidOperationException>(() => _ = new AnalysisContext(root, new ReviewOptions()).Changes);
        Git("config", "branch.trunk.remote", "upstream");
        Assert.That(new AnalysisContext(root, new ReviewOptions()).Changes.BaseCommit, Is.EqualTo(baseline));
    }

    [Test]
    public void MissingBaseAndMissingDefaultHeadFailWithActionableMessages()
    {
        var missing = Assert.Throws<InvalidOperationException>(() =>
            _ = new AnalysisContext(root, new ReviewOptions { Base = "not-a-real-ref" }).Changes);
        Assert.That(missing!.Message, Does.Contain("--base"));
        Git("remote", "add", "upstream", "https://github.com/example/repository.git");
        var missingHead = Assert.Throws<InvalidOperationException>(() => _ = new AnalysisContext(root, new ReviewOptions()).Changes);
        Assert.That(missingHead!.Message, Does.Contain("git remote set-head"));
    }

    [Test]
    public void LinkedWorktreeUsesItsOwnHeadAndLocalEdits()
    {
        var linked = Path.Combine(root, "linked-worktree");
        Git("worktree", "add", "-b", "linked", linked);
        try
        {
            File.WriteAllText(Path.Combine(linked, "source.cs"), "linked edit\n", new UTF8Encoding(false));
            var snapshot = new AnalysisContext(linked, new ReviewOptions { Base = baseline }).Changes;
            Assert.That(snapshot.RepositoryRoot, Is.EqualTo(linked));
            Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "source.cs" }));
            Assert.That(snapshot.ReadSource("source.cs", 1, 1), Is.EqualTo("1: linked edit"));
        }
        finally { Git("worktree", "remove", "--force", linked); }
    }

    [Test]
    public void ContextStaysLazyForNonRepositoryAndFullScope()
    {
        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(plain);
        // The Git directory itself is not a worktree even though its parent is.
        var context = new AnalysisContext(Path.Combine(root, ".git"), new ReviewOptions());
        Assert.DoesNotThrow(context.AssertUnchanged);
        Assert.Throws<InvalidOperationException>(() => _ = context.Changes);
        var full = new AnalysisContext(plain, new ReviewOptions { Scope = "full" });
        Assert.That(full.RootPath, Is.EqualTo(plain));
        Assert.DoesNotThrow(full.AssertUnchanged);
    }

    [Test]
    public void PullRequestIsExactCleanAndRevalidatedWithoutNetwork()
    {
        Write("source.cs", "committed pr\n");
        var head = Commit("PR change");
        Git("remote", "add", "origin", "https://github.com/example/repository.git");
        var runner = new PullRequestCommands(commands, Metadata(baseline, head));
        var logs = new List<string>();
        var snapshot = new ChangeSnapshot(root, new ReviewOptions { Scope = "pr", Pr = "12" }, runner, logs.Add);
        Assert.That(logs, Is.EqualTo(new[]
        {
            $"Change-scoped review: mode=pr; scope=pr; base={baseline}; head={head}; "
                + "local edits=excluded (exact PR head; clean target required); files=1; hunks=1."
        }));
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.BaseCommit, Is.EqualTo(baseline));
            Assert.That(snapshot.HeadCommit, Is.EqualTo(head));
            Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "source.cs" }));
            Assert.That(snapshot.ReadSource("source.cs", 1, 1), Is.EqualTo("1: committed pr"));
            Assert.That(runner.GhCalls, Is.EqualTo(2));
            Assert.That(runner.FetchCalls, Is.EqualTo(0));
        });
        runner.Metadata = Metadata(head, head);
        Assert.Throws<InvalidOperationException>(snapshot.AssertUnchanged);
    }

    [Test]
    public void PullRequestRejectsWrongHeadDirtyTargetAndWrongRepository()
    {
        Git("remote", "add", "origin", "https://github.com/example/repository.git");
        var runner = new PullRequestCommands(commands, Metadata(baseline, new string('a', 40)));
        Assert.That(Assert.Throws<InvalidOperationException>(() =>
            _ = new ChangeSnapshot(root, new ReviewOptions { Scope = "pr", Pr = "12" }, runner))!.Message, Does.Contain("exact head"));
        runner.Metadata = Metadata(baseline, baseline);
        Write("untracked.cs", "local\n");
        Assert.That(Assert.Throws<InvalidOperationException>(() =>
            _ = new ChangeSnapshot(root, new ReviewOptions { Scope = "pr", Pr = "12" }, runner))!.Message, Does.Contain("clean target"));
        File.Delete(Path.Combine(root, "untracked.cs"));
        Git("remote", "set-url", "origin", "https://github.com/other/repository.git");
        Assert.That(Assert.Throws<InvalidOperationException>(() =>
            _ = new ChangeSnapshot(root, new ReviewOptions { Scope = "pr", Pr = "12" }, runner))!.Message, Does.Contain("not a remote"));
        Assert.That(runner.FetchCalls, Is.EqualTo(0));
    }

    [Test]
    public void PullRequestMissingBaseFetchesOnlyTheExactObjectAndFailsExplicitly()
    {
        Git("remote", "add", "origin", "https://github.com/example/repository.git");
        var runner = new PullRequestCommands(commands, Metadata(new string('a', 40), baseline));
        Assert.That(Assert.Throws<InvalidOperationException>(() =>
            _ = new ChangeSnapshot(root, new ReviewOptions { Scope = "pr", Pr = "12" }, runner))!.Message, Does.Contain("exact PR commit"));
        Assert.That(runner.FetchCalls, Is.EqualTo(1));
        Assert.That(runner.LastFetchArguments, Is.EqualTo(new[]
        {
            "--no-pager", "-c", "core.fsmonitor=false", "fetch", "--no-tags", "--no-recurse-submodules",
            "--", "origin", new string('a', 40)
        }));
    }

    [Test]
    public void HunkParsingPreservesOmittedCountsZeroCountsAndFunctionContext()
    {
        var patch = "diff --git a/source.cs b/source.cs\n--- a/source.cs\n+++ b/source.cs\n"
            + "@@ -1 +2 @@ function name\n-old\n+new\n"
            + "@@ -3,2 +4,0 @@\n-deleted\n-lines\n"
            + "@@ -5,0 +6,2 @@\n+added\n+lines\n"
            + "+@@ -99 +99 @@ is source text, not a hunk\n"
            + " @@ -88 +88 @@ is context, not a hunk\n";
        Assert.That(ChangeSnapshot.ParseRanges(patch), Is.EqualTo(new[]
        {
            new ChangedRange(1, 1, 2, 1),
            new ChangedRange(3, 2, 4, 0),
            new ChangedRange(5, 0, 6, 2)
        }));
        Assert.That(ChangeSnapshot.ParseRanges("Binary files a/source.dat and b/source.dat differ\n"), Is.Empty);
    }

    [TestCase("@@ -1 +1 @")]
    [TestCase("@@ -1 +1")]
    [TestCase("@@  -1 +1 @@")]
    [TestCase("@@@ -1 +1 @@@")]
    [TestCase("@@ 1 +1 @@")]
    [TestCase("@@ -1 -1 @@")]
    [TestCase("@@ -1, +1 @@")]
    [TestCase("@@ -1 +1,-1 @@")]
    [TestCase("@@ -1,2,3 +1 @@")]
    [TestCase("@@ -2147483648 +1 @@")]
    [TestCase("@@ -1 +1,2147483648 @@")]
    public void MalformedHunkHeadersFailExplicitly(string header)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ChangeSnapshot.ParseRanges(header + "\n"));
        Assert.That(error!.Message, Does.Contain("malformed or unsupported diff hunk header"));
    }

    [TestCase("https://GitHub.com/Example/Repository.git", "github.com/example/repository")]
    [TestCase("ssh://git@github.com/Example/Repository.git", "github.com/example/repository")]
    [TestCase("git@github.com:Example/Repository.git", "github.com/example/repository")]
    [TestCase("github.com:Example/Repository", "github.com/example/repository")]
    [TestCase("git@@github.com:example/repository.git", null)]
    [TestCase("@github.com:example/repository.git", null)]
    [TestCase("git@:example/repository.git", null)]
    [TestCase("git@github.com:", null)]
    [TestCase("git@github.com:example/repository:extra", null)]
    [TestCase("git@github.com:example/repository/extra", null)]
    [TestCase("git@github.com:example/../repository", null)]
    [TestCase("github.com/example/repository", null)]
    public void RepositoryIdentityParsingHandlesUrisScpAndMalformedRemotes(string remote, string? expected)
    {
        Assert.That(ChangeSnapshot.RepositoryIdentity(remote), Is.EqualTo(expected));
    }

    [TestCase("auto", null, null, "all", true)]
    [TestCase("full", null, null, "all", true)]
    [TestCase("branch", "HEAD", null, "changes", true)]
    [TestCase("pr", null, "12", "all", true)]
    [TestCase("pr", null, "https://github.com/example/repository/pull/12", "all", true)]
    [TestCase("auto", null, "12", "all", false)]
    [TestCase("full", "HEAD", null, "all", false)]
    [TestCase("auto", "HEAD", "12", "all", false)]
    [TestCase("auto", "--help", null, "all", false)]
    [TestCase("pr", null, null, "all", false)]
    [TestCase("branch", null, "12", "all", false)]
    [TestCase("auto", null, "--repo=bad", "all", false)]
    [TestCase("auto", null, "0", "all", false)]
    [TestCase("auto", null, "https://github.com/example/repository/pull/12?x=y", "all", false)]
    [TestCase("auto", null, null, "changed", false)]
    public void OptionsValidateConflictsAndRejectOptionInjection(string scope, string? @base, string? pr, string report, bool valid)
    {
        var options = new ReviewOptions { Scope = scope, Base = @base, Pr = pr, ReportScope = report };
        if (valid)
            Assert.DoesNotThrow(options.Validate);
        else
            Assert.Throws<ArgumentException>(options.Validate);
    }

    private ChangeSnapshot Snapshot() => new AnalysisContext(root, new ReviewOptions { Base = baseline }, _ => { }).Changes;

    private void Write(string path, string text)
    {
        var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text, new UTF8Encoding(false));
    }

    private string Commit(string message)
    {
        Git("add", "--all");
        Git("commit", "--quiet", "-m", message);
        return Git("rev-parse", "HEAD").Trim();
    }

    private string Git(params string[] arguments)
    {
        var result = commands.Run(root, "git", arguments);
        Assert.That(result.ExitCode, Is.EqualTo(0), $"git {string.Join(' ', arguments)}: {result.Error}");
        return result.Output;
    }

    private static string Metadata(string @base, string head) => JsonSerializer.Serialize(new
    {
        baseRefOid = @base,
        headRefOid = head,
        baseRefName = "trunk",
        headRefName = "feature",
        headRepository = new { name = "repository" },
        headRepositoryOwner = new { login = "example" },
        url = "https://github.com/example/repository/pull/12"
    });

    private sealed class PullRequestCommands(ChangeCommandRunner git, string metadata) : ChangeCommandRunner
    {
        public string Metadata { get; set; } = metadata;
        public int GhCalls { get; private set; }
        public int FetchCalls { get; private set; }
        public string[]? LastFetchArguments { get; private set; }

        public override ChangeCommandResult Run(string directory, string executable, params string[] arguments)
        {
            if (executable == "gh")
            {
                GhCalls++;
                return new(0, Metadata, "");
            }
            if (arguments.Contains("fetch"))
            {
                FetchCalls++;
                LastFetchArguments = arguments;
                return new(1, "", "fixture: exact object is unavailable");
            }
            return git.Run(directory, executable, arguments);
        }
    }
}
