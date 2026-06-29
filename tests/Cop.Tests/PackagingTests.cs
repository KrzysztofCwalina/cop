using System.Security;
using Cop.Cli.Commands;
using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Guards packaging-resolution and restore-safety fixes:
/// - local <c>packages/</c> must shadow the global <c>~/.cop</c> cache (the stale-cache footgun);
/// - package-path traversal validation must not depend on the process working directory.
/// </summary>
[TestFixture]
public class PackagingTests
{
    [Test]
    public void GetFeedPaths_LocalPackages_ComeBeforeGlobalCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cop-feed-" + Guid.NewGuid().ToString("N")[..8]);
        var localPackages = Path.Combine(dir, "packages");
        Directory.CreateDirectory(localPackages);
        try
        {
            var feeds = PackageResolver.GetFeedPaths(dir);

            Assert.That(feeds, Is.Not.Empty);
            Assert.That(feeds[0], Is.EqualTo(localPackages),
                "local packages/ must be searched before the global ~/.cop cache so an in-repo package " +
                "shadows a possibly-stale auto-restored copy");

            // If the global cache exists on this machine, it must come AFTER the local dir.
            var cache = PackageResolver.GlobalCachePath;
            if (feeds.Contains(cache))
                Assert.That(feeds.IndexOf(localPackages), Is.LessThan(feeds.IndexOf(cache)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void ValidatePackagePath_RejectsTraversal_IndependentOfCwd()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "cop-base-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(baseDir);
        var prevCwd = Directory.GetCurrentDirectory();
        try
        {
            // A normal relative path is accepted and stays under baseDir.
            var ok = RestoreEngine.ValidatePackagePath("src/foo.cop", baseDir);
            Assert.That(Path.GetFullPath(ok), Does.StartWith(Path.GetFullPath(baseDir)));

            // Traversal is rejected.
            Assert.Throws<SecurityException>(() => RestoreEngine.ValidatePackagePath("../../etc/passwd", baseDir));

            // The check must not depend on the process CWD — set CWD to baseDir and re-check.
            Directory.SetCurrentDirectory(baseDir);
            Assert.Throws<SecurityException>(() => RestoreEngine.ValidatePackagePath("../../../etc", baseDir));
            Assert.DoesNotThrow(() => RestoreEngine.ValidatePackagePath("src/ok.cop", baseDir));
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            Directory.Delete(baseDir, recursive: true);
        }
    }
}
