using System.IO;

namespace Cop.Core;

/// <summary>
/// Helpers for installing restored packages into the package cache (~/.cop/packages/).
/// </summary>
public static class PackageInstaller
{
    /// <summary>
    /// Moves a freshly downloaded package from <paramref name="tempDir"/> into its final
    /// location <paramref name="pkgDir"/>.
    ///
    /// A package directory is only valid if it contains a manifest (cop.json). If
    /// <paramref name="pkgDir"/> already holds a valid package (e.g. a concurrent process
    /// placed it), the freshly downloaded copy is discarded. Otherwise — whether pkgDir is
    /// missing OR stale/incomplete (e.g. lib DLLs but no manifest) — it is replaced with the
    /// freshly downloaded copy. This prevents an incomplete cache directory from sticking
    /// around forever and causing "restored ok" but "package not found" loops.
    ///
    /// If the existing directory's files are locked (e.g. by another running cop process),
    /// the underlying Directory.Delete/Move throws so the caller can report an honest
    /// failure instead of a false "restored".
    /// </summary>
    public static void PlaceRestoredPackage(string tempDir, string pkgDir)
    {
        var existingIsValidPackage = Directory.Exists(pkgDir) &&
            File.Exists(Path.Combine(pkgDir, PackageMetadata.MetadataFileName));

        if (existingIsValidPackage)
        {
            // Another process already placed a complete package — keep theirs.
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            return;
        }

        if (Directory.Exists(pkgDir))
            Directory.Delete(pkgDir, recursive: true);
        Directory.Move(tempDir, pkgDir);
    }
}
