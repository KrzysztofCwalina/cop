using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests.Lang.Cli;

[TestFixture]
public class VersionNotifierTests
{
    private static VersionNotifier.ReleaseNote Note(string version, bool approved, params string[] features)
        => new(version, approved, features);

    // ── SelectNewFeatures ────────────────────────────────────────────

    [Test]
    public void SelectNewFeatures_ReturnsApprovedVersionsAbovePrevious_IncludingSkipped_OldestFirst()
    {
        var notes = new[]
        {
            Note("2026.6.22.1", true, "f1"),
            Note("2026.6.22.2", true, "f2"),
            Note("2026.6.22.3", true, "f3"),
        };
        // User was on .1 and jumped to .3 — both .2 and .3 (the skipped + newest) must show.
        var result = VersionNotifier.SelectNewFeatures(notes, new Version("2026.6.22.1"), new Version("2026.6.22.3"));

        Assert.That(result.Select(n => n.Version), Is.EqualTo(new[] { "2026.6.22.2", "2026.6.22.3" }));
    }

    [Test]
    public void SelectNewFeatures_ExcludesUnapprovedVersions()
    {
        var notes = new[]
        {
            Note("2026.6.22.2", true, "f2"),
            Note("2026.6.22.3", false, "f3-unapproved"),
        };
        var result = VersionNotifier.SelectNewFeatures(notes, new Version("2026.6.22.1"), new Version("2026.6.22.3"));

        Assert.That(result.Select(n => n.Version), Is.EqualTo(new[] { "2026.6.22.2" }));
    }

    [Test]
    public void SelectNewFeatures_ExcludesSeenAndFutureVersions()
    {
        var notes = new[]
        {
            Note("2026.6.22.1", true, "f1"),   // == seen, excluded
            Note("2026.6.22.2", true, "f2"),   // in range
            Note("2026.6.22.9", true, "f9"),   // > current, excluded
        };
        var result = VersionNotifier.SelectNewFeatures(notes, new Version("2026.6.22.1"), new Version("2026.6.22.2"));

        Assert.That(result.Select(n => n.Version), Is.EqualTo(new[] { "2026.6.22.2" }));
    }

    [Test]
    public void SelectNewFeatures_SkipsEntriesWithNoFeatures()
    {
        var notes = new[] { Note("2026.6.22.2", true /* no features */) };
        var result = VersionNotifier.SelectNewFeatures(notes, new Version("2026.6.22.1"), new Version("2026.6.22.2"));
        Assert.That(result, Is.Empty);
    }

    // ── ShouldCheckRemote (daily throttle) ───────────────────────────

    [Test]
    public void ShouldCheckRemote_TrueWhenNeverChecked()
        => Assert.That(VersionNotifier.ShouldCheckRemote(null, DateTime.UtcNow), Is.True);

    [Test]
    public void ShouldCheckRemote_FalseWithinADay()
    {
        var now = DateTime.UtcNow;
        Assert.That(VersionNotifier.ShouldCheckRemote(now.AddHours(-5), now), Is.False);
    }

    [Test]
    public void ShouldCheckRemote_TrueAfterADay()
    {
        var now = DateTime.UtcNow;
        Assert.That(VersionNotifier.ShouldCheckRemote(now.AddHours(-25), now), Is.True);
    }

    // ── IsUpdateAvailable ────────────────────────────────────────────

    [Test]
    public void IsUpdateAvailable_TrueWhenTagNewer()
        => Assert.That(VersionNotifier.IsUpdateAvailable("v2026.6.22.5", new Version("2026.6.22.4")), Is.True);

    [Test]
    public void IsUpdateAvailable_FalseWhenSameOrOlderOrMissing()
    {
        var current = new Version("2026.6.22.4");
        Assert.That(VersionNotifier.IsUpdateAvailable("v2026.6.22.4", current), Is.False);
        Assert.That(VersionNotifier.IsUpdateAvailable("v2026.6.22.3", current), Is.False);
        Assert.That(VersionNotifier.IsUpdateAvailable(null, current), Is.False);
        Assert.That(VersionNotifier.IsUpdateAvailable("not-a-version", current), Is.False);
    }

    // ── ParseVersion ─────────────────────────────────────────────────

    [Test]
    public void ParseVersion_HandlesTagPrefixAndPlainAndInvalid()
    {
        Assert.That(VersionNotifier.ParseVersion("v2026.6.22.4"), Is.EqualTo(new Version("2026.6.22.4")));
        Assert.That(VersionNotifier.ParseVersion("2026.6.22.4"), Is.EqualTo(new Version("2026.6.22.4")));
        Assert.That(VersionNotifier.ParseVersion("  V2026.6.22.4  "), Is.EqualTo(new Version("2026.6.22.4")));
        Assert.That(VersionNotifier.ParseVersion("garbage"), Is.Null);
        Assert.That(VersionNotifier.ParseVersion(null), Is.Null);
    }

    // ── Run (orchestration) ──────────────────────────────────────────

    private static readonly VersionNotifier.ReleaseNote[] SampleNotes =
    {
        new("2026.6.22.1", true, new[] { "f1" }),
        new("2026.6.22.2", true, new[] { "f2" }),
        new("2026.6.22.3", true, new[] { "f3" }),
    };

    [Test]
    public void Run_OnUpgrade_EmitsWhatsNewIncludingSkipped_AndAdvancesSeenVersion()
    {
        var now = DateTime.UtcNow;
        var state = new VersionNotifier.State(now, "v2026.6.22.3", "2026.6.22.1"); // recent check; seen .1
        var (newState, messages) = VersionNotifier.Run(state, SampleNotes, new Version("2026.6.22.3"), now, () => null);

        var text = string.Join("\n", messages.Select(m => m.Text));
        Assert.That(text, Does.Contain("What's new"));
        Assert.That(text, Does.Contain("f2"), "skipped version's feature must show");
        Assert.That(text, Does.Contain("f3"));
        Assert.That(text, Does.Not.Contain("f1"), "the already-seen version must not show");
        Assert.That(newState.SeenVersion, Is.EqualTo("2026.6.22.3"));
    }

    [Test]
    public void Run_FirstRun_NoWhatsNew_RecordsSeenVersion()
    {
        var now = DateTime.UtcNow;
        var state = new VersionNotifier.State(now, null, null); // never seen before
        var (newState, messages) = VersionNotifier.Run(state, SampleNotes, new Version("2026.6.22.3"), now, () => null);

        Assert.That(messages.Any(m => m.Text.Contains("What's new")), Is.False);
        Assert.That(newState.SeenVersion, Is.EqualTo("2026.6.22.3"));
    }

    [Test]
    public void Run_UpdateAvailable_EmitsYellowReminder()
    {
        var now = DateTime.UtcNow;
        var state = new VersionNotifier.State(now, "v2026.6.22.9", "2026.6.22.3"); // cached newer tag
        var (_, messages) = VersionNotifier.Run(state, SampleNotes, new Version("2026.6.22.3"), now, () => null);

        var update = messages.SingleOrDefault(m => m.Color == ConsoleColor.Yellow);
        Assert.That(update, Is.Not.Null);
        Assert.That(update!.Text, Does.Contain("cop update"));
        Assert.That(update.Text, Does.Contain("2026.6.22.9"));
    }

    [Test]
    public void Run_Throttled_DoesNotFetch_WhenLastCheckRecent()
    {
        var now = DateTime.UtcNow;
        bool fetched = false;
        var state = new VersionNotifier.State(now.AddHours(-1), "v2026.6.22.3", "2026.6.22.3");
        var (newState, _) = VersionNotifier.Run(state, SampleNotes, new Version("2026.6.22.3"), now,
            () => { fetched = true; return "v2026.6.99.9"; });

        Assert.That(fetched, Is.False, "must not hit the network within the throttle window");
        Assert.That(newState.LastCheck, Is.EqualTo(now.AddHours(-1)), "lastCheck unchanged when throttled");
    }

    [Test]
    public void Run_Stale_Fetches_UpdatesLatestTagAndLastCheck()
    {
        var now = DateTime.UtcNow;
        var state = new VersionNotifier.State(now.AddHours(-48), "v2026.6.22.3", "2026.6.22.3");
        var (newState, messages) = VersionNotifier.Run(state, SampleNotes, new Version("2026.6.22.3"), now,
            () => "v2026.6.99.9");

        Assert.That(newState.LastCheck, Is.EqualTo(now));
        Assert.That(newState.LatestTag, Is.EqualTo("v2026.6.99.9"));
        Assert.That(messages.Any(m => m.Color == ConsoleColor.Yellow && m.Text.Contains("2026.6.99.9")), Is.True);
    }

    // ── Embedded data + state I/O ────────────────────────────────────

    [Test]
    public void EmbeddedReleaseNotes_LoadAndDriveWhatsNew()
    {
        var notes = VersionNotifier.LoadReleaseNotes();
        Assert.That(notes.Count, Is.GreaterThanOrEqualTo(4), "release-notes.json must be embedded and parsed");

        var v3 = notes.Single(n => n.Version == "2026.6.22.3");
        Assert.That(v3.Approved, Is.True);
        Assert.That(v3.Features, Is.Not.Empty);

        var current = notes.Single(n => n.Version == "2026.6.23.1");
        Assert.That(current.Approved, Is.True, "the released version must be approved");

        // Upgrading .2 -> .3 surfaces .3's real, approved features (but not the unapproved .4).
        var now = DateTime.UtcNow;
        var state = new VersionNotifier.State(now, "v2026.6.22.3", "2026.6.22.2");
        var (_, messages) = VersionNotifier.Run(state, notes, new Version("2026.6.22.3"), now, () => null);
        var text = string.Join("\n", messages.Select(m => m.Text));
        Assert.That(text, Does.Contain("cop init --checks"));
    }

    [Test]
    public void State_RoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "cop-state-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var when = new DateTime(2026, 6, 22, 10, 0, 0, DateTimeKind.Utc);
            VersionNotifier.WriteState(path, new VersionNotifier.State(when, "v2026.6.22.9", "2026.6.22.4"));
            var read = VersionNotifier.ReadState(path);

            Assert.That(read.LatestTag, Is.EqualTo("v2026.6.22.9"));
            Assert.That(read.SeenVersion, Is.EqualTo("2026.6.22.4"));
            Assert.That(read.LastCheck, Is.EqualTo(when));
        }
        finally { File.Delete(path); }
    }
}
