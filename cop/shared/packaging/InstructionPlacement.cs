using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cop.Core;

/// <summary>
/// Places a package's natural-language instructions (<c>instructions/*.md</c>) into the
/// repository's <c>.github/instructions/{packageName}.instructions.md</c> file so that coding
/// agents (e.g. GitHub Copilot) pick them up.
///
/// The placed file gets a single <c>applyTo</c> YAML front-matter block (from the package's
/// <c>cop.json</c> <c>applyTo</c> field, defaulting to <c>**</c>) followed by the concatenated
/// bodies of the package's instruction files. Placement is idempotent: when the target already
/// contains identical content it is left untouched (so repeated auto-restores don't churn the
/// working tree).
///
/// Shared by both restore paths — <see cref="RestoreEngine"/> (explicit <c>cop package restore</c>)
/// and the CLI auto-restore path — so the two cannot diverge.
/// </summary>
public static class InstructionPlacement
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Builds the combined instruction content (front-matter + bodies) for the given files.
    /// Files are ordered by name for deterministic output. Any per-file front-matter is stripped
    /// so the package-level <paramref name="applyTo"/> is the single source of truth.
    /// </summary>
    public static string BuildContent(string applyTo, IReadOnlyList<(string FileName, string Content)> files)
    {
        var glob = string.IsNullOrWhiteSpace(applyTo) ? "**" : applyTo.Trim();

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("applyTo: '").Append(glob.Replace("'", "''")).Append("'\n");
        sb.Append("---\n\n");

        var bodies = files
            .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(f => StripFrontmatter(f.Content))
            .Where(b => b.Length > 0)
            .ToList();

        sb.Append(string.Join("\n\n", bodies));
        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Writes the combined instructions for a package to
    /// <c>{repoRoot}/.github/instructions/{packageName}.instructions.md</c>.
    /// Returns the target path (whether or not a write occurred), or <c>null</c> when there are no
    /// instruction files. The write is skipped when the existing file is byte-identical.
    /// </summary>
    /// <param name="wrote">True when the file was created or its content changed.</param>
    public static string? Place(
        string repoRoot,
        string packageName,
        string applyTo,
        IReadOnlyList<(string FileName, string Content)> files,
        out bool wrote)
    {
        wrote = false;
        if (files == null || files.Count == 0)
            return null;

        var instructionsDir = Path.Combine(repoRoot, ".github", "instructions");
        var targetPath = Path.Combine(instructionsDir, $"{packageName}.instructions.md");

        var content = BuildContent(applyTo, files);
        var bytes = Utf8NoBom.GetBytes(content);

        if (File.Exists(targetPath))
        {
            var existing = File.ReadAllBytes(targetPath);
            if (existing.AsSpan().SequenceEqual(bytes))
                return targetPath; // unchanged — leave the working tree alone
        }

        Directory.CreateDirectory(instructionsDir);
        File.WriteAllBytes(targetPath, bytes);
        wrote = true;
        return targetPath;
    }

    /// <summary>
    /// Convenience overload used by the auto-restore path: reads a package's <c>instructions/*.md</c>
    /// and <c>cop.json</c> from disk and places them. Returns the target path or <c>null</c> when the
    /// package has no instructions.
    /// </summary>
    public static string? PlaceFromPackageDir(string repoRoot, string packageDir, out bool wrote)
    {
        wrote = false;
        var instrDir = Path.Combine(packageDir, "instructions");
        if (!Directory.Exists(instrDir))
            return null;

        var mdFiles = Directory.GetFiles(instrDir, "*.md", SearchOption.TopDirectoryOnly);
        if (mdFiles.Length == 0)
            return null;

        var meta = PackageMetadata.TryLoadFromDirectory(packageDir);
        var name = meta?.Name;
        if (string.IsNullOrEmpty(name))
            name = Path.GetFileName(packageDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var applyTo = meta?.ApplyTo ?? string.Empty;

        var files = mdFiles
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();

        return Place(repoRoot, name!, applyTo, files, out wrote);
    }

    /// <summary>
    /// Removes a leading YAML front-matter block (delimited by <c>---</c> lines) from
    /// <paramref name="raw"/> and trims the remainder. Returns the content unchanged (trimmed)
    /// when there is no front-matter. Line endings are normalized to <c>\n</c>.
    /// </summary>
    public static string StripFrontmatter(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var text = raw.Replace("\r\n", "\n").Replace("\r", "\n").TrimStart('\uFEFF');
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            return text.Trim();

        // Find the closing '---' delimiter line.
        int close = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (close < 0)
            return text.Trim();

        int bodyStart = text.IndexOf('\n', close + 1);
        if (bodyStart < 0)
            return string.Empty; // closing delimiter at EOF, no body

        return text.Substring(bodyStart + 1).Trim();
    }
}
