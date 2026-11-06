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
    public async Task SetUp()
    {
        root = Path.Combine(Directory.GetCurrentDirectory(), ".change-snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await Git("init", "--initial-branch=trunk");
        await Git("config", "user.email", "snapshot@example.invalid");
        await Git("config", "user.name", "Snapshot Test");
        await Git("config", "core.autocrlf", "false");
        await Git("config", "commit.gpgsign", "false");
        Write("source.cs", "one\ntwo\nthree\nfour\nfive\n");
        Write("unchanged.cs", "unchanged needle\nsecond\n");
        baseline = (await Commit("baseline"));
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
    public async Task BranchComparisonIncludesCommittedStagedUnstagedAndUntrackedOnce()
    {
        Write("source.cs", "one\ncommitted\nthree\nfour\nfive\n");
        await Commit("feature");
        Write("source.cs", "one\ncommitted\nstaged\nfour\nfive\n");
        await Git("add", "source.cs");
        Write("source.cs", "one\ncommitted\nstaged\nunstaged\nfive\n");
        Write("new file.cs", "new\ncontent\n");

        var snapshot = (await Snapshot());

        Assert.That(snapshot.BaseCommit, Is.EqualTo(baseline));
        Assert.That(snapshot.HeadCommit, Is.EqualTo((await Git("rev-parse", "HEAD")).Trim()));
        Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "new file.cs", "source.cs" }));
        var modified = snapshot.Files.Single(file => file.Path == "source.cs");
        await Assert.MultipleAsync(async () =>
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
            Assert.That((await snapshot.ReadSourceAsync("new file.cs", 1, 2)), Is.EqualTo("1: new\n2: content"));
        });
        await snapshot.AssertUnchangedAsync();
    }

    [Test]
    public async Task StagedEditCancelledByWorkingTreeProducesZeroReview()
    {
        Write("source.cs", "changed\n");
        await Git("add", "source.cs");
        Write("source.cs", "one\ntwo\nthree\nfour\nfive\n");
        var logs = new List<string>();
        var snapshot = (await new AnalysisContext(root, new ReviewOptions { Base = baseline }, logs.Add).GetChangesAsync());
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
    public async Task BranchScopeSummaryIsEmittedOnlyOnceWhenLazySnapshotResolves()
    {
        Write("source.cs", "one\nstaged\nthree\nfour\nfive\n");
        await Git("add", "source.cs");
        Write("source.cs", "one\nstaged\nunstaged\nfour\nfive\n");
        Write("new.cs", "untracked\n");
        var logs = new List<string>();
        var context = new AnalysisContext(root, new ReviewOptions { Scope = "branch", Base = baseline }, logs.Add);
        await context.AssertUnchangedAsync();
        Assert.That(logs, Is.Empty);

        var snapshot = (await context.GetChangesAsync());
        Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "new.cs", "source.cs" }));
        Assert.That(logs, Is.EqualTo(new[]
        {
            $"Change-scoped review: mode=branch; scope=branch; base={baseline}; head={baseline}; "
                + "local edits=included (staged, unstaged, nonignored untracked); files=2; hunks=2."
        }));

        Assert.That((await context.GetChangesAsync()), Is.SameAs(snapshot));
        await context.AssertUnchangedAsync();
        Assert.That(logs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RenamesAndDeletionsPreserveOldPathsAndPureDeletionAnchors()
    {
        await Git("mv", "unchanged.cs", "renamed space.cs");
        Write("renamed space.cs", "unchanged needle\nsecond\nthird\n");
        Write("source.cs", "one\ntwo\nfour\nfive\n");
        var snapshot = (await Snapshot());
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
        await Git("rm", "--force", "source.cs");
        snapshot = (await Snapshot());
        Assert.That(snapshot.Files.Single(file => file.Path == "source.cs").IsDeleted, Is.True);
        Assert.That(snapshot.IsChanged("source.cs", 4), Is.True);
        Assert.That((await snapshot.ReadSourceAsync("source.cs", 2, 2, "base")), Is.EqualTo("2: two\n3: three"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync("source.cs", 1, 1));
    }

    [Test]
    public async Task SubfolderPathsAreTargetRelativeWhileSourcesAreRepositoryRelative()
    {
        Write("nested folder/inside.cs", "inside\n");
        await Commit("nested baseline");
        var nestedBase = (await Git("rev-parse", "HEAD")).Trim();
        Write("nested folder/inside.cs", "inside changed\n");
        Write("source.cs", "outside changed\n");
        var target = Path.Combine(root, "nested folder");
        var snapshot = (await new AnalysisContext(target, new ReviewOptions { Base = nestedBase }).GetChangesAsync());
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "nested folder/inside.cs" }));
            Assert.That(snapshot.ToRepositoryPath("inside.cs"), Is.EqualTo("nested folder/inside.cs"));
            Assert.That(snapshot.ToRepositoryPath(Path.Combine(target, "inside.cs")), Is.EqualTo("nested folder/inside.cs"));
            Assert.That(snapshot.IsChanged("inside.cs", 1), Is.True);
            Assert.That((await snapshot.ReadSourceAsync("nested folder/inside.cs", 1, 1)), Is.EqualTo("1: inside changed"));
        });
        Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync("source.cs", 1, 1));
        Assert.Throws<InvalidOperationException>(() => snapshot.ToRepositoryPath(Path.Combine(root, "source.cs")));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync("inside.cs", 1, 1));
    }

    [Test]
    public async Task SourceToolsReadUnchangedSourceAndBoundTheirResults()
    {
        Write("source.cs", "changed\n");
        Write("many.cs", string.Concat(Enumerable.Repeat("needle\n", 45)));
        var snapshot = (await Snapshot());
        Assert.That((await snapshot.ReadSourceAsync("unchanged.cs", 1, 2)), Is.EqualTo("1: unchanged needle\n2: second"));
        Assert.That((await snapshot.ReadSourceAsync("source.cs", 1, 1, "base")), Is.EqualTo("1: one"));
        var matches = (await snapshot.SearchSourceAsync("needle", 2)).Split('\n');
        Assert.That(matches, Has.Length.EqualTo(3));
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(matches[0], Is.EqualTo("many.cs:1: needle"));
            Assert.That(matches[1], Is.EqualTo("many.cs:2: needle"));
            Assert.That(matches[2], Does.StartWith("[incomplete:"));
            Assert.That((await snapshot.SearchSourceAsync("no-such-token")), Is.EqualTo("No matches."));
        });
        Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync("source.cs", 1, 201));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync("source.cs", 0, 1));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync("source.cs", 2, 1));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync("source.cs", 1, 1, "worktree"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.SearchSourceAsync("needle", 41));
    }

    [Test]
    public async Task SourceByteLimitsFailExplicitlyRatherThanTruncating()
    {
        Write("large.cs", new string('é', 17000) + "\n");
        var snapshot = (await Snapshot());
        Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync("large.cs", 1, 1));
        var search = (await snapshot.SearchSourceAsync("é"));
        Assert.That(search, Does.Contain("[incomplete:"));
        Assert.That(Encoding.UTF8.GetByteCount(search), Is.LessThanOrEqualTo(ChangeSnapshot.MaxSourceResponseBytes));
    }

    [Test]
    public async Task BinaryChangesAreExposedButCannotBeReadAsSource()
    {
        File.WriteAllBytes(Path.Combine(root, "binary.dat"), new byte[] { 0, 1, 2, 255 });
        var snapshot = (await Snapshot());
        Assert.That(snapshot.Files.Single().Path, Is.EqualTo("binary.dat"));
        Assert.That(snapshot.Files.Single().IsBinary, Is.True);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync("binary.dat", 1, 1));
        Assert.That((await snapshot.SearchSourceAsync("needle")), Does.Contain("[incomplete:"));
    }

    [Test]
    public async Task UnsafeAndSensitiveSourceRequestsAreRejected()
    {
        Write(".gitignore", "ignored.cs\n");
        Write("ignored.cs", "ignored needle\n");
        Write(".env", "SECRET=needle\n");
        Write(".env.production", "SECRET=needle\n");
        Write("bin/generated.cs", "generated needle\n");
        Write("private.key", "secret needle\n");
        var snapshot = (await Snapshot());
        foreach (var path in new[] { "../outside", ".git/config", ".env", ".env.production", "private.key", "bin/generated.cs", "ignored.cs" })
            Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync(path, 1, 1), path);
        Assert.That((await snapshot.SearchSourceAsync("needle")), Is.EqualTo("unchanged.cs:1: unchanged needle"));
        Assert.Throws<InvalidOperationException>(() => snapshot.ToRepositoryPath("../source.cs"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync(Path.Combine(root, "source.cs"), 1, 1));
        if (OperatingSystem.IsWindows())
            Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync("source.cs:stream", 1, 1));
    }

    [Test]
    public async Task ReparsePointTraversalIsRejectedBeforeSourceIsRead()
    {
        var linked = Path.Combine(root, "linked");
        var actual = Path.Combine(root, "actual");
        Directory.CreateDirectory(actual);
        Write("actual/linked.cs", "must not traverse\n");
        var snapshot = (await Snapshot());
        if (OperatingSystem.IsWindows())
        {
            var junction = await commands.RunAsync(
                root, "cmd.exe", ["/c", "mklink", "/J", linked, actual]);
            Assert.That(junction.ExitCode, Is.EqualTo(0), junction.Error);
        }
        else
        {
            Directory.CreateSymbolicLink(linked, actual);
        }
        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync("linked/linked.cs", 1, 1));
            Assert.Throws<InvalidOperationException>(() => snapshot.ToRepositoryPath("linked/linked.cs"));
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                _ = (await new AnalysisContext(linked, new ReviewOptions { Base = baseline }).GetChangesAsync()));
        }
        finally
        {
            Directory.Delete(linked);
        }
    }

    [Test]
    public async Task GitPathProtocolPreservesSpacesUnicodeAndPathspecMetacharacters()
    {
        Write("unicode λ [1].cs", "initial\n");
        var nameBase = (await Commit("names"));
        Write("unicode λ [1].cs", "modified\n");
        Write("new [2] space.cs", "untracked\n");
        var snapshot = (await new AnalysisContext(root, new ReviewOptions { Base = nameBase }).GetChangesAsync());
        Assert.That(snapshot.Files.Select(file => file.Path),
            Is.EqualTo(new[] { "new [2] space.cs", "unicode λ [1].cs" }));
        Assert.That((await snapshot.ReadSourceAsync("unicode λ [1].cs", 1, 1)), Is.EqualTo("1: modified"));
        Assert.That(snapshot.Files.Single(file => file.Status == "M").Patch, Does.Contain("+modified"));
    }

    [Test]
    public void PatchLimitFailsExplicitlyWithoutReturningAPartialSnapshot()
    {
        Write("over-limit.cs", new string('x', ChangeSnapshot.MaxPatchBytes) + "\n");
        var failure = Assert.ThrowsAsync<InvalidOperationException>(async () => _ = (await Snapshot()));
        Assert.That(failure!.Message, Does.Contain("no patch was truncated"));
    }

    [Test]
    public async Task MergeBaseExcludesChangesMadeOnlyOnTheBaseBranch()
    {
        await Git("checkout", "--quiet", "-b", "feature");
        Write("source.cs", "feature\n");
        var feature = (await Commit("feature"));
        await Git("checkout", "--quiet", "trunk");
        Write("base-only.cs", "base branch\n");
        await Commit("base diverged");
        await Git("checkout", "--quiet", "feature");
        var snapshot = (await new AnalysisContext(root, new ReviewOptions { Base = "trunk" }).GetChangesAsync());
        Assert.That(snapshot.BaseCommit, Is.EqualTo(baseline));
        Assert.That(snapshot.HeadCommit, Is.EqualTo(feature));
        Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "source.cs" }));
    }

    [Test]
    public async Task EmptyAndNoFinalNewlineFilesHaveExactRanges()
    {
        Write("empty.cs", "");
        Write("no-newline.cs", "last line");
        var snapshot = (await Snapshot());
        Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "empty.cs", "no-newline.cs" }));
        Assert.That(snapshot.Files[0].Ranges, Is.Empty);
        Assert.That(snapshot.Files[1].Ranges, Is.EqualTo(new[] { new ChangedRange(0, 0, 1, 1) }));
        Assert.That(snapshot.Files[1].Patch, Does.Contain("+last line\n\\ No newline at end of file\n"));
        Assert.That((await snapshot.ReadSourceAsync("no-newline.cs", 1, 1)), Is.EqualTo("1: last line"));
        Assert.That(snapshot.IsChanged("empty.cs", 0), Is.True);
    }

    [TestCase("changed")]
    [TestCase("unchanged")]
    [TestCase("new")]
    [TestCase("index")]
    [TestCase("head")]
    public async Task SourceAndGitChangesInvalidateTheSnapshot(string edit)
    {
        Write("source.cs", "initial change\n");
        var snapshot = (await Snapshot());
        switch (edit)
        {
            case "changed": Write("source.cs", "later change\n"); break;
            case "unchanged": Write("unchanged.cs", "edited unchanged\n"); break;
            case "new": Write("later.cs", "new\n"); break;
            case "index": await Git("add", "source.cs"); break;
            case "head": await Commit("moved head"); break;
        }
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await snapshot.AssertUnchangedAsync());
        if (edit == "unchanged")
            Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.ReadSourceAsync("unchanged.cs", 1, 1));
    }

    [Test]
    public async Task FingerprintsDetectSameLengthContentChangesWithRestoredTimestamp()
    {
        var snapshot = (await Snapshot());
        var path = Path.Combine(root, "unchanged.cs");
        var time = File.GetLastWriteTimeUtc(path);
        Write("unchanged.cs", "UNCHANGED needle\nsecond\n");
        File.SetLastWriteTimeUtc(path, time);
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await snapshot.AssertUnchangedAsync());
        Assert.ThrowsAsync<InvalidOperationException>(async () => await snapshot.SearchSourceAsync("needle"));
    }

    [Test]
    public async Task DefaultBaseUsesRemoteSymbolicHeadWithoutGuessingBranchNames()
    {
        await Git("remote", "add", "upstream", "https://github.com/example/repository.git");
        await Git("update-ref", "refs/remotes/upstream/develop", baseline);
        await Git("symbolic-ref", "refs/remotes/upstream/HEAD", "refs/remotes/upstream/develop");
        Write("source.cs", "changed\n");
        var snapshot = (await new AnalysisContext(root, new ReviewOptions()).GetChangesAsync());
        Assert.That(snapshot.BaseCommit, Is.EqualTo(baseline));
        Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "source.cs" }));
        await Git("remote", "add", "other", "https://github.com/example/other.git");
        Assert.ThrowsAsync<InvalidOperationException>(async () => _ = (await new AnalysisContext(root, new ReviewOptions()).GetChangesAsync()));
        await Git("config", "branch.trunk.remote", "upstream");
        Assert.That((await new AnalysisContext(root, new ReviewOptions()).GetChangesAsync()).BaseCommit, Is.EqualTo(baseline));
    }

    [Test]
    public async Task MissingBaseAndMissingDefaultHeadFailWithActionableMessages()
    {
        var missing = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            _ = (await new AnalysisContext(root, new ReviewOptions { Base = "not-a-real-ref" }).GetChangesAsync()));
        Assert.That(missing!.Message, Does.Contain("--base"));
        await Git("remote", "add", "upstream", "https://github.com/example/repository.git");
        var missingHead = Assert.ThrowsAsync<InvalidOperationException>(async () => _ = (await new AnalysisContext(root, new ReviewOptions()).GetChangesAsync()));
        Assert.That(missingHead!.Message, Does.Contain("git remote set-head"));
    }

    [Test]
    public async Task LinkedWorktreeUsesItsOwnHeadAndLocalEdits()
    {
        var linked = Path.Combine(root, "linked-worktree");
        await Git("worktree", "add", "-b", "linked", linked);
        try
        {
            File.WriteAllText(Path.Combine(linked, "source.cs"), "linked edit\n", new UTF8Encoding(false));
            var snapshot = (await new AnalysisContext(linked, new ReviewOptions { Base = baseline }).GetChangesAsync());
            Assert.That(snapshot.RepositoryRoot, Is.EqualTo(linked));
            Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "source.cs" }));
            Assert.That((await snapshot.ReadSourceAsync("source.cs", 1, 1)), Is.EqualTo("1: linked edit"));
        }
        finally { await Git("worktree", "remove", "--force", linked); }
    }

    [Test]
    public async Task ContextStaysLazyForNonRepositoryAndFullScope()
    {
        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(plain);
        // The Git directory itself is not a worktree even though its parent is.
        var context = new AnalysisContext(Path.Combine(root, ".git"), new ReviewOptions());
        Assert.DoesNotThrowAsync(async () => await context.AssertUnchangedAsync());
        Assert.ThrowsAsync<InvalidOperationException>(async () => _ = (await context.GetChangesAsync()));
        var full = new AnalysisContext(plain, new ReviewOptions { Scope = "full" });
        Assert.That(full.RootPath, Is.EqualTo(plain));
        Assert.DoesNotThrowAsync(async () => await full.AssertUnchangedAsync());
    }

    [Test]
    public async Task PullRequestIsExactCleanAndRevalidatedWithoutNetwork()
    {
        Write("source.cs", "committed pr\n");
        var head = (await Commit("PR change"));
        await Git("remote", "add", "origin", "https://github.com/example/repository.git");
        var runner = new PullRequestCommands(commands, Metadata(baseline, head));
        var logs = new List<string>();
        var snapshot = await ChangeSnapshot.CaptureAsync(
            root, new ReviewOptions { Scope = "pr", Pr = "12" },
            runner, logs.Add, CancellationToken.None);
        Assert.That(logs, Is.EqualTo(new[]
        {
            $"Change-scoped review: mode=pr; scope=pr; base={baseline}; head={head}; "
                + "local edits=excluded (exact PR head; clean target required); files=1; hunks=1."
        }));
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(snapshot.BaseCommit, Is.EqualTo(baseline));
            Assert.That(snapshot.HeadCommit, Is.EqualTo(head));
            Assert.That(snapshot.Files.Select(file => file.Path), Is.EqualTo(new[] { "source.cs" }));
            Assert.That((await snapshot.ReadSourceAsync("source.cs", 1, 1)), Is.EqualTo("1: committed pr"));
            Assert.That(runner.GhCalls, Is.EqualTo(2));
            Assert.That(runner.FetchCalls, Is.EqualTo(0));
        });
        runner.Metadata = Metadata(head, head);
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await snapshot.AssertUnchangedAsync());
    }

    [Test]
    public async Task PullRequestRejectsWrongHeadDirtyTargetAndWrongRepository()
    {
        await Git("remote", "add", "origin", "https://github.com/example/repository.git");
        var runner = new PullRequestCommands(commands, Metadata(baseline, new string('a', 40)));
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ChangeSnapshot.CaptureAsync(root, new ReviewOptions { Scope = "pr", Pr = "12" },
                runner, null, CancellationToken.None))!.Message, Does.Contain("exact head"));
        runner.Metadata = Metadata(baseline, baseline);
        Write("untracked.cs", "local\n");
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ChangeSnapshot.CaptureAsync(root, new ReviewOptions { Scope = "pr", Pr = "12" },
                runner, null, CancellationToken.None))!.Message, Does.Contain("clean target"));
        File.Delete(Path.Combine(root, "untracked.cs"));
        await Git("remote", "set-url", "origin", "https://github.com/other/repository.git");
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ChangeSnapshot.CaptureAsync(root, new ReviewOptions { Scope = "pr", Pr = "12" },
                runner, null, CancellationToken.None))!.Message, Does.Contain("not a remote"));
        Assert.That(runner.FetchCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task PullRequestMissingBaseFetchesOnlyTheExactObjectAndFailsExplicitly()
    {
        await Git("remote", "add", "origin", "https://github.com/example/repository.git");
        var runner = new PullRequestCommands(commands, Metadata(new string('a', 40), baseline));
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ChangeSnapshot.CaptureAsync(root, new ReviewOptions { Scope = "pr", Pr = "12" },
                runner, null, CancellationToken.None))!.Message, Does.Contain("exact PR commit"));
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

    private async Task<ChangeSnapshot> Snapshot() => (await new AnalysisContext(root, new ReviewOptions { Base = baseline }, _ => { }).GetChangesAsync());

    private void Write(string path, string text)
    {
        var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text, new UTF8Encoding(false));
    }

    private async Task<string> Commit(string message)
    {
        await Git("add", "--all");
        await Git("commit", "--quiet", "-m", message);
        return (await Git("rev-parse", "HEAD")).Trim();
    }

    private async Task<string> Git(params string[] arguments)
    {
        var result = (await commands.RunAsync(root, "git", arguments));
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

        public override ValueTask<ChangeCommandResult> RunAsync(
            string directory, string executable, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            if (executable == "gh")
            {
                GhCalls++;
                return ValueTask.FromResult(new ChangeCommandResult(0, Metadata, ""));
            }
            if (arguments.Contains("fetch"))
            {
                FetchCalls++;
                LastFetchArguments = arguments.ToArray();
                return ValueTask.FromResult(
                    new ChangeCommandResult(1, "", "fixture: exact object is unavailable"));
            }
            return git.RunAsync(directory, executable, arguments, cancellationToken);
        }
    }
}
